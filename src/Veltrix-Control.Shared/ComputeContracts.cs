namespace VeltrixControl.Contracts;

public sealed record ComputePolicyRequest(
    string Mode,
    int ReservedCpuThreads,
    long ReservedMemoryBytes,
    long ReservedDiskBytes);

public sealed record ComputePolicyView(
    Guid DeviceId,
    string Mode,
    int ReservedCpuThreads,
    long ReservedMemoryBytes,
    long ReservedDiskBytes,
    DateTimeOffset UpdatedAt);

public sealed record ComputeRequirements(
    int CpuThreads,
    long MemoryBytes,
    long DiskBytes,
    bool RequiresGpu,
    string? DeviceNameContains,
    Guid? DeviceId);

public sealed record ComputeCommand(
    string Executable,
    string? Arguments,
    string? WorkingDirectory);

public sealed record ComputeJobRequest(
    string Name,
    int Priority,
    ComputeRequirements Requirements,
    ComputeCommand Command,
    int TimeoutSeconds,
    int MaxAttempts);

public sealed record ComputeJobView(
    Guid Id,
    string Name,
    string State,
    int Priority,
    string RequirementJson,
    string CommandJson,
    Guid? AssignedDeviceId,
    string RequestedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? ResultJson,
    string? Error,
    int Attempts,
    int MaxAttempts,
    int TimeoutSeconds);

public sealed record ComputeJobArgument(
    Guid JobId,
    string Executable,
    string? Arguments,
    string? WorkingDirectory,
    int TimeoutSeconds);

public sealed record ComputeJobResult(
    Guid JobId,
    int ExitCode,
    string Output,
    bool TimedOut);

public sealed record ComputeCancelArgument(Guid JobId);

public static class ComputeModes
{
    public static bool AllowsJobs(string mode) => mode.ToLowerInvariant() switch
    {
        "gaming" => false,
        "maintenance" => false,
        _ => true
    };

    public static readonly string[] All = ["Idle", "Server", "Compute", "Gaming", "Maintenance"];
}

public static class ComputeLimits
{
    public const int MaxPriority = 1000;
    public const int MinTimeoutSeconds = 10;
    public const int MaxTimeoutSeconds = 24 * 60 * 60;
    public const int MaxAttempts = 5;
    public const int MaxOutputLength = 32 * 1024;
    public const int MaxQueuedJobs = 500;
}
