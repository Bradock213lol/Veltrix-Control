using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NexaGrid.Controller.Security;

public static class ControllerCertificate
{
    // Windows Schannel cannot serve TLS with an ephemeral private key. Import into the
    // service account's user key set; the PFX at rest remains protected with machine DPAPI.
    private const X509KeyStorageFlags ServerKeyStorageFlags = X509KeyStorageFlags.UserKeySet;

    public static X509Certificate2 LoadOrCreate(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "controller-certificate.dat");
        if (File.Exists(path))
        {
            var protectedBytes = File.ReadAllBytes(path);
            var pfx = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine);
            var loaded = X509CertificateLoader.LoadPkcs12(pfx, null, ServerKeyStorageFlags);
            WriteFingerprint(dataDirectory, loaded);
            return loaded;
        }

        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            $"CN={Environment.MachineName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        var exported = created.Export(X509ContentType.Pfx);
        File.WriteAllBytes(path, ProtectedData.Protect(exported, null, DataProtectionScope.LocalMachine));
        var certificate = X509CertificateLoader.LoadPkcs12(exported, null, ServerKeyStorageFlags);
        WriteFingerprint(dataDirectory, certificate);
        return certificate;
    }

    private static void WriteFingerprint(string dataDirectory, X509Certificate2 certificate)
    {
        var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        File.WriteAllText(Path.Combine(dataDirectory, "controller-certificate.sha256"), fingerprint);
    }
}
