package ru.bytplast.tsdagent

import org.json.JSONArray
import org.json.JSONObject

object AgentTrace {
    private val notes = ArrayDeque<JSONObject>()
    @Volatile var updatePhase = "нет"
    @Volatile var screenPhase = "нет"

    fun note(area: String, text: String) {
        val item = JSONObject()
            .put("area", area)
            .put("text", text)
            .put("at", System.currentTimeMillis())
        synchronized(notes) {
            notes.addLast(item)
            while (notes.size > 40) notes.removeFirst()
        }
    }

    fun payload(): JSONArray = synchronized(notes) { JSONArray(notes.toList()) }
}
