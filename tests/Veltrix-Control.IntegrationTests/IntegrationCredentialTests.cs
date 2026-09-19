using Microsoft.Extensions.Options;
using VeltrixControl.Infrastructure;

namespace VeltrixControl.IntegrationTests;

public sealed class IntegrationCredentialTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Veltrix-Control.Credentials", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CredentialsRoundTripWithoutPlaintext()
    {
        Directory.CreateDirectory(_directory);
        var protector = new IntegrationCredentialProtector(Options.Create(new StoreOptions { DatabasePath = Path.Combine(_directory, "controller.db") }));
        const string secret = "ptla_super_secret_api_key";
        var protectedValue = protector.Protect(secret);

        Assert.DoesNotContain("ptla", protectedValue, StringComparison.Ordinal);
        Assert.Equal(secret, protector.Unprotect(protectedValue));
    }

    [Fact]
    public void TamperedCredentialsAreRejected()
    {
        Directory.CreateDirectory(_directory);
        var protector = new IntegrationCredentialProtector(Options.Create(new StoreOptions { DatabasePath = Path.Combine(_directory, "controller.db") }));
        var protectedValue = protector.Protect("secret");
        var tampered = protectedValue[..^4] + "AAAA";

        Assert.Null(protector.Unprotect(tampered));
        Assert.Null(protector.Unprotect("not-base64"));
    }

    [Fact]
    public void KeyFileIsReusedAcrossInstances()
    {
        Directory.CreateDirectory(_directory);
        var first = new IntegrationCredentialProtector(Options.Create(new StoreOptions { DatabasePath = Path.Combine(_directory, "controller.db") }));
        var protectedValue = first.Protect("secret");
        var second = new IntegrationCredentialProtector(Options.Create(new StoreOptions { DatabasePath = Path.Combine(_directory, "controller.db") }));
        Assert.Equal("secret", second.Unprotect(protectedValue));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
