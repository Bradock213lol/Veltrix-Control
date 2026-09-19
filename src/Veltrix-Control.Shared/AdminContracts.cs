namespace VeltrixControl.Contracts;

public sealed record ProcessTargetArgument(int ProcessId, string ProcessName);

public sealed record StartProcessArgument(string FileName, string? Arguments, string? WorkingDirectory);

public sealed record ProcessPriorityArgument(int ProcessId, string ProcessName, string PriorityClass);

public sealed record ServiceTargetArgument(string Name);

public sealed record ServiceStartTypeArgument(string Name, string StartType);

public sealed record ScheduledPowerArgument(int DelaySeconds);

public sealed record ProcessActionResult(int ProcessId, string Name, string Action);

public sealed record ServiceActionResult(string Name, string Action, string? Detail);

public enum TerminalShell
{
    PowerShell,
    CommandPrompt
}

public sealed record TerminalStartArgument(Guid SessionId, string Shell, string WorkingDirectory);

public sealed record TerminalInputArgument(Guid SessionId, string Data);

public sealed record TerminalOutputArgument(Guid SessionId, long SinceSequence);

public sealed record TerminalStopArgument(Guid SessionId);

public sealed record TerminalOutputResult(
    Guid SessionId,
    long Sequence,
    string Output,
    bool Exited,
    int? ExitCode);

public sealed record StartTerminalRequest(TerminalShell Shell, string WorkingDirectory);

public sealed record TerminalInputRequest(string Data);

public sealed record TerminalSessionView(
    Guid Id,
    Guid DeviceId,
    string Shell,
    string WorkingDirectory,
    string State,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivity);

public sealed record WakeRequest();

public sealed record TerminalStartResponse(
    TerminalSessionView Session,
    OperationView Operation);

public sealed record TerminalOperationResponse(
    TerminalSessionView Session,
    OperationView Operation);

public static class AdminLimits
{
    public const int MaxCommandLength = 4096;
    public const int MaxTerminalOutputChunk = 64 * 1024;
    public const int TerminalBufferBytes = 512 * 1024;
    public const int MaxScheduledPowerSeconds = 7 * 24 * 60 * 60;
    public const int MaxTerminalSessionsPerDevice = 4;

    public static readonly string[] ProcessPriorities =
        ["Idle", "BelowNormal", "Normal", "AboveNormal", "High", "RealTime"];

    public static readonly string[] ServiceStartTypes =
        ["Automatic", "Manual", "Disabled"];

    public static bool IsProtectedProcess(string name) =>
        ProtectedProcesses.Contains(name);

    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "fontdrvhost", "dwm", "svchost", "WmiPrvSE", "Veltrix-Control Managed Node"
    };

    private static readonly HashSet<string> ProtectedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "DcomLaunch", "WinDefend", "SecurityHealthService", "LsaSrv", "SamSs", "Schedule",
        "Veltrix-Control Controller", "Veltrix-Control Managed Node"
    };

    public static bool IsProtectedService(string name) => ProtectedServices.Contains(name);
}
