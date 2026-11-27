package ru.bytplast.tsdagent

import android.app.AppOpsManager
import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.Parcel
import android.os.Process
import android.os.UserManager
import android.provider.Settings
import java.io.File
import java.lang.reflect.InvocationTargetException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

object DeviceOwner {
    private val claimed = AtomicReference(false)

    fun isOwner(context: Context): Boolean {
        val dpm = context.getSystemService(DevicePolicyManager::class.java) ?: return false
        return dpm.isDeviceOwnerApp(context.packageName)
    }

    fun claim(context: Context) {
        if (!claimed.compareAndSet(false, true)) return
        val app = context.applicationContext
        if (isOwner(app)) {
            enableControl(app)
            AgentTrace.note("owner", "агент уже владелец устройства")
            return
        }
        val admin = ComponentName(app, TsdDeviceAdmin::class.java)
        val reasons = mutableListOf<String>()
        reasons += "платформа: " + reflectOwner(app, admin)
        if (!isOwner(app)) {
            reasons += "zebra: " + zebraOwner(app, admin)
        }
        if (!isOwner(app)) {
            reasons += "oem: " + OemInstall.tryOwner(admin)
        }
        if (isOwner(app)) {
            enableControl(app)
            AgentTrace.note("owner", "агент назначен владельцем устройства")
            return
        }
        AgentTrace.note("owner", "владелец при установке не назначен. " + reasons.joinToString(" "))
    }

    fun enableControl(context: Context) {
        if (!isOwner(context)) return
        try {
            enable(context)
        } catch (_: Exception) {
        }
    }

    private fun reflectOwner(context: Context, admin: ComponentName): String {
        val dpm = context.getSystemService(DevicePolicyManager::class.java) ?: return "нет службы политики"
        val methods = dpm.javaClass.methods.filter { it.name == "setDeviceOwner" }
        if (methods.isEmpty()) return "метода нет"
        var last = "нет подходящей сигнатуры"
        for (method in methods) {
            val values = ownerArgs(method.parameterTypes, admin) ?: continue
            try {
                method.isAccessible = true
                val ok = method.invoke(dpm, *values) as? Boolean
                last = if (ok == true) "назначен" else "отказ"
                if (ok == true) return last
            } catch (ex: InvocationTargetException) {
                last = short(ex.targetException)
            } catch (ex: Exception) {
                last = short(ex)
            }
        }
        return last
    }

    private fun ownerArgs(types: Array<Class<*>>, admin: ComponentName): Array<Any?>? {
        if (types.size == 2 && types[0] == ComponentName::class.java && types[1] == String::class.java) {
            return arrayOf(admin, "Агент ТСД")
        }
        if (types.size == 3 && types[0] == ComponentName::class.java && types[1] == String::class.java && types[2] == Int::class.javaPrimitiveType) {
            return arrayOf(admin, "Агент ТСД", 0)
        }
        return null
    }

    private fun zebraOwner(context: Context, admin: ComponentName): String {
        val attempts = listOf(
            Zebra("com.symbol.osx.proxyframework.IDeviceAdminService", "com.symbol.osx.proxyframework", 1),
            Zebra("com.symbol.mxmf.proxyframework.IDeviceAdminService", "com.symbol.mxmf.proxyframework", 4)
        )
        val results = mutableListOf<String>()
        for (attempt in attempts) {
            val result = bindAndSet(context, attempt, admin)
            results += result
            if (isOwner(context)) return results.joinToString("; ")
        }
        return results.joinToString("; ")
    }

    private fun bindAndSet(context: Context, zebra: Zebra, admin: ComponentName): String {
        val connected = CountDownLatch(1)
        val remote = AtomicReference<IBinder?>(null)
        val early = AtomicReference<String?>(null)
        val connection = object : ServiceConnection {
            override fun onServiceConnected(name: ComponentName?, service: IBinder?) {
                remote.set(service)
                connected.countDown()
            }

            override fun onServiceDisconnected(name: ComponentName?) {
            }
        }
        val intent = Intent(zebra.action).setPackage(zebra.pkg)
        val posted = CountDownLatch(1)
        Handler(Looper.getMainLooper()).post {
            try {
                val bound = context.bindService(intent, connection, Context.BIND_AUTO_CREATE)
                if (!bound) {
                    early.set("сервис не открылся")
                    connected.countDown()
                }
            } catch (ex: Exception) {
                early.set(short(ex))
                connected.countDown()
            } finally {
                posted.countDown()
            }
        }
        if (!posted.await(3, TimeUnit.SECONDS)) return "поток интерфейса не принял вызов"
        if (!connected.await(4, TimeUnit.SECONDS)) {
            unbind(context, connection)
            return early.get() ?: "сервис не ответил"
        }
        early.get()?.let { return it }
        val binder = remote.get() ?: return "пустой ответ сервиса"
        val result = transact(binder, zebra.action, zebra.code, admin)
        unbind(context, connection)
        return result
    }

    private fun transact(remote: IBinder, descriptor: String, code: Int, admin: ComponentName): String {
        val data = Parcel.obtain()
        val reply = Parcel.obtain()
        return try {
            data.writeInterfaceToken(descriptor)
            data.writeInt(1)
            admin.writeToParcel(data, 0)
            if (!remote.transact(code, data, reply, 0)) return "транзакция отклонена"
            reply.readException()
            if (reply.readInt() != 0) "назначен" else "сервис вернул отказ"
        } catch (ex: Exception) {
            short(ex)
        } finally {
            reply.recycle()
            data.recycle()
        }
    }

