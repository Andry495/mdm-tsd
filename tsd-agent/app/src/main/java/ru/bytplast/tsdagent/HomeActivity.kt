package ru.bytplast.tsdagent

import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import org.json.JSONObject

class HomeActivity : AppCompatActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.home)
        findViewById<Button>(R.id.settings).setOnClickListener {
            startActivity(Intent(this, BootstrapActivity::class.java))
        }
        findViewById<Button>(R.id.control).setOnClickListener {
            startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
        }
    }

    override fun onResume() {
        super.onResume()
        val config = AgentConfig(this)
        val status = findViewById<TextView>(R.id.status)
        if (config.ready) {
            startForegroundService(Intent(this, AgentService::class.java))
            keepAlive()
            status.text = when {
                !AgentAccessibility.connected() -> "Для нажатий с портала включите пункт «Управление»: служба «Агент ТСД»."
                CertInstaller.pending(config) -> "Агент работает в фоне. Сертификат площадки ставится в настройках."
                else -> "Агент работает в фоне. Это окно можно закрыть."
            }
            loadCatalog(config, status)
        } else {
            status.text = "Портал ещё не указан. Откройте настройки."
            findViewById<LinearLayout>(R.id.list).removeAllViews()
        }
    }

    private fun keepAlive() {
        val config = AgentConfig(this)
        if (config.askedBattery) return
        val power = getSystemService(PowerManager::class.java)
        if (power.isIgnoringBatteryOptimizations(packageName)) {
            config.askedBattery = true
            return
        }
        config.askedBattery = true
        try {
            startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
        } catch (_: Exception) {
        }
    }

    private fun loadCatalog(config: AgentConfig, status: TextView) {
        Thread {
            try {
                val connection = java.net.URL(config.server.trimEnd('/') + "/store/catalog.json").openConnection() as javax.net.ssl.HttpsURLConnection
                connection.sslSocketFactory = pinnedClient(config.fingerprint).socketFactory
                connection.hostnameVerifier = javax.net.ssl.HostnameVerifier { _, _ -> true }
                connection.connectTimeout = 10000
                connection.readTimeout = 15000
                val text = connection.inputStream.bufferedReader().use { it.readText() }
                val items = JSONObject(text).getJSONArray("items")
                runOnUiThread { showItems(items) }
            } catch (ex: Exception) {
                runOnUiThread { status.text = ex.message ?: "каталог портала не открылся" }
            }
        }.start()
    }

    private fun showItems(items: org.json.JSONArray) {
        val list = findViewById<LinearLayout>(R.id.list)
        list.removeAllViews()
        if (items.length() == 0) {
            list.addView(TextView(this).apply { text = "На портале пока нет пакетов." })
            return
        }
        for (index in 0 until items.length()) {
            val item = items.getJSONObject(index)
            val packageName = item.getString("packageName")
            val versionCode = item.getInt("versionCode")
            val installed = installedCode(packageName)
            val box = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(0, 24, 0, 24)
            }
            box.addView(TextView(this).apply {
                text = item.optString("title", packageName)
                textSize = 18f
            })
            box.addView(TextView(this).apply {
                text = "$packageName · ${item.optString("versionName")} ($versionCode)"
            })
            val summary = item.optString("summary")
            if (summary.isNotEmpty())
                box.addView(TextView(this).apply { text = summary })
            val button = Button(this)
            when {
                installed == null -> button.text = "Установить"
                installed < versionCode -> button.text = "Обновить"
                else -> {
                    button.text = "Установлено"
                    button.isEnabled = false
                }
            }
            button.setOnClickListener {
                button.isEnabled = false
                button.text = "Ставится…"
                Thread {
                    val error = CommandServer(this, AgentConfig(this)).installLocal(item)
                    runOnUiThread {
                        if (error == null) {
                            button.text = "Готово"
                        } else {
                            button.isEnabled = true
                            button.text = "Повторить"
                            findViewById<TextView>(R.id.status).text = error
                        }
                    }
                }.start()
            }
            box.addView(button)
            list.addView(box)
        }
    }

    private fun installedCode(packageName: String): Int? = try {
        val info = packageManager.getPackageInfo(packageName, 0)
        info.longVersionCode.toInt()
    } catch (_: PackageManager.NameNotFoundException) {
        null
    }
}
