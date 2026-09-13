using System.Security.Cryptography;
using System.Text.Json;

namespace VeltrixControl.Agent.Security;

public sealed record DeviceIdentity(Guid DeviceId, string PrivateKey);

public sealed class DeviceIdentityStore(AgentOptions options)
{
    private readonly string _identityPath = Path.Combine(options.DataDirectory, "device-identity.dat");

    public DeviceIdentity? Load()
    {
        if (!File.Exists(_identityPath)) return null;
        var protectedBytes = File.ReadAllBytes(_identityPath);
        var json = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine);
        return JsonSerializer.Deserialize<DeviceIdentity>(json);
    }

    public void Save(DeviceIdentity identity)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_identityPath)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(identity);
        var protectedBytes = ProtectedData.Protect(json, null, DataProtectionScope.LocalMachine);
        var temporaryPath = _identityPath + ".new";
        File.WriteAllBytes(temporaryPath, protectedBytes);
        File.Move(temporaryPath, _identityPath, true);
    }

    public string? TakeEnrollmentCode()
    {
        var path = Path.IsPathRooted(options.EnrollmentCodeFile)
            ? options.EnrollmentCodeFile
            : Path.Combine(options.DataDirectory, options.EnrollmentCodeFile);
        if (!File.Exists(path)) return null;
        var code = File.ReadAllText(path).Trim();
        return string.IsNullOrWhiteSpace(code) ? null : code;
    }

    public void RemoveEnrollmentCode()
    {
        var path = Path.IsPathRooted(options.EnrollmentCodeFile)
            ? options.EnrollmentCodeFile
            : Path.Combine(options.DataDirectory, options.EnrollmentCodeFile);
        if (File.Exists(path)) File.Delete(path);
    }
}
