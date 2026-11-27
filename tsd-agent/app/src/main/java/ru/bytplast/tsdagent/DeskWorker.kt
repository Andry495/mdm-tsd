package ru.bytplast.tsdagent

import android.content.Context
import org.json.JSONObject
import java.net.URL
import java.util.concurrent.atomic.AtomicBoolean
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection

class DeskWorker(private val context: Context, private val config: AgentConfig) {
    fun handle(message: JSONObject) {
        val stamp = message.optString("certSha256")
        if (stamp.isNotEmpty())
            CertInstaller.remember(config, stamp)
        val update = message.optJSONObject("update")
        if (update != null) {
            val own = update.optString("packageName") == context.packageName
            if (own) {
                val code = update.optInt("versionCode")
                val installed = installedCode()
                AgentTrace.updatePhase = "получено $code"
                val jobId = update.optString("jobId")
                if (code <= installed) {
                    AgentTrace.note("update", "пропускаю $code: уже стоит $installed")
                    if (jobId.isNotEmpty())
                        postJob(jobId, false, "уже стоит версия $installed")
                    AgentTrace.updatePhase = "нет"
                    return
                }
                AgentTrace.note("update", "портал прислал обновление $code")
                if (DeviceOwner.isOwner(context) || OemInstall.present()) {
                    if (installing.compareAndSet(false, true)) {
                        Thread {
                            val error = try {
                                CommandServer(context, config).installLocal(update)
                            } catch (ex: Exception) {
                                ex.message ?: "обновление не запустилось"
                            }
                            if (error == null) return@Thread
                            installing.set(false)
                            if (!DeviceOwner.isOwner(context)) {
                                AgentTrace.note("update", error)
                                UpdateActivity.open(context, update)
                                return@Thread
                            }
                            AgentTrace.updatePhase = "ошибка"
                            AgentTrace.note("update", error)
                            if (jobId.isNotEmpty())
                                postJob(jobId, false, error)
                        }.start()
                    }
                    return
                }
                UpdateActivity.open(context, update)
                return
            }
            val error = CommandServer(context, config).installLocal(update)
            postJob(update.getString("jobId"), error == null, error ?: "")
        }
        val session = message.optJSONObject("session")
        if (session != null)
            ScreenStreamer.ensure(context, config, session)
        else
            ScreenStreamer.stop()
    }

    fun report(jobId: String, ok: Boolean, error: String) = postJob(jobId, ok, error)

    private fun postJob(jobId: String, ok: Boolean, error: String) {
        val nonce = (System.currentTimeMillis() / 1000).toString()
        val body = JSONObject()
            .put("deviceId", config.deviceId)
            .put("nonce", nonce)
            .put("signature", Identity.sign("$nonce|${config.deviceId}|$jobId"))
            .put("ok", ok)
            .put("error", error)
        val connection = open(config.server.trimEnd('/') + "/api/agent/jobs/$jobId")
        connection.requestMethod = "POST"
        connection.doOutput = true
        connection.setRequestProperty("Content-Type", "application/json; charset=utf-8")
        connection.outputStream.use { it.write(body.toString().toByteArray()) }
        connection.responseCode
        connection.disconnect()
    }

    private fun installedCode(): Long = try {
        context.packageManager.getPackageInfo(context.packageName, 0).longVersionCode
    } catch (_: Exception) {
        0
    }

    private fun open(url: String): HttpsURLConnection {
        val connection = URL(url).openConnection() as HttpsURLConnection
        connection.sslSocketFactory = pinnedClient(config.fingerprint).socketFactory
        connection.hostnameVerifier = HostnameVerifier { _, _ -> true }
        connection.connectTimeout = 10000
        connection.readTimeout = 20000
        return connection
    }

    companion object {
        private val installing = AtomicBoolean(false)
    }
}
