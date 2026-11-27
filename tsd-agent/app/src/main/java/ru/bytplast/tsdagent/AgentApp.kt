package ru.bytplast.tsdagent

import android.app.Application
import java.io.File

class AgentApp : Application() {
    override fun onCreate() {
        super.onCreate()
        val file = File(filesDir, "crash.txt")
        if (file.exists()) {
            val text = file.readText().lineSequence().take(8).joinToString(" ").take(500)
            if (text.isNotBlank())
                AgentTrace.note("crash", text)
            file.delete()
        }
        Thread({
            try {
                DeviceOwner.claim(this)
            } catch (_: Exception) {
            }
        }, "device-owner").start()
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, error ->
            try {
                file.writeText(error.stackTraceToString().take(1500))
            } catch (_: Exception) {
            }
            previous?.uncaughtException(thread, error)
        }
    }
}
