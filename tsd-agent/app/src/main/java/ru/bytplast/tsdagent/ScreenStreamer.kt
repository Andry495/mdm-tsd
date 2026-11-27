package ru.bytplast.tsdagent

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.Image
import android.media.ImageReader
import android.util.DisplayMetrics
import android.view.Display
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionConfig
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.net.URL
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection

object ScreenStreamer {
    @Volatile private var sessionId: String? = null
    @Volatile private var running = false
    @Volatile private var asking = false
    @Volatile private var declined: String? = null
    private var projection: MediaProjection? = null
    private var display: VirtualDisplay? = null
    @Volatile private var reader: ImageReader? = null
    private var host: java.lang.ref.WeakReference<Activity>? = null
    private val surfaceLock = Any()
    private var captureWidth = 0
    private var captureHeight = 0
    private var displayManager: DisplayManager? = null
    private var displayListener: DisplayManager.DisplayListener? = null
    private var fitHandler: Handler? = null
    private var boundContext: Context? = null
    private var boundProjection: MediaProjection? = null
    private var boundWidth = 720
    private val refit = Runnable {
        val context = boundContext ?: return@Runnable
        val granted = boundProjection ?: return@Runnable
        if (!running) return@Runnable
        mountSurface(context, granted, boundWidth)
    }

    fun ensure(context: Context, config: AgentConfig, session: JSONObject) {
        val id = session.getString("sessionId")
        if (sessionId == id && (running || asking)) return
        if (declined == id) return
        detachCapture()
        sessionId = id
        val granted = projection
        if (granted != null) {
            AgentTrace.note("screen", "съёмка по уже выданному разрешению")
            openCapture(context, config, granted, id, session.optInt("fps", 4), session.optInt("width", 720))
            return
        }
        asking = true
        AgentTrace.screenPhase = "запрос"
        AgentTrace.note("screen", "прошу показ всего экрана")
        val intent = Intent(context, ScreenPermissionActivity::class.java)
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        intent.putExtra("sessionId", id)
        intent.putExtra("fps", session.optInt("fps", 4))
        intent.putExtra("width", session.optInt("width", 720))
        try {
            context.startActivity(intent)
        } catch (ex: Exception) {
            asking = false
            AgentTrace.screenPhase = "ошибка"
            AgentTrace.note("screen", "окно разрешения не открылось: " + (ex.message ?: ex.javaClass.simpleName))
        }
    }

    fun bind(activity: Activity) {
        host = java.lang.ref.WeakReference(activity)
    }

