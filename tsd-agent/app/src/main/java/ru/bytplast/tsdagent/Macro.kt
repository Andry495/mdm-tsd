package ru.bytplast.tsdagent

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.Intent
import android.graphics.Path
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import org.json.JSONObject
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class MacroPlayer(private val context: android.content.Context, private val config: AgentConfig) {
    fun run(message: JSONObject): JSONObject {
        val jobId = message.optString("jobId")
        val blockId = message.optString("blockId")
        val deviceType = message.optString("deviceType")
        if (deviceType.isNotEmpty() && deviceType != Identity.deviceType())
            return result(jobId, false, "device type mismatch", null)
        val service = AgentAccessibility.instance
            ?: return result(jobId, false, "accessibility not enabled", null)
        val steps = message.optJSONArray("steps") ?: return result(jobId, false, "нет шагов", null)
        for (index in 0 until steps.length()) {
            val step = steps.getJSONObject(index)
            val error = try {
                when (step.optString("op")) {
                    "launch" -> launch(step.optString("package"))
                    "wait" -> waitFor(service, step)
                    "tap" -> if (service.tap(step.optString("text"), step.optDouble("x", 0.5).toFloat(), step.optDouble("y", 0.5).toFloat())) null else "tap miss"
                    "swipe" -> if (service.swipe(step)) null else "swipe miss"
                    "text" -> if (service.typeText(step.optString("value"))) null else "нет поля ввода"
                    "key" -> if (service.key(step.optString("code"))) null else "клавиша"
                    "pause" -> { Thread.sleep(step.optLong("ms", step.optLong("timeoutMs", 500))); null }
                    else -> "неизвестный шаг"
                }
            } catch (ex: Exception) {
                ex.message ?: "шаг"
            }
            if (error != null)
                return result(jobId, false, error, index)
        }
        if (message.optBoolean("once") && blockId.isNotEmpty())
            config.rememberBlock(blockId)
        return result(jobId, true, "", null)
    }

    private fun launch(packageName: String): String? {
        val intent = context.packageManager.getLaunchIntentForPackage(packageName)
            ?: return "приложение не установлено"
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        context.startActivity(intent)
        return null
    }

    private fun waitFor(service: AgentAccessibility, step: JSONObject): String? {
        val text = step.optString("text")
        val timeout = step.optLong("timeoutMs", 10000)
        val until = System.currentTimeMillis() + timeout
        while (System.currentTimeMillis() < until) {
            if (text.isNotEmpty() && service.hasText(text)) return null
            Thread.sleep(300)
        }
        return "wait timeout"
    }

    private fun result(jobId: String, ok: Boolean, error: String, step: Int?): JSONObject {
        val json = JSONObject().put("type", "jobResult").put("jobId", jobId).put("ok", ok).put("error", error)
        if (step != null) json.put("failedStep", step)
        return json
    }
}

class AgentAccessibility : AccessibilityService() {
    override fun onServiceConnected() { instance = this }
    override fun onDestroy() { if (instance == this) instance = null }
    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}
    override fun onInterrupt() {}

    fun hasText(text: String): Boolean = onMain { rootInActiveWindow?.findAccessibilityNodeInfosByText(text)?.isNotEmpty() == true }

    fun tap(text: String, x: Float, y: Float): Boolean {
        if (text.isNotEmpty()) {
            val clicked = onMain {
                val nodes = rootInActiveWindow?.findAccessibilityNodeInfosByText(text).orEmpty()
                val node = nodes.firstOrNull { it.isVisibleToUser }
                node?.performAction(AccessibilityNodeInfo.ACTION_CLICK) == true ||
                    node?.parent?.performAction(AccessibilityNodeInfo.ACTION_CLICK) == true
            }
            if (clicked) return true
        }
        return gesture(x, y, x, y)
    }

    fun swipe(step: JSONObject): Boolean =
        gesture(step.optDouble("x1", 0.5).toFloat(), step.optDouble("y1", 0.8).toFloat(), step.optDouble("x2", 0.5).toFloat(), step.optDouble("y2", 0.2).toFloat())

    fun typeText(value: String): Boolean = onMain {
        val root = rootInActiveWindow ?: return@onMain false
        val focused = root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT)
        val target = focused ?: findEditable(root)
        if (target == null) return@onMain false
        val args = Bundle()
        args.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, value)
        target.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args)
    }

    fun key(code: String): Boolean = when (code) {
        "back" -> onMain { performGlobalAction(GLOBAL_ACTION_BACK) }
        "home" -> onMain { performGlobalAction(GLOBAL_ACTION_HOME) }
        "recents" -> onMain { performGlobalAction(GLOBAL_ACTION_RECENTS) }
        "enter" -> onMain {
            val node = rootInActiveWindow?.findFocus(AccessibilityNodeInfo.FOCUS_INPUT)
            node?.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id) == true
        }
        else -> false
    }

    private fun findEditable(node: AccessibilityNodeInfo): AccessibilityNodeInfo? {
        if (node.isEditable) return node
        for (i in 0 until node.childCount) {
            val child = node.getChild(i) ?: continue
            val found = findEditable(child)
            if (found != null) return found
        }
        return null
    }

    private fun realScreen(): Pair<Int, Int> {
        val metrics = android.util.DisplayMetrics()
        val display = getSystemService(android.hardware.display.DisplayManager::class.java)
            ?.getDisplay(android.view.Display.DEFAULT_DISPLAY)
        @Suppress("DEPRECATION")
        display?.getRealMetrics(metrics)
        if (metrics.widthPixels > 0 && metrics.heightPixels > 0)
            return metrics.widthPixels to metrics.heightPixels
        val fallback = resources.displayMetrics
        return fallback.widthPixels to fallback.heightPixels
    }

    private fun gesture(x1: Float, y1: Float, x2: Float, y2: Float): Boolean {
        val size = realScreen()
        val path = Path()
        val startX = x1 * size.first
        val startY = y1 * size.second
        var endX = x2 * size.first
        var endY = y2 * size.second
        if (startX == endX && startY == endY) endX += 1f
        path.moveTo(startX, startY)
        path.lineTo(endX, endY)
        val stroke = GestureDescription.StrokeDescription(path, 0, if (x1 == x2 && y1 == y2) 50 else 300)
        val gesture = GestureDescription.Builder().addStroke(stroke).build()
        val latch = CountDownLatch(1)
        var ok = false
        dispatchGesture(gesture, object : GestureResultCallback() {
            override fun onCompleted(gestureDescription: GestureDescription?) { ok = true; latch.countDown() }
            override fun onCancelled(gestureDescription: GestureDescription?) { latch.countDown() }
        }, null)
        latch.await(2, TimeUnit.SECONDS)
        return ok
    }

    private fun <T> onMain(block: () -> T): T {
        if (Looper.myLooper() == Looper.getMainLooper()) return block()
        val latch = CountDownLatch(1)
        val box = arrayOfNulls<Any>(1)
        var thrown: Throwable? = null
        Handler(Looper.getMainLooper()).post {
            try { box[0] = block() } catch (error: Throwable) { thrown = error }
            latch.countDown()
        }
        latch.await(5, TimeUnit.SECONDS)
        thrown?.let { throw it }
        @Suppress("UNCHECKED_CAST")
        return box[0] as T
    }

    companion object {
        @Volatile var instance: AgentAccessibility? = null
        fun connected() = instance != null
    }
}
