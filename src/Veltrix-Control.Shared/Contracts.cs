namespace VeltrixControl.Contracts;

public sealed record DiskInventory(
    string Name,
    string Format,
    long TotalBytes,
    long AvailableBytes);

public sealed record HardwareInventory(
    string OperatingSystem,
    string OsVersion,
    string Architecture,
    int LogicalProcessors,
    long TotalMemoryBytes,
    IReadOnlyList<DiskInventory> Disks,
    string AgentVersion,
    bool IsSimulation = false,
    string? MacAddress = null);

public sealed record TelemetrySnapshot(
    DateTimeOffset CapturedAt,
    double CpuPercent,
    long UsedMemoryBytes,
    long TotalMemoryBytes,
    long UptimeSeconds,
    IReadOnlyList<DiskInventory> Disks);

public sealed record EnrollmentRequest(
    string Code,
    string DeviceName,
    string DevicePublicKey,
    HardwareInventory Inventory);

public sealed record EnrollmentResponse(
    Guid DeviceId,
    DateTimeOffset EnrolledAt);

public sealed record SignedAgentMessage(
    Guid DeviceId,
    long UnixTimeSeconds,
    string Nonce,
    string Payload,
    string Signature);

public sealed record HeartbeatPayload(
    string DeviceName,
    HardwareInventory Inventory,
    TelemetrySnapshot Telemetry);

public sealed record HeartbeatResponse(
    IReadOnlyList<OperationAssignment> Operations,
    int NextHeartbeatSeconds);

public enum OperationKind
{
    Restart,
    Shutdown,
    ListDirectory,
    ListProcesses,
    ListServices,
    ListSoftware,
    ListNetworkAdapters,

    CreateDirectory,
    CreateFile,
    RenameFile,
    MoveFile,
    CopyFile,
    DeleteFile,
    SearchFiles,
    DirectorySize,
    CreateArchive,
    ExtractArchive,
    ReadTextFile,
    WriteTextFile,
    ListFileBackups,
    RestoreFileBackup,
    ReadFileBackup,

    StartProcess,
    StopProcess,
    SetProcessPriority,
    StartService,
    StopService,
    SetServiceStartType,
    ScheduleRestart,
    ScheduleShutdown,
    Logoff,
    Sleep,
    Hibernate,
    TerminalStart,
    TerminalInput,
    TerminalOutput,
    TerminalStop,

    InstallSoftware,
    UninstallSoftware,
    UpgradeSoftware,
    ScanWindowsUpdates,
    InstallWindowsUpdate,

    CreateBackup,
    RestoreBackup,
    VerifyBackup,

    RunComputeJob,
    CancelComputeJob,

    GameServerProvision,
    GameServerStart,
    GameServerStop,
    GameServerUpdate,
    GameServerInput,
    GameServerOutput
}

public enum OperationState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut
}

public sealed record OperationAssignment(
    Guid Id,
    OperationKind Kind,
    string? Argument,
    DateTimeOffset RequestedAt);

public sealed record OperationResultPayload(
    Guid OperationId,
    OperationState State,
    string? ResultJson,
    string? Error,
    DateTimeOffset FinishedAt);

public sealed record FileEntry(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long? SizeBytes,
    DateTimeOffset LastModified);

public sealed record ProcessSnapshot(
    int Id,
    string Name,
    long WorkingSetBytes,
    double TotalProcessorSeconds,
    int ThreadCount);

public sealed record ServiceSnapshot(
    string Name,
    string DisplayName,
    string Status,
    string StartType);

public sealed record SoftwareSnapshot(
    string Name,
    string? Version,
    string? Publisher,
    string? InstallDate);

public sealed record NetworkAdapterSnapshot(
    string Name,
    string Description,
    string Status,
    long SpeedBitsPerSecond,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers,
    long BytesSent,
    long BytesReceived);

public sealed record CreateEnrollmentTokenRequest(int LifetimeMinutes = 15);

public sealed record EnrollmentTokenResponse(
    Guid Id,
    string Code,
    DateTimeOffset ExpiresAt);

public sealed record SetupRequest(string Username, string Password);

public sealed record LoginRequest(string Username, string Password);

public sealed record OperationRequest(
    OperationKind Kind,
    string? Argument,
    bool Confirmed);

public sealed record DeviceSummary(
    Guid Id,
    string Name,
    bool Online,
    DateTimeOffset LastHeartbeat,
    HardwareInventory Inventory,
    TelemetrySnapshot? Telemetry,
    int HealthScore);

public sealed record AuditEventView(
    long Id,
    DateTimeOffset Timestamp,
    string Actor,
    string Action,
    string Target,
    string Outcome,
    string? Metadata);

public sealed record OperationView(
    Guid Id,
    Guid DeviceId,
    OperationKind Kind,
    OperationState State,
    string? Argument,
    string? ResultJson,
    string? Error,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);
