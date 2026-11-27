package ru.bytplast.tsdagent

import android.app.Activity
import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.util.Base64
import androidx.core.content.FileProvider
import java.io.ByteArrayInputStream
import java.io.File
import java.net.URL
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection

object CertInstaller {
    fun remember(config: AgentConfig, stamp: String) {
        if (stamp.isNotBlank())
            config.certWanted = stamp
    }

    fun pending(config: AgentConfig) = config.certWanted.isNotBlank() && config.certWanted != config.certStamp

    fun prepare(context: Context, config: AgentConfig): Intent? {
        val root = root(download(config)) ?: return null
        if (installAsOwner(context, root.encoded)) {
            markInstalled(config)
            return null
        }
        val file = File(context.cacheDir, "bytplast-ca.crt")
        file.writeText(pem(root))
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", file)
        return Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, "application/x-x509-ca-cert")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
    }

    fun markInstalled(config: AgentConfig) {
        if (config.certWanted.isNotBlank())
            config.certStamp = config.certWanted
    }

    private fun installAsOwner(context: Context, der: ByteArray): Boolean {
        val dpm = context.getSystemService(DevicePolicyManager::class.java) ?: return false
        val mine = context.packageName
        if (!dpm.isDeviceOwnerApp(mine) && !dpm.isProfileOwnerApp(mine)) return false
        dpm.installCaCert(ComponentName(context, TsdDeviceAdmin::class.java), der)
        return true
    }

    private fun download(config: AgentConfig): String {
        val connection = URL(config.server.trimEnd('/') + "/store/portal.crt").openConnection() as HttpsURLConnection
        connection.sslSocketFactory = pinnedClient(config.fingerprint).socketFactory
        connection.hostnameVerifier = HostnameVerifier { _, _ -> true }
        connection.connectTimeout = 10000
        connection.readTimeout = 15000
        return connection.inputStream.bufferedReader().use { it.readText() }
    }

    private fun root(pem: String): X509Certificate? {
        val factory = CertificateFactory.getInstance("X509")
        val certs = pem.split("-----END CERTIFICATE-----").mapNotNull { part ->
            val body = part.substringAfter("-----BEGIN CERTIFICATE-----", "")
            if (body.isBlank()) return@mapNotNull null
            val der = Base64.decode(body.replace(Regex("\\s"), ""), Base64.DEFAULT)
            factory.generateCertificate(ByteArrayInputStream(der)) as X509Certificate
        }
        return certs.firstOrNull { it.basicConstraints >= 0 && it.subjectX500Principal == it.issuerX500Principal }
            ?: certs.firstOrNull { it.basicConstraints >= 0 }
    }

    private fun pem(cert: X509Certificate): String {
        val body = Base64.encodeToString(cert.encoded, Base64.NO_WRAP).chunked(64).joinToString("\n")
        return "-----BEGIN CERTIFICATE-----\n$body\n-----END CERTIFICATE-----\n"
    }
}

fun Activity.launchCertInstall(config: AgentConfig, launcher: androidx.activity.result.ActivityResultLauncher<Intent>) {
    val intent = CertInstaller.prepare(this, config) ?: return
    launcher.launch(intent)
}
