namespace VeltrixControl.Agent;

public sealed class AgentOptions
{
    public string ControllerUrl { get; set; } = "https://localhost:5443";
    public string? ControllerCertificateThumbprint { get; set; }
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Veltrix-Control",
        "Agent");
    public string EnrollmentCodeFile { get; set; } = "enrollment-code.txt";
    public string ManagedRoot { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
    public bool AllowPowerActions { get; set; }
    public bool AllowProcessActions { get; set; }
    public bool AllowServiceActions { get; set; }
    public bool AllowTerminal { get; set; }
    public bool AllowInsecureLoopback { get; set; }
}