    fun begin(context: Context, config: AgentConfig, resultCode: Int, data: Intent, session: String, fps: Int, width: Int) {
        asking = false
        val manager = context.getSystemService(MediaProjectionManager::class.java)
        val granted = try {
            manager.getMediaProjection(resultCode, data)
        } catch (ex: Exception) {
            AgentTrace.screenPhase = "ошибка"
            AgentTrace.note("screen", "съёмка не началась: " + (ex.message ?: ex.javaClass.simpleName))
            null
        } ?: return
        granted.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() {
                if (projection == null) return
                projection = null
                detachCapture()
                AgentService.instance?.releaseProjection()
                AgentTrace.screenPhase = "нет"
                AgentTrace.note("screen", "система забрала разрешение на экран")
            }
        }, Handler(Looper.getMainLooper()))
        val previous = projection
        projection = granted
        try {
            previous?.stop()
        } catch (_: Exception) {
        }
        AgentTrace.note("screen", "разрешение на экран сохранено до перезапуска агента")
        openCapture(context, config, granted, session, fps, width)
    }

    fun note(config: AgentConfig, session: String, text: String) {
        postStatus(config, session, text)
    }

    fun denied(config: AgentConfig, session: String) {
        asking = false
        declined = session
        AgentTrace.screenPhase = "отказ"
        AgentTrace.note("screen", "показ экрана не разрешили")
        postStatus(config, session, "нужно разрешить показ всего экрана")
    }

    private fun postStatus(config: AgentConfig, session: String, text: String) {
        val nonce = (System.currentTimeMillis() / 1000).toString()
        val body = JSONObject()
            .put("deviceId", config.deviceId)
            .put("nonce", nonce)
            .put("signature", Identity.sign("$nonce|${config.deviceId}|$session"))
            .put("error", text)
        try {
            val connection = open(config, config.server.trimEnd('/') + "/api/agent/session/$session/status")
            connection.requestMethod = "POST"
            connection.doOutput = true
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8")
            connection.outputStream.use { it.write(body.toString().toByteArray()) }
            connection.responseCode
            connection.disconnect()
        } catch (_: Exception) {
        }
    }

    fun stop() {
        declined = null
        asking = false
        sessionId = null
        host?.get()?.finish()
        host = null
        detachCapture()
        if (projection != null)
            AgentService.instance?.promoteProjection(false)
        else
            AgentService.instance?.releaseProjection()
    }

    private fun openCapture(context: Context, config: AgentConfig, granted: MediaProjection, session: String, fps: Int, width: Int) {
        detachCapture()
        sessionId = session
        AgentService.instance?.promoteProjection(true)
        running = true
        AgentTrace.screenPhase = "идёт"
        AgentTrace.note("screen", "съёмка экрана идёт, агент может быть в фоне")
        if (!AgentAccessibility.connected())
            note(config, session, "включите специальные возможности «Агент ТСД»: портал видит экран, но нажать не может")
        boundContext = context.applicationContext
        boundProjection = granted
        boundWidth = width
        mountSurface(boundContext!!, granted, width)
        watchDisplay(boundContext!!)
        Thread({
            capture(context, config, session, fps.coerceIn(1, 8))
        }, "tsd-screen").start()
        Thread({
            inputs(config, session)
        }, "tsd-input").start()
    }

    private fun detachCapture() {
        running = false
        fitHandler?.removeCallbacks(refit)
        displayListener?.let { listener ->
            try {
                displayManager?.unregisterDisplayListener(listener)
            } catch (_: Exception) {
            }
        }
        displayListener = null
        displayManager = null
        fitHandler = null
        boundContext = null
        boundProjection = null
        captureWidth = 0
        captureHeight = 0
        val currentDisplay = display
        val currentReader = reader
        display = null
        reader = null
        try {
            currentDisplay?.release()
        } catch (_: Exception) {
        }
        try {
            currentReader?.close()
        } catch (_: Exception) {
        }
    }

    private fun watchDisplay(context: Context) {
        val manager = context.getSystemService(DisplayManager::class.java) ?: return
        val handler = Handler(Looper.getMainLooper())
        fitHandler = handler
        val listener = object : DisplayManager.DisplayListener {
            override fun onDisplayAdded(displayId: Int) {}
            override fun onDisplayRemoved(displayId: Int) {}
            override fun onDisplayChanged(displayId: Int) {
                if (displayId != Display.DEFAULT_DISPLAY || !running) return
                handler.removeCallbacks(refit)
                handler.postDelayed(refit, 300)
            }
        }
        displayListener = listener
        displayManager = manager
        manager.registerDisplayListener(listener, handler)
    }

    private fun mountSurface(context: Context, granted: MediaProjection, width: Int) {
        val metrics = realMetrics(context)
        if (metrics.widthPixels <= 0 || metrics.heightPixels <= 0) return
        val targetWidth = width.coerceIn(320, metrics.widthPixels)
        val targetHeight = (metrics.heightPixels * (targetWidth.toFloat() / metrics.widthPixels)).toInt().coerceAtLeast(1)
        synchronized(surfaceLock) {
            if (targetWidth == captureWidth && targetHeight == captureHeight && reader != null && display != null) return
            val previousDisplay = display
            val previousReader = reader
            display = null
            try {
                previousDisplay?.release()
            } catch (_: Exception) {
            }
            val imageReader = ImageReader.newInstance(targetWidth, targetHeight, PixelFormat.RGBA_8888, 2)
            val next = try {
                granted.createVirtualDisplay(
                    "tsd-screen",
                    targetWidth,
                    targetHeight,
                    metrics.densityDpi,
                    DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                    imageReader.surface,
                    null,
                    Handler(Looper.getMainLooper())
                )
            } catch (ex: Exception) {
                imageReader.close()
                AgentTrace.note("screen", "кадр не перестроен: " + (ex.message ?: ex.javaClass.simpleName))
                return
            }
            display = next
            reader = imageReader
            captureWidth = targetWidth
            captureHeight = targetHeight
            if (previousReader != null) {
                Handler(Looper.getMainLooper()).postDelayed({
                    try {
                        previousReader.close()
                    } catch (_: Exception) {
                    }
                }, 500)
            }
            AgentTrace.note("screen", "кадр ${targetWidth}x$targetHeight")
        }
    }

    private fun realMetrics(context: Context): DisplayMetrics {
        val metrics = DisplayMetrics()
        val display = context.getSystemService(DisplayManager::class.java).getDisplay(Display.DEFAULT_DISPLAY)
        @Suppress("DEPRECATION")
        display?.getRealMetrics(metrics)
        if (metrics.widthPixels == 0) {
            val fallback = context.resources.displayMetrics
            metrics.setTo(fallback)
        }
        return metrics
    }

    private fun jpeg(image: Image): ByteArray {
        val plane = image.planes[0]
        val buffer = plane.buffer
        val pixelStride = plane.pixelStride.coerceAtLeast(1)
        val rowPadding = plane.rowStride - pixelStride * image.width
        val bitmap = Bitmap.createBitmap(
            image.width + (rowPadding / pixelStride).coerceAtLeast(0),
            image.height,
            Bitmap.Config.ARGB_8888
        )
        buffer.rewind()
        bitmap.copyPixelsFromBuffer(buffer)
        val cropped = if (bitmap.width == image.width) bitmap else Bitmap.createBitmap(bitmap, 0, 0, image.width, image.height)
        val out = ByteArrayOutputStream()
        cropped.compress(Bitmap.CompressFormat.JPEG, 60, out)
        if (cropped !== bitmap) cropped.recycle()
        bitmap.recycle()
        return out.toByteArray()
    }

    private fun capture(context: Context, config: AgentConfig, session: String, fps: Int) {
        val delay = 1000L / fps
        while (running && sessionId == session) {
            try {
                val image = synchronized(surfaceLock) { reader?.acquireLatestImage() }
                if (image != null) {
                    val bytes = image.use { jpeg(it) }
                    if (bytes.size > 32) postFrame(config, session, bytes)
                }
            } catch (_: Exception) {
            }
            try {
                Thread.sleep(delay)
            } catch (_: InterruptedException) {
                break
            }
        }
    }

    private fun inputs(config: AgentConfig, session: String) {
        while (running && sessionId == session) {
            try {
                val nonce = (System.currentTimeMillis() / 1000).toString()
                val body = JSONObject()
                    .put("deviceId", config.deviceId)
                    .put("nonce", nonce)
                    .put("signature", Identity.sign("$nonce|${config.deviceId}|$session"))
                val connection = open(config, config.server.trimEnd('/') + "/api/agent/session/$session/input")
                connection.requestMethod = "POST"
                connection.doOutput = true
                connection.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                connection.outputStream.use { it.write(body.toString().toByteArray()) }
                val text = connection.inputStream.bufferedReader().use { it.readText() }
                connection.disconnect()
                val events = JSONObject(text).optJSONArray("events") ?: JSONArray()
                for (index in 0 until events.length())
                    apply(events.getJSONObject(index))
            } catch (_: Exception) {
                try { Thread.sleep(1000) } catch (_: InterruptedException) { break }
            }
        }
    }

    private fun apply(event: JSONObject) {
        val service = AgentAccessibility.instance ?: return
        when (event.optString("type")) {
            "tap" -> service.tap("", event.optDouble("x", 0.5).toFloat(), event.optDouble("y", 0.5).toFloat())
            "swipe" -> service.swipe(JSONObject()
                .put("x1", event.optDouble("x"))
                .put("y1", event.optDouble("y"))
                .put("x2", event.optDouble("x2"))
                .put("y2", event.optDouble("y2")))
            "text" -> service.typeText(event.optString("value"))
            "key" -> service.key(event.optString("code"))
        }
    }

    private fun postFrame(config: AgentConfig, session: String, jpeg: ByteArray) {
        val nonce = (System.currentTimeMillis() / 1000).toString()
        val connection = open(config, config.server.trimEnd('/') + "/api/agent/session/$session/frame")
        connection.requestMethod = "POST"
        connection.doOutput = true
        connection.setRequestProperty("Content-Type", "image/jpeg")
        connection.setRequestProperty("X-Device-Id", config.deviceId)
        connection.setRequestProperty("X-Nonce", nonce)
        connection.setRequestProperty("X-Signature", Identity.sign("$nonce|${config.deviceId}|$session"))
        connection.outputStream.use { it.write(jpeg) }
        connection.responseCode
        connection.disconnect()
    }

    private fun open(config: AgentConfig, url: String): HttpsURLConnection {
        val connection = URL(url).openConnection() as HttpsURLConnection
        connection.sslSocketFactory = pinnedClient(config.fingerprint).socketFactory
        connection.hostnameVerifier = HostnameVerifier { _, _ -> true }
        connection.connectTimeout = 10000
        connection.readTimeout = 20000
        return connection
    }
}

