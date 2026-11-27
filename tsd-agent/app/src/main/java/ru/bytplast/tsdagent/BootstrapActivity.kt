package ru.bytplast.tsdagent

import android.content.Intent
import android.os.Bundle
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import org.json.JSONObject

class BootstrapActivity : AppCompatActivity() {
    private val installCert = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        val config = AgentConfig(this)
        val status = findViewById<TextView>(R.id.status)
        if (result.resultCode == RESULT_OK) {
            CertInstaller.markInstalled(config)
            status.text = "Сертификат площадки установлен."
        } else {
            status.text = "Сертификат не установлен. Нажмите ещё раз и подтвердите системное окно."
        }
    }

    private val pickFile = registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri == null) return@registerForActivityResult
        try {
            contentResolver.openInputStream(uri)?.use { stream ->
                val json = JSONObject(stream.readBytes().toString(Charsets.UTF_8))
                findViewById<EditText>(R.id.server).setText(json.optString("server"))
                findViewById<EditText>(R.id.fingerprint).setText(json.optString("fingerprint"))
                findViewById<EditText>(R.id.site).setText(json.optString("site"))
                findViewById<TextView>(R.id.status).text = "Файл прочитан. Нажмите «Сообщить порталу»."
            }
        } catch (ex: Exception) {
            findViewById<TextView>(R.id.status).text = ex.message ?: "файл настройки не прочитан"
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.bootstrap)
        val config = AgentConfig(this)
        val server = findViewById<EditText>(R.id.server)
        val fingerprint = findViewById<EditText>(R.id.fingerprint)
        val site = findViewById<EditText>(R.id.site)
        val status = findViewById<TextView>(R.id.status)
        server.setText(config.server.ifEmpty { "https://mdm-tsd" })
        fingerprint.setText(config.fingerprint)
        site.setText(config.site.ifEmpty { "mdm-tsd" })
        if (config.ready)
            status.text = "Агент запущен. Он сам сообщает порталу, что включён."
        findViewById<Button>(R.id.pick).setOnClickListener {
            try {
                pickFile.launch(arrayOf("*/*"))
            } catch (ex: Exception) {
                status.text = ex.message ?: "не удалось открыть выбор файла"
            }
        }
        val save = findViewById<Button>(R.id.save)
        save.setOnClickListener { register(config, server, fingerprint, site, status, save) }
        findViewById<Button>(R.id.certificate).setOnClickListener {
            if (!config.ready) {
                status.text = "Сначала сообщите порталу, затем ставьте сертификат."
                return@setOnClickListener
            }
            status.text = "Открываю установку сертификата центра…"
            Thread {
                try {
                    val intent = CertInstaller.prepare(this, config)
                    runOnUiThread {
                        if (intent == null && CertInstaller.pending(config))
                            status.text = "В файле портала нет сертификата центра"
                        else if (intent == null)
                            status.text = "Сертификат центра установлен"
                        else {
                            status.text = "Подтвердите установку сертификата ЦС. Система предупредит, что это сертификат организации."
                            installCert.launch(intent)
                        }
                    }
                } catch (ex: Exception) {
                    runOnUiThread { status.text = ex.message ?: "сертификат не открылся" }
                }
            }.start()
        }
    }

    private fun register(
        config: AgentConfig,
        serverField: EditText,
        fingerprintField: EditText,
        siteField: EditText,
        status: TextView,
        save: Button
    ) {
        val typedServer = serverField.text.toString().trim().trimEnd('/')
        val typedSite = siteField.text.toString().trim()
        val typedFingerprint = fingerprintField.text.toString()
        if (!typedServer.startsWith("https://")) {
            status.text = "Адрес портала должен начинаться с https://"
            return
        }
        save.isEnabled = false
        status.text = "Сообщаю порталу…"
        Thread {
            try {
                var fingerprint = typedFingerprint
                var site = typedSite
                if (AgentConfig.normalize(fingerprint).length != 64) {
                    val json = PortalRegistrar.fetchBootstrap(typedServer)
                    fingerprint = json.getString("fingerprint")
                    if (site.isEmpty()) site = json.optString("site", "mdm-tsd")
                    runOnUiThread {
                        fingerprintField.setText(fingerprint)
                        siteField.setText(site)
                    }
                }
                config.server = typedServer
                config.fingerprint = fingerprint
                config.site = site.ifEmpty { "mdm-tsd" }
                Identity.ensure(config)
                val message = PortalRegistrar(this, config).announce()
                startForegroundService(Intent(this, AgentService::class.java))
                runOnUiThread {
                    status.text = message
                    save.isEnabled = true
                }
            } catch (ex: Exception) {
                runOnUiThread {
                    status.text = ex.message ?: "не удалось сообщить порталу"
                    save.isEnabled = true
                }
            }
        }.start()
    }
}
