package ru.bytplast.tsdagent

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import android.os.Build
import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.InputStream
import java.io.OutputStream
import java.math.BigInteger
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.URL
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.Date
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.X509ExtendedTrustManager

class CommandServer(private val context: Context, private val config: AgentConfig) {
    @Volatile private var running = false
    private var socket: ServerSocket? = null

    fun start() {
        if (running) return
        running = true
        Thread({
            val server = listen()
            socket = server
            while (running) {
                try {
                    val client = server.accept() as javax.net.ssl.SSLSocket
                    client.soTimeout = 20000
                    Thread({ handle(client) }, "tsd-command").start()
                } catch (_: Exception) {
                    if (!running) break
                }
            }
        }, "tsd-command-accept").start()
    }

    fun stop() {
        running = false
        socket?.close()
    }

    private fun listen(): SSLServerSocket {
        val store = serverStore()
        val keys = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm())
        keys.init(store, charArrayOf())
        val context = SSLContext.getInstance("TLS")
        context.init(keys.keyManagers, arrayOf<TrustManager>(pinTrust(config.fingerprint)), SecureRandom())
        val factory = context.serverSocketFactory
        val server = factory.createServerSocket() as SSLServerSocket
        server.needClientAuth = true
        server.reuseAddress = true
        server.bind(InetSocketAddress(8742))
        return server
    }

    private fun handle(socket: javax.net.ssl.SSLSocket) {
        socket.use { client ->
            try {
                client.startHandshake()
                val input = client.inputStream
                val output = client.outputStream
                while (true) {
                    val (type, payload) = readFrame(input)
                    if (type != 1) continue
                    val message = JSONObject(String(payload, Charsets.UTF_8))
                    when (message.optString("type")) {
                        "hello" -> {
                            val nonce = message.getString("nonce")
                            writeJson(output, JSONObject()
                                .put("type", "helloOk")
                                .put("deviceId", config.deviceId)
                                .put("signature", Identity.sign(nonce + "|" + config.deviceId)))
                        }
                        "getReport" -> writeJson(output, report())
                        "install" -> install(message, output)
                        "runMacro" -> writeJson(output, MacroPlayer(context, config).run(message))
                        "startSession" -> writeJson(output, JSONObject()
                            .put("type", "error")
                            .put("error", "session permission required"))
                        "stopSession" -> writeJson(output, JSONObject()
                            .put("type", "sessionStopped")
                            .put("sessionId", message.optString("sessionId")))
                        else -> writeJson(output, JSONObject().put("type", "error").put("error", "unknown"))
                    }
                }
            } catch (_: Exception) {
            }
        }
    }

    private fun report(): JSONObject {
        val packages = JSONArray()
        val pm = context.packageManager
        try {
            val installed = if (Build.VERSION.SDK_INT >= 33)
                pm.getInstalledPackages(PackageManager.PackageInfoFlags.of(0))
            else
                @Suppress("DEPRECATION") pm.getInstalledPackages(0)
            for (info in installed) {
                val code = info.longVersionCode.toInt()
                packages.put(JSONObject().put("packageName", info.packageName).put("versionCode", code))
            }
        } catch (_: Exception) {
            val info = pm.getPackageInfo(context.packageName, 0)
            val code = info.longVersionCode.toInt()
            packages.put(JSONObject().put("packageName", context.packageName).put("versionCode", code))
        }
        val self = pm.getPackageInfo(context.packageName, 0)
        val agentCode = self.longVersionCode.toInt()
        val canInstall = DeviceOwner.canInstall(context)
        val blocks = JSONArray()
        config.completedBlocks().forEach { blocks.put(it) }
        return JSONObject()
            .put("type", "report")
            .put("agentCode", agentCode)
            .put("agentName", self.versionName ?: agentCode.toString())
            .put("packages", packages)
            .put("completedBlocks", blocks)
            .put("publicationUrl", "")
            .put("scannerReady", false)
            .put("keyboardReady", AgentAccessibility.connected())
            .put("lastError", if (canInstall) "" else "нет права устанавливать пакеты")
    }

    fun installLocal(message: JSONObject): String? {
        val quiet = DeviceOwner.isOwner(context) || OemInstall.present()
        if (!quiet && !DeviceOwner.canInstall(context))
            return "нужно разрешить установку пакетов"
        val packageName = message.optString("packageName")
        val expected = message.optString("sha256")
        val url = message.getString("url")
        val versionId = url.trimEnd('/').substringAfterLast('/')
        val file = File(context.cacheDir, "$versionId.apk")
        try {
            download(url, versionId, expected, file)
            val digest = sha256(file)
            if (!digest.equals(expected, ignoreCase = true)) {
                file.delete()
                return "sha256 mismatch"
            }
            if (!DeviceOwner.isOwner(context)) {
                when (OemInstall.tryInstall(context, file)) {
                    true -> return null
                    false -> if (!DeviceOwner.canInstall(context)) return "производитель не поставил пакет"
                    null -> if (!DeviceOwner.canInstall(context)) return "нужно разрешить установку пакетов"
                }
            }
            if (!DeviceOwner.canInstall(context))
                return "нужно разрешить установку пакетов"
            return commitInstall(file, message.optBoolean("allowDowngrade"))
        } catch (ex: Exception) {
            file.delete()
            return ex.message ?: "install failed"
        }
    }

    private fun install(message: JSONObject, output: OutputStream) {
        val jobId = message.optString("jobId")
        val own = message.optString("packageName") == context.packageName
        val error = installLocal(message)
        if (own && error == null)
            return
        writeJson(output, jobResult(jobId, error == null, error ?: ""))
    }

    private fun download(url: String, versionId: String, sha256: String, file: File) {
        val connection = URL(url).openConnection() as HttpsURLConnection
        connection.sslSocketFactory = pinnedClient(config.fingerprint).socketFactory
        connection.hostnameVerifier = javax.net.ssl.HostnameVerifier { _, _ -> true }
        connection.setRequestProperty("X-Device-Id", config.deviceId)
        connection.setRequestProperty("X-Signature", Identity.sign("${config.deviceId}|$versionId|$sha256"))
        connection.connectTimeout = 15000
        connection.readTimeout = 120000
        val code = connection.responseCode
        if (code !in 200..299)
            throw IllegalStateException("скачивание $code")
        connection.inputStream.use { input -> file.outputStream().use { input.copyTo(it) } }
    }

    private fun installIntentFlags(): Int {
        val base = PendingIntent.FLAG_UPDATE_CURRENT
        return if (Build.VERSION.SDK_INT >= 31) base or PendingIntent.FLAG_MUTABLE else base
    }

    private fun commitInstall(file: File, allowDowngrade: Boolean): String? {
        val installer = context.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
        if (Build.VERSION.SDK_INT >= 31)
            params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
        val sessionId = installer.createSession(params)
        installer.openSession(sessionId).use { session ->
            session.openWrite("apk", 0, file.length()).use { out ->
                file.inputStream().use { it.copyTo(out) }
                session.fsync(out)
            }
            val intent = Intent(context, InstallResultReceiver::class.java)
            val pending = PendingIntent.getBroadcast(
                context,
                sessionId,
                intent,
                installIntentFlags()
            )
            Installs.prepare(sessionId)
            session.commit(pending.intentSender)
        }
        return Installs.await(sessionId)
    }

    private fun serverStore(): KeyStore {
        val file = File(context.filesDir, "command.p12")
        if (file.exists()) {
            val store = KeyStore.getInstance("PKCS12")
            file.inputStream().use { store.load(it, charArrayOf()) }
            return store
        }
        val keys = KeyPairGenerator.getInstance("RSA").apply { initialize(2048) }.generateKeyPair()
        val now = Date()
        val later = Date(now.time + 3650L * 24 * 60 * 60 * 1000)
        val holder = X500Name("CN=tsd-agent")
        val builder = JcaX509v3CertificateBuilder(holder, BigInteger.valueOf(now.time), now, later, holder, keys.public)
        val signer = JcaContentSignerBuilder("SHA256withRSA").build(keys.private)
        val cert = JcaX509CertificateConverter().getCertificate(builder.build(signer))
        val store = KeyStore.getInstance("PKCS12")
        store.load(null, null)
        store.setKeyEntry("agent", keys.private, charArrayOf(), arrayOf(cert))
        file.outputStream().use { store.store(it, charArrayOf()) }
        return store
    }
}

