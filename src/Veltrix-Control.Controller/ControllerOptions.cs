namespace VeltrixControl.Controller;

public sealed class ControllerOptions
{
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Veltrix-Control");

    public int HeartbeatSeconds { get; set; } = 5;
    public int OfflineAfterSeconds { get; set; } = 20;
    public int RetentionDays { get; set; } = 30;
    public bool EnableHttpsListener { get; set; } = true;
    public int HttpsPort { get; set; } = 5443;
    public bool EnableLocalHttpListener { get; set; } = true;
    public int LocalHttpPort { get; set; } = 5187;
}
