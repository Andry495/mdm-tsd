package ru.bytplast.tsdagent

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import org.json.JSONObject

class UpdateActivity : AppCompatActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        showing = true
        setContentView(R.layout.update)
        if (showInstaller(intent)) return
        val status = findViewById<TextView>(R.id.status)
        val offered = intent.getIntExtra("versionCode", 0)
        val installed = try {
            packageManager.getPackageInfo(packageName, 0).longVersionCode
        } catch (_: Exception) {
            0L
        }
        if (offered > 0 && offered.toLong() <= installed) {
            status.text = "Эта версия уже стоит."
            AgentTrace.note("update", "окно обновления закрыто: уже стоит $installed")
            finish()
            return
        }
        val jobId = intent.getStringExtra("jobId").orEmpty()
        val update = JSONObject()
            .put("jobId", jobId)
            .put("packageName", packageName)
            .put("sha256", intent.getStringExtra("sha256").orEmpty())
            .put("url", intent.getStringExtra("url").orEmpty())
            .put("versionCode", intent.getIntExtra("versionCode", 0))
        status.text = "Скачиваю обновление агента…"
        AgentTrace.updatePhase = "скачивание"
        AgentTrace.note("update", "скачиваю пакет")
        Thread {
            val error = try {
                CommandServer(this, AgentConfig(this)).installLocal(update)
            } catch (ex: Exception) {
                ex.message ?: "обновление не запустилось"
            }
            if (error != null && jobId.isNotEmpty()) {
                try {
                    DeskWorker(this, AgentConfig(this)).report(jobId, false, error)
                } catch (ex: Exception) {
                    AgentTrace.note("update", "отчёт об ошибке не ушёл: " + (ex.message ?: ex.javaClass.simpleName))
                }
            }
            runOnUiThread {
                if (isFinishing) return@runOnUiThread
                if (error == null) {
                    status.text = "Подтвердите установку в системном окне. Новая версия сама сообщит порталу, что встала."
                } else {
                    status.text = error
                    if (error.contains("установку пакетов")) {
                        startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, android.net.Uri.parse("package:$packageName")))
                    }
                }
            }
        }.start()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        showInstaller(intent)
    }

    private fun showInstaller(source: Intent): Boolean {
        if (!source.hasExtra(PackageInstaller.EXTRA_STATUS)) return false
        val status = findViewById<TextView>(R.id.status)
        val code = source.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        val sessionId = source.getIntExtra(PackageInstaller.EXTRA_SESSION_ID, -1)
        if (code == PackageInstaller.STATUS_PENDING_USER_ACTION) {
            val confirm = try {
                if (Build.VERSION.SDK_INT >= 33)
                    source.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java)
                else
                    @Suppress("DEPRECATION") source.getParcelableExtra(Intent.EXTRA_INTENT)
            } catch (ex: Exception) {
                AgentTrace.note("update", "системное окно не прочиталось: " + (ex.message ?: ex.javaClass.simpleName))
                null
            }
            status.text = "Подтвердите установку в системном окне."
            AgentTrace.updatePhase = "ожидание подтверждения"
            AgentTrace.note("update", "открыто системное окно установки")
            if (confirm != null) window.decorView.post {
                try {
                    startActivity(confirm)
                } catch (ex: Exception) {
                    status.text = ex.message ?: "системное окно установки не открылось"
                    AgentTrace.note("update", "окно установки не открылось: " + status.text)
                }
            }
            return true
        }
        val message = source.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
        val failure = if (code == PackageInstaller.STATUS_SUCCESS) null else message ?: "установка отклонена"
        Installs.finish(sessionId, failure)
        AgentTrace.updatePhase = if (failure == null) "установлено" else "ошибка"
        AgentTrace.note("update", failure ?: "система приняла установку")
        status.text = failure ?: "Установка запущена."
        return true
    }

    override fun onDestroy() {
        showing = false
        super.onDestroy()
    }

    companion object {
        @Volatile var showing = false

        fun open(context: Context, update: JSONObject) {
            if (showing) return
            val intent = Intent(context, UpdateActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                .putExtra("jobId", update.optString("jobId"))
                .putExtra("sha256", update.optString("sha256"))
                .putExtra("url", update.optString("url"))
                .putExtra("versionCode", update.optInt("versionCode"))
            val pending = PendingIntent.getActivity(
                context,
                7,
                intent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )
            val manager = context.getSystemService(NotificationManager::class.java)
            val channelId = "update"
            manager.createNotificationChannel(NotificationChannel(channelId, "Обновление агента", NotificationManager.IMPORTANCE_HIGH))
            val notification = Notification.Builder(context, channelId)
                .setSmallIcon(android.R.drawable.stat_sys_download)
                .setContentTitle("Обновление агента")
                .setContentText("Портал прислал новую версию. Нажмите, чтобы установить.")
                .setContentIntent(pending)
                .setFullScreenIntent(pending, true)
                .setAutoCancel(true)
                .build()
            manager.notify(7, notification)
            try {
                context.startActivity(intent)
            } catch (ex: Exception) {
                AgentTrace.note("update", "окно обновления не открылось: " + (ex.message ?: ex.javaClass.simpleName))
            }
        }
    }
}