    private fun unbind(context: Context, connection: ServiceConnection) {
        try {
            context.unbindService(connection)
        } catch (_: Exception) {
        }
    }

    private fun short(error: Throwable): String {
        val text = error.message?.lineSequence()?.firstOrNull()?.trim().orEmpty()
        if (text.isEmpty()) return error.javaClass.simpleName
        return text.take(160)
    }

    private fun enable(context: Context) {
        val flat = ComponentName(context, AgentAccessibility::class.java).flattenToString()
        val current = Settings.Secure.getString(context.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES).orEmpty()
        val next = if (current.split(':').any { it.equals(flat, ignoreCase = true) }) current
        else listOf(current, flat).filter { it.isNotBlank() }.joinToString(":")
        Settings.Secure.putString(context.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES, next)
        Settings.Secure.putInt(context.contentResolver, Settings.Secure.ACCESSIBILITY_ENABLED, 1)
        allowInstall(context)
    }

    fun canInstall(context: Context): Boolean =
        isOwner(context) || OemInstall.present() || context.packageManager.canRequestPackageInstalls()

    private fun allowInstall(context: Context) {
        val dpm = context.getSystemService(DevicePolicyManager::class.java) ?: return
        val admin = ComponentName(context, TsdDeviceAdmin::class.java)
        for (restriction in listOf(
            UserManager.DISALLOW_INSTALL_APPS,
            UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES,
            UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES_GLOBALLY
        )) {
            try {
                dpm.clearUserRestriction(admin, restriction)
            } catch (_: Exception) {
            }
        }
        try {
            val appOps = context.getSystemService(AppOpsManager::class.java) ?: return
            val setMode = appOps.javaClass.getMethod(
                "setMode",
                Int::class.javaPrimitiveType,
                Int::class.javaPrimitiveType,
                String::class.java,
                Int::class.javaPrimitiveType
            )
            setMode.invoke(appOps, 66, Process.myUid(), context.packageName, AppOpsManager.MODE_ALLOWED)
        } catch (_: Exception) {
        }
    }

    private data class Zebra(val action: String, val pkg: String, val code: Int)
}

object OemInstall {
    private val classes = listOf(
        "android.device.DeviceManager",
        "com.chainway.deviceapi.DeviceManager"
    )

    fun present(): Boolean = classes.any { load(it) != null }

    fun tryOwner(admin: ComponentName): String {
        val notes = mutableListOf<String>()
        var found = false
        for (name in classes) {
            val type = load(name) ?: continue
            found = true
            val instance = instance(type)
            if (instance == null) {
                notes += type.simpleName + ": не создался"
                continue
            }
            val methods = type.methods.filter { it.name == "setDeviceOwner" || it.name == "setDeviceOwnerApp" }
            if (methods.isEmpty()) {
                notes += type.simpleName + ": метода владельца нет"
                continue
            }
            for (method in methods) {
                val args = argsFor(method.parameterTypes, admin) ?: continue
                try {
                    method.isAccessible = true
                    val result = method.invoke(instance, *args)
                    if (result as? Boolean != false) return type.simpleName + ": назначен"
                    notes += type.simpleName + ": отказ"
                } catch (ex: InvocationTargetException) {
                    notes += type.simpleName + ": " + message(ex.targetException)
                } catch (ex: Exception) {
                    notes += type.simpleName + ": " + message(ex)
                }
            }
        }
        return if (found) notes.joinToString("; ") else "класса нет"
    }

    fun tryInstall(context: Context, source: File): Boolean? {
        val staged = stage(context, source)
        val paths = listOf(staged.absolutePath, "file://" + staged.absolutePath, source.absolutePath).distinct()
        var seen = false
        for (name in classes) {
            val type = load(name) ?: continue
            seen = true
            val instance = instance(type) ?: continue
            val method = try {
                type.getMethod("installApplication", String::class.java)
            } catch (_: NoSuchMethodException) {
                continue
            }
            for (path in paths) {
                try {
                    if (method.invoke(instance, path) as? Boolean == true) return true
                } catch (_: Exception) {
                }
            }
        }
        return if (seen) false else null
    }

    private fun stage(context: Context, source: File): File {
        val dir = context.getExternalFilesDir(null) ?: return source
        val copy = File(dir, source.name)
        return try {
            source.copyTo(copy, overwrite = true)
            copy.setReadable(true, false)
            copy
        } catch (_: Exception) {
            source
        }
    }

    private fun instance(type: Class<*>): Any? {
        try {
            return type.getDeclaredConstructor().newInstance()
        } catch (_: Exception) {
        }
        return try {
            type.getMethod("getInstance").invoke(null)
        } catch (_: Exception) {
            null
        }
    }

    private fun load(name: String): Class<*>? = try {
        Class.forName(name)
    } catch (_: ClassNotFoundException) {
        null
    }

    private fun argsFor(types: Array<Class<*>>, admin: ComponentName): Array<Any?>? = when {
        types.size == 1 && types[0] == ComponentName::class.java -> arrayOf(admin)
        types.size == 1 && types[0] == String::class.java -> arrayOf(admin.flattenToString())
        types.size == 2 && types[0] == ComponentName::class.java && types[1] == String::class.java -> arrayOf(admin, "Агент ТСД")
        else -> null
    }

    private fun message(error: Throwable): String {
        val text = error.message?.lineSequence()?.firstOrNull()?.trim().orEmpty()
        return if (text.isEmpty()) error.javaClass.simpleName else text.take(160)
    }
}
