using System.Security.Cryptography;
using System.Text;

namespace TsdServer.Core;

public static class Protocol
{
    public const int DiscoveryPort = 8741;
    public const int CommandPort = 8742;
    public static readonly byte[] Magic = "TSD1"u8.ToArray();

    public static string Fingerprint(byte[] certificateDer)
    {
        var hash = SHA256.HashData(certificateDer);
        return Convert.ToHexString(hash);
    }

    public static string FormatFingerprint(string hex)
    {
        var clean = NormalizeFingerprint(hex);
        return string.Join(':', Enumerable.Range(0, clean.Length / 2).Select(i => clean.Substring(i * 2, 2)));
    }

    public static string NormalizeFingerprint(string value) =>
        value.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    public static bool VerifySignature(string publicKeySpki, string payload, string signatureBase64)
    {
        try
        {
            var key = Convert.FromBase64String(publicKeySpki);
            var signature = Convert.FromBase64String(signatureBase64);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out _);
            return ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(payload),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