private fun jobResult(jobId: String, ok: Boolean, error: String) =
    JSONObject().put("type", "jobResult").put("jobId", jobId).put("ok", ok).put("error", error)

private fun writeJson(output: OutputStream, json: JSONObject) {
    val payload = json.toString().toByteArray(Charsets.UTF_8)
    val length = payload.size + 1
    val frame = ByteArray(4 + length)
    frame[0] = (length shr 24).toByte()
    frame[1] = (length shr 16).toByte()
    frame[2] = (length shr 8).toByte()
    frame[3] = length.toByte()
    frame[4] = 1
    payload.copyInto(frame, 5)
    output.write(frame)
    output.flush()
}

private fun readFrame(input: InputStream): Pair<Int, ByteArray> {
    val header = ByteArray(4)
    readFully(input, header)
    val length = ((header[0].toInt() and 0xff) shl 24) or
        ((header[1].toInt() and 0xff) shl 16) or
        ((header[2].toInt() and 0xff) shl 8) or
        (header[3].toInt() and 0xff)
    if (length < 1 || length > 2_000_000) throw IllegalStateException("кадр")
    val body = ByteArray(length)
    readFully(input, body)
    return (body[0].toInt() and 0xff) to body.copyOfRange(1, body.size)
}

private fun sha256(file: File): String {
    val digest = MessageDigest.getInstance("SHA-256")
    file.inputStream().use { input ->
        val buffer = ByteArray(8192)
        while (true) {
            val read = input.read(buffer)
            if (read < 0) break
            digest.update(buffer, 0, read)
        }
    }
    return digest.digest().joinToString("") { "%02x".format(it) }
}

