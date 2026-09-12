namespace NexaGrid.Contracts;

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
    bool IsSimulation = false);

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
    ListDirectory
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
