using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VeltrixControl.Controller.Security;

namespace VeltrixControl.IntegrationTests;

public sealed class ControllerCertificateTests
{
    [Fact]
    public async Task GeneratedCertificateCompletesTlsHandshake()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Veltrix-Control.Tests", Guid.NewGuid().ToString("N"));
        var listener = new TcpListener(IPAddress.Loopback, 0);

        try
        {
            using var certificate = ControllerCertificate.LoadOrCreate(directory);
            Assert.True(certificate.HasPrivateKey);
            var expectedFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);

            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var serverHandshake = AcceptTlsClientAsync(listener, certificate, timeout.Token);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var clientStream = new SslStream(
                client.GetStream(),
                false,
                (_, remoteCertificate, _, _) => HasFingerprint(remoteCertificate, expectedFingerprint));
            await clientStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, timeout.Token);
            await serverHandshake;

            Assert.True(clientStream.IsAuthenticated);
            Assert.True(clientStream.IsEncrypted);
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static async Task AcceptTlsClientAsync(
        TcpListener listener,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var serverStream = new SslStream(client.GetStream(), false);
        await serverStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, cancellationToken);
        Assert.True(serverStream.IsAuthenticated);
        Assert.True(serverStream.IsEncrypted);
    }

    private static bool HasFingerprint(X509Certificate? certificate, string expectedFingerprint)
    {
        if (certificate is null) return false;
        using var loaded = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return string.Equals(
            loaded.GetCertHashString(HashAlgorithmName.SHA256),
            expectedFingerprint,
            StringComparison.OrdinalIgnoreCase);
    }
}
