namespace VeltrixControl.Controller;

public sealed class ControllerOptions
{
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Veltrix-Control");

    public int HeartbeatSeconds { get; set; } = 5;
    public int OfflineAfterSeconds { get; set; } = 20;
    public int RetentionDays { get; set; } = 30;

    // Temporary recovery account: created automatically after the first Owner account
    // exists, so an Owner password can never lock an administrator out of the platform.
    // Change or delete it from Settings -> User accounts once it is no longer needed.
    public bool EnableRecoveryAccount { get; set; } = true;
    public string RecoveryAccountUsername { get; set; } = "admin";
    public string RecoveryAccountPassword { get; set; } = "admin!";
    public bool EnableHttpsListener { get; set; } = true;
    public int HttpsPort { get; set; } = 5443;
    public bool EnableLocalHttpListener { get; set; } = true;
    public int LocalHttpPort { get; set; } = 5187;
}
