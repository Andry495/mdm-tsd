package ru.bytplast.tsdagent

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder

class BootReceiver : android.content.BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val action = intent.action
        if (action != Intent.ACTION_BOOT_COMPLETED && action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        if (!AgentConfig(context).ready) return
        try {
            context.startForegroundService(Intent(context, AgentService::class.java))
        } catch (_: Exception) {
        }
    }
}

class AgentService : Service() {
    private var discovery: DiscoveryServer? = null
    private var commands: CommandServer? = null
    private var registrar: PortalRegistrar? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        instance = this
        val config = AgentConfig(this)
        if (!config.ready) {
            stopSelf()
            return START_NOT_STICKY
        }
        try {
            startInForeground(holdingProjection, false)
        } catch (_: Exception) {
            holdingProjection = false
            startInForeground(false, false)
        }
        DeviceOwner.enableControl(this)
        if (DeviceOwner.isOwner(this) && CertInstaller.pending(config)) {
            Thread {
                try {
                    CertInstaller.prepare(this, config)
                } catch (_: Exception) {
                }
            }.start()
        }
        if (discovery == null) {
            Identity.ensure(config)
            discovery = DiscoveryServer(this, config).also { it.start() }
            commands = CommandServer(this, config).also { it.start() }
            registrar = PortalRegistrar(this, config).also { it.start() }
        }
        return START_STICKY
    }

    fun promoteProjection(live: Boolean) {
        streaming = live
        try {
            startInForeground(true, live)
            holdingProjection = true
        } catch (ex: Exception) {
            holdingProjection = false
            streaming = false
            AgentTrace.note("screen", "службу съёмки система не пустила: " + (ex.message ?: ex.javaClass.simpleName))
            try {
                startInForeground(false, false)
            } catch (_: Exception) {
            }
        }
    }

    fun releaseProjection() {
        holdingProjection = false
        streaming = false
        startInForeground(false, false)
    }

    fun refreshLabel() {
        val name = deviceName(this)
        if (name == shownName) return
        startInForeground(holdingProjection, streaming)
    }

    override fun onDestroy() {
        if (instance == this) instance = null
        discovery?.stop()
        commands?.stop()
        registrar?.stop()
        super.onDestroy()
    }

    private fun startInForeground(projection: Boolean, live: Boolean) {
        val manager = getSystemService(NotificationManager::class.java)
        val channelId = "agent"
        manager.createNotificationChannel(NotificationChannel(channelId, "Агент ТСД", NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, BootstrapActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val name = deviceName(this)
        shownName = name
        val text = when {
            projection && live -> "Показывает экран порталу"
            projection -> "Разрешение на экран сохранено"
            else -> "Сообщает порталу, что устройство включено"
        }
        val notification = Notification.Builder(this, channelId)
            .setSmallIcon(android.R.drawable.stat_notify_sync)
            .setContentTitle(name)
            .setContentText(text)
            .setSubText("Агент ТСД")
            .setContentIntent(open)
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= 34) {
            val type = ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE or
                (if (projection) ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION else 0)
            startForeground(1, notification, type)
        } else if (projection)
            startForeground(1, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        else
            startForeground(1, notification)
    }

    companion object {
        @Volatile var instance: AgentService? = null
        @Volatile var holdingProjection = false
        @Volatile var streaming = false
        @Volatile var shownName: String? = null
    }
}
