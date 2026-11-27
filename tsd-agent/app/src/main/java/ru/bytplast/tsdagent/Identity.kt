package ru.bytplast.tsdagent

import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.UUID

object Identity {
    private const val ALIAS = "tsd-agent"

    fun ensure(config: AgentConfig) {
        if (config.deviceId.isEmpty())
            config.deviceId = UUID.randomUUID().toString()
        val ks = keyStore()
        if (!ks.containsAlias(ALIAS)) {
            val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
            val spec = KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
                .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                .setDigests(KeyProperties.DIGEST_SHA256)
                .build()
            generator.initialize(spec)
            generator.generateKeyPair()
        }
    }

    fun publicKey(): String {
        val cert = keyStore().getCertificate(ALIAS)
        return Base64.encodeToString(cert.publicKey.encoded, Base64.NO_WRAP)
    }

    fun sign(payload: String): String {
        val entry = keyStore().getEntry(ALIAS, null) as KeyStore.PrivateKeyEntry
        val signature = Signature.getInstance("SHA256withECDSA")
        signature.initSign(entry.privateKey)
        signature.update(payload.toByteArray(Charsets.UTF_8))
        return Base64.encodeToString(signature.sign(), Base64.NO_WRAP)
    }

    fun deviceType(): String {
        val text = "${Build.MANUFACTURER} ${Build.MODEL}".uppercase()
        return when {
            text.contains("MC33") -> "zebra_mc33"
            text.contains("MT65") || text.contains("NEWLAND") -> "newland_mt65"
            else -> "phone"
        }
    }

    private fun keyStore() = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
}