internal fun pinTrust(expected: String) = object : X509ExtendedTrustManager() {
    override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String) = check(chain)
    override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String, socket: java.net.Socket?) = check(chain)
    override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String, engine: javax.net.ssl.SSLEngine?) = check(chain)
    override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String) = check(chain)
    override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String, socket: java.net.Socket?) = check(chain)
    override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String, engine: javax.net.ssl.SSLEngine?) = check(chain)
    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    private fun check(chain: Array<out X509Certificate>) {
        if (chain.isEmpty()) throw CertificateException("нет сертификата")
        val got = MessageDigest.getInstance("SHA-256").digest(chain[0].encoded).joinToString("") { "%02X".format(it) }
        if (got != AgentConfig.normalize(expected)) throw CertificateException("отпечаток")
    }
}

internal fun pinnedClient(expected: String): SSLContext {
    val context = SSLContext.getInstance("TLS")
    context.init(null, arrayOf<TrustManager>(pinTrust(expected)), SecureRandom())
    return context
}

object Installs {
    private val waits = ConcurrentHashMap<Int, Wait>()

    fun prepare(sessionId: Int) {
        waits[sessionId] = Wait()
    }

    fun await(sessionId: Int): String? {
        val wait = waits[sessionId] ?: return "нет сессии установки"
        wait.latch.await(2, TimeUnit.MINUTES)
        waits.remove(sessionId)
        return wait.error
    }

    fun finish(sessionId: Int, error: String?) {
        val wait = waits[sessionId] ?: return
        wait.error = error
        wait.latch.countDown()
    }

    private class Wait {
        val latch = CountDownLatch(1)
        @Volatile var error: String? = "время подтверждения установки вышло"
    }
}

class InstallResultReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        val sessionId = intent.getIntExtra(PackageInstaller.EXTRA_SESSION_ID, -1)
        if (status == PackageInstaller.STATUS_PENDING_USER_ACTION) {
            val confirm = try {
                if (Build.VERSION.SDK_INT >= 33)
                    intent.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java)
                else
                    @Suppress("DEPRECATION") intent.getParcelableExtra(Intent.EXTRA_INTENT)
            } catch (_: Exception) {
                null
            }
            if (confirm == null) {
                Installs.finish(sessionId, "система запросила подтверждение установки")
                return
            }
            confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            try {
                context.startActivity(confirm)
            } catch (ex: Exception) {
                Installs.finish(sessionId, ex.message ?: "окно установки не открылось")
            }
            return
        }
        val message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
        Installs.finish(sessionId, if (status == PackageInstaller.STATUS_SUCCESS) null else message ?: "установка отклонена")
    }
}
