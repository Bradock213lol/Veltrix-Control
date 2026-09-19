namespace VeltrixControl.Contracts;

public sealed record BackupRequest(
    string Name,
    string SourcePath,
    string DestinationPath,
    int RetentionDays);

public sealed record BackupView(
    Guid Id,
    Guid DeviceId,
    string Name,
    string SourcePath,
    string ArchivePath,
    string State,
    long SizeBytes,
    string? Sha256,
    string? Error,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    int RetentionDays);

public sealed record BackupArgument(
    Guid BackupId,
    string SourcePath,
    string DestinationPath);

public sealed record RestoreBackupArgument(
    string ArchivePath,
    string DestinationPath);

public sealed record VerifyBackupArgument(
    string ArchivePath);

public sealed record BackupOperationResult(
    string ArchivePath,
    long SizeBytes,
    string Sha256,
    int EntryCount);

public sealed record RestoreOperationResult(
    string ArchivePath,
    string DestinationPath,
    int EntryCount);

public sealed record VerifyOperationResult(
    string ArchivePath,
    bool Valid,
    int EntryCount,
    string Sha256);

public sealed record AlertView(
    Guid Id,
    Guid? DeviceId,
    string Severity,
    string Code,
    string Title,
    string Message,
    string State,
    DateTimeOffset CreatedAt,
    string? AcknowledgedBy,
    DateTimeOffset? AcknowledgedAt,
    string? Metadata);

public sealed record AutomationTriggerRequest(
    string Kind,
    string? AlertCode,
    string? Severity,
    int? EveryMinutes);

public sealed record AutomationConditionRequest(
    string? DeviceNameContains,
    string? AlertCodeEquals);

public sealed record AutomationActionRequest(
    string Kind,
    string? AlertCode,
    string? AlertTitle,
    string? OperationKind);

public sealed record AutomationRequest(
    string Name,
    bool Enabled,
    AutomationTriggerRequest Trigger,
    AutomationConditionRequest? Condition,
    AutomationActionRequest Action,
    int CooldownSeconds);

public sealed record AutomationView(
    Guid Id,
    string Name,
    bool Enabled,
    string TriggerJson,
    string? ConditionJson,
    string ActionJson,
    int CooldownSeconds,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastRunAt,
    int RunCount);

public sealed record AutomationRunView(
    Guid Id,
    Guid AutomationId,
    Guid? DeviceId,
    string State,
    string? Detail,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

public static class MonitoringLimits
{
    public const int MaxBackupsPerDevice = 100;
    public const int MaxAutomations = 100;
    public const int MaxCooldownSeconds = 24 * 60 * 60;
    public const int MinScheduleMinutes = 5;
    public const int MaxAlertsReturned = 500;

    public static readonly string[] AlertSeverities = ["Info", "Warning", "Critical"];

    public static readonly string[] AutomationTriggers = ["AlertRaised", "DeviceOffline", "DeviceOnline", "Schedule"];

    public static readonly string[] AutomationActions = ["CreateAlert", "QueueOperation"];

    public static readonly string[] AutomationOperationKinds = ["Restart", "Shutdown", "ScanWindowsUpdates"];
}
