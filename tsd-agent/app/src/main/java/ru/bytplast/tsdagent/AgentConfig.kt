package ru.bytplast.tsdagent

import android.content.Context
import org.json.JSONObject

class AgentConfig(context: Context) {
    private val prefs = context.getSharedPreferences("agent", Context.MODE_PRIVATE)

    var server: String
        get() = prefs.getString("server", "") ?: ""
        set(value) = prefs.edit().putString("server", value.trim().trimEnd('/')).apply()

    var fingerprint: String
        get() = prefs.getString("fingerprint", "") ?: ""
        set(value) = prefs.edit().putString("fingerprint", normalize(value)).apply()

    var site: String
        get() = prefs.getString("site", "") ?: ""
        set(value) = prefs.edit().putString("site", value.trim()).apply()

    var deviceId: String
        get() = prefs.getString("deviceId", "") ?: ""
        set(value) = prefs.edit().putString("deviceId", value).apply()

    var certStamp: String
        get() = prefs.getString("certStamp", "") ?: ""
        set(value) = prefs.edit().putString("certStamp", value).apply()

    var certWanted: String
        get() = prefs.getString("certWanted", "") ?: ""
        set(value) = prefs.edit().putString("certWanted", value).apply()

    var askedBattery: Boolean
        get() = prefs.getBoolean("askedBattery", false)
        set(value) = prefs.edit().putBoolean("askedBattery", value).apply()

    val ready: Boolean
        get() = server.startsWith("https://") && fingerprint.length == 64 && deviceId.isNotEmpty()

    fun completedBlocks(): Set<String> = prefs.getStringSet("completed", emptySet()) ?: emptySet()

    fun rememberBlock(id: String) {
        val next = completedBlocks().toMutableSet()
        next.add(id)
        prefs.edit().putStringSet("completed", next).apply()
    }

    fun applyBootstrap(json: JSONObject) {
        server = json.getString("server")
        fingerprint = json.getString("fingerprint")
        site = json.optString("site", site)
    }

    companion object {
        fun normalize(value: String) = value.replace(":", "").replace(" ", "").uppercase()
    }
}
