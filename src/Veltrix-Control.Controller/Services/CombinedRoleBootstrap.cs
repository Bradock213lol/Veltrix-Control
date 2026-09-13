using VeltrixControl.Core.Security;

namespace VeltrixControl.Controller.Services;

public static class CombinedRoleBootstrap
{
    public const string BootstrapFileName = "bootstrap-enrollment-code.txt";
    public const string AgentCodeFileName = "enrollment-code.txt";
    public const string AgentIdentityFileName = "device-identity.dat";

    public static bool Prepare(string controllerDataDirectory, string agentDataDirectory)
    {
        Directory.CreateDirectory(controllerDataDirectory);
        Directory.CreateDirectory(agentDataDirectory);
        if (File.Exists(Path.Combine(agentDataDirectory, AgentIdentityFileName))) return false;

        var code = SecretGenerator.CreateEnrollmentCode();
        WriteAtomically(Path.Combine(controllerDataDirectory, BootstrapFileName), code);
        WriteAtomically(Path.Combine(agentDataDirectory, AgentCodeFileName), code);
        return true;
    }

    private static void WriteAtomically(string path, string value)
    {
        var temporaryPath = path + ".new";
        File.WriteAllText(temporaryPath, value);
        File.Move(temporaryPath, path, true);
    }
}