private object ProjectionConsent34 {
    fun create(manager: MediaProjectionManager): Intent =
        manager.createScreenCaptureIntent(MediaProjectionConfig.createConfigForDefaultDisplay())
}

class ScreenPermissionActivity : Activity() {
    private var asked = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        ScreenStreamer.bind(this)
        asked = savedInstanceState?.getBoolean("asked") == true
    }

    override fun onResume() {
        super.onResume()
        if (asked) return
        asked = true
        val manager = getSystemService(MediaProjectionManager::class.java)
        val consent = if (Build.VERSION.SDK_INT >= 34)
            ProjectionConsent34.create(manager)
        else
            manager.createScreenCaptureIntent()
        @Suppress("DEPRECATION")
        startActivityForResult(consent, 1)
    }

    override fun onSaveInstanceState(outState: Bundle) {
        outState.putBoolean("asked", asked)
        super.onSaveInstanceState(outState)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
    }

    @Deprecated("Android projection consent")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        val session = intent.getStringExtra("sessionId").orEmpty()
        val config = AgentConfig(this)
        if (resultCode == RESULT_OK && data != null && session.isNotEmpty()) {
            ScreenStreamer.begin(this, config, resultCode, data, session, intent.getIntExtra("fps", 4), intent.getIntExtra("width", 720))
            moveTaskToBack(true)
        } else {
            if (session.isNotEmpty()) ScreenStreamer.denied(config, session)
            finish()
        }
    }
}
