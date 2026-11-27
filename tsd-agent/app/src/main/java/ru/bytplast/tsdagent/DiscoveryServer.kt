package ru.bytplast.tsdagent

import android.content.Context
import android.os.Build
import org.json.JSONObject
import java.net.InetSocketAddress
import java.net.ServerSocket

class DiscoveryServer(private val context: Context, private val config: AgentConfig) {
    @Volatile private var running = false
    private var socket: ServerSocket? = null

    fun start() {
        if (running) return
        running = true
        Thread({
            val server = ServerSocket()
            server.reuseAddress = true
            server.bind(InetSocketAddress(8741))
            socket = server
            while (running) {
                try {
                    server.accept().use { client ->
                        client.soTimeout = 3000
                        val input = client.getInputStream()
                        val magic = ByteArray(4)
                        readFully(input, magic)
                        if (!magic.contentEquals("TSD1".toByteArray())) return@use
                        val line = readLine(input)
                        val request = JSONObject(line)
                        if (request.optString("type") != "discover") return@use
                        val nonce = request.getString("nonce")
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
                            .put("signature", Identity.sign(nonce + "|" + config.deviceId))
                        client.getOutputStream().write((body.toString() + "\n").toByteArray())
                    }
                } catch (_: Exception) {
                    if (!running) break
                }
            }
        }, "tsd-discovery").start()
    }

    fun stop() {
        running = false
        socket?.close()
    }
}

internal fun readFully(input: java.io.InputStream, buffer: ByteArray) {
    var offset = 0
    while (offset < buffer.size) {
        val read = input.read(buffer, offset, buffer.size - offset)
        if (read < 0) throw java.io.EOFException()
        offset += read
    }
}

internal fun readLine(input: java.io.InputStream): String {
    val out = java.io.ByteArrayOutputStream()
    while (out.size() < 65536) {
        val b = input.read()
        if (b < 0 || b == '\n'.code) break
        if (b != '\r'.code) out.write(b)
    }
    return out.toString(Charsets.UTF_8.name())
}
