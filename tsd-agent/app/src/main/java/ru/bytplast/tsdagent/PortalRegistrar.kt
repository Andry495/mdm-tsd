package ru.bytplast.tsdagent

import android.os.Build
import android.provider.Settings
import org.json.JSONObject
import java.net.URL
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.X509Certificate
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManager
import javax.net.ssl.X509ExtendedTrustManager

object AgentInfo {
    const val versionName = "1.0.26"
    const val versionCode = 27
}

class PortalRegistrar(private val context: android.content.Context, private val config: AgentConfig) {
    @Volatile private var running = false

    fun start() {
        if (running) return
        running = true
        Thread({
            while (running) {
                try {
                    AgentService.instance?.refreshLabel()
                    announce()
                    val work = waitForWork()
                    if (work != null) {
                        DeskWorker(context, config).handle(work)
                        if (work.optJSONObject("session") != null)
                            Thread.sleep(2_000)
                    }
                } catch (_: Exception) {
                    try {
                        Thread.sleep(5_000)
                    } catch (_: InterruptedException) {
                        break
                    }
                }
            }
        }, "tsd-register").start()
    }

    fun stop() {
        running = false
    }

    fun announce(): String {
        val nonce = (System.currentTimeMillis() / 1000).toString()
        val serial = try {
            Build.getSerial()
        } catch (_: SecurityException) {
            ""
        }
        val body = JSONObject()
            .put("type", "announce")
            .put("protocol", 1)
            .put("deviceId", config.deviceId)
            .put("serial", serial)
            .put("manufacturer", Build.MANUFACTURER)
            .put("name", deviceName(context))
            .put("model", Build.MODEL)
            .put("android", Build.VERSION.RELEASE)
            .put("agentName", AgentInfo.versionName)
            .put("agentCode", AgentInfo.versionCode)
            .put("publicKey", Identity.publicKey())
            .put("nonce", nonce)
            .put("signature", Identity.sign("$nonce|${config.deviceId}"))
        val connection = open(config.server.trimEnd('/') + "/api/agent/announce", config.fingerprint)
        connection.requestMethod = "POST"
        connection.doOutput = true
        connection.setRequestProperty("Content-Type", "application/json; charset=utf-8")
        connection.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
        val code = connection.responseCode
        val text = (if (code in 200..299) connection.inputStream else connection.errorStream)
            ?.bufferedReader()?.use { it.readText() }.orEmpty()
        if (code !in 200..299) {
            val error = try {
                JSONObject(text).optString("error")
            } catch (_: Exception) {
                ""
            }
            throw IllegalStateException(if (error.isNotBlank()) error else "портал ответил $code")
        }
        val state = try {
            JSONObject(text).optString("state")
        } catch (_: Exception) {
            ""
        }
        return when (state) {
            "pending" -> "Портал записал устройство и ждёт подтверждения."
            "managed" -> "Портал видит это устройство."
            "rejected" -> "Портал отклонил это устройство."
            else -> "Портал принял сообщение."
        }
    }

    private fun waitForWork(): JSONObject? {
        val info = context.packageManager.getPackageInfo(context.packageName, 0)
        val code = info.longVersionCode.toInt()
        val nonce = (System.currentTimeMillis() / 1000).toString()
        val body = JSONObject()
            .put("deviceId", config.deviceId)
            .put("nonce", nonce)
            .put("signature", Identity.sign("$nonce|${config.deviceId}"))
            .put("agentCode", code)
            .put("androidId", Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID) ?: "")
            .put("name", deviceName(context))
            .put("updatePhase", AgentTrace.updatePhase)
            .put("screenPhase", AgentTrace.screenPhase)
            .put("controlReady", AgentAccessibility.connected())
            .put("notes", AgentTrace.payload())
        val connection = open(config.server.trimEnd('/') + "/api/agent/wait", config.fingerprint)
        connection.requestMethod = "POST"
        connection.doOutput = true
        connection.readTimeout = 30000
        connection.setRequestProperty("Content-Type", "application/json; charset=utf-8")
        connection.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
        val codeHttp = connection.responseCode
        val text = (if (codeHttp in 200..299) connection.inputStream else connection.errorStream)
            ?.bufferedReader()?.use { it.readText() }.orEmpty()
        if (codeHttp !in 200..299) return null
        return JSONObject(text)
    }

    companion object {
        fun fetchBootstrap(server: String): JSONObject {
            var seen = ""
            val trust = object : X509ExtendedTrustManager() {
                override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String) = Unit
                override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String, socket: java.net.Socket?) = Unit
                override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String, engine: javax.net.ssl.SSLEngine?) = Unit
                override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String) {
                    seen = fingerprint(chain)
                }
                override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String, socket: java.net.Socket?) {
                    seen = fingerprint(chain)
                }
                override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String, engine: javax.net.ssl.SSLEngine?) {
                    seen = fingerprint(chain)
                }
                override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
                private fun fingerprint(chain: Array<out X509Certificate>): String {
                    if (chain.isEmpty()) throw java.security.cert.CertificateException("нет сертификата")
                    return MessageDigest.getInstance("SHA-256").digest(chain[0].encoded)
                        .joinToString("") { "%02X".format(it) }
                }
            }
            val context = SSLContext.getInstance("TLS")
            context.init(null, arrayOf<TrustManager>(trust), SecureRandom())
            val connection = URL(server.trimEnd('/') + "/store/bootstrap.json").openConnection() as HttpsURLConnection
            connection.sslSocketFactory = context.socketFactory
            connection.hostnameVerifier = HostnameVerifier { _, _ -> true }
            connection.connectTimeout = 10000
            connection.readTimeout = 10000
            val text = connection.inputStream.bufferedReader().use { it.readText() }
            val json = JSONObject(text)
            val printed = AgentConfig.normalize(json.getString("fingerprint"))
            if (printed.length != 64 || printed != seen)
                throw IllegalStateException("отпечаток портала не совпал с сертификатом")
            return json
        }

        private fun open(url: String, fingerprint: String): HttpsURLConnection {
            val connection = URL(url).openConnection() as HttpsURLConnection
            connection.sslSocketFactory = pinnedClient(fingerprint).socketFactory
            connection.hostnameVerifier = HostnameVerifier { _, _ -> true }
            connection.connectTimeout = 10000
            connection.readTimeout = 15000
            return connection
        }
    }
}

internal fun deviceName(context: android.content.Context): String {
    val named = Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME)
    if (!named.isNullOrBlank()) return named.trim()
    return Build.MODEL ?: ""
}
