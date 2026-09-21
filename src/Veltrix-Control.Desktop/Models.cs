using VeltrixControl.Contracts;
using VeltrixControl.Core.Formatting;
using System.Globalization;

namespace VeltrixControl.Desktop;

public sealed record SetupStatus(bool Required);
public sealed record UserIdentity(string Username, string Role);
public sealed record ControllerInfo(int HttpsPort, string? CertificateSha256);
public sealed record AuditIntegrity(bool Valid);
public sealed record ErrorEnvelope(string? Error);

public sealed class DeviceRow(DeviceSummary source)
{
    private static readonly System.Windows.Media.Brush OnlineBrush = Frozen(0x8D, 0xE5, 0xBD);
    private static readonly System.Windows.Media.Brush OfflineBrush = Frozen(0x81, 0x8E, 0x87);
    private static readonly System.Windows.Media.Brush HealthyBrush = Frozen(0x8D, 0xE5, 0xBD);
    private static readonly System.Windows.Media.Brush WarningBrush = Frozen(0xDF, 0xB6, 0x6E);
    private static readonly System.Windows.Media.Brush DangerBrush = Frozen(0xF2, 0x8D, 0x8D);

    public DeviceSummary Source { get; } = source;
    public Guid Id => Source.Id;
    public string Name => Source.Name;
    public string Status => Source.Online ? "Online" : "Offline";
    public System.Windows.Media.Brush StatusBrush => Source.Online ? OnlineBrush : OfflineBrush;
    public string Cpu => Source.Telemetry is null ? "—" : $"{Source.Telemetry.CpuPercent:0}%";
    public string Memory => Source.Telemetry is null ? "—" : MetricFormatter.Memory(Source.Telemetry.UsedMemoryBytes, Source.Telemetry.TotalMemoryBytes);
    public string FreeMemory => Source.Telemetry is null ? "—" : MetricFormatter.Bytes(Math.Max(0, Source.Telemetry.TotalMemoryBytes - Source.Telemetry.UsedMemoryBytes));
    public int Health => Source.HealthScore;
    public System.Windows.Media.Brush HealthBrush => Source.HealthScore >= 75 ? HealthyBrush : Source.HealthScore >= 50 ? WarningBrush : DangerBrush;
    public string HealthDetail => Source.Online ? $"{Source.HealthScore} / 100" : "Offline";
    public string LastSeen => Source.LastHeartbeat.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string OperatingSystem => Source.Inventory.OperatingSystem;
    public string Agent => Source.Inventory.IsSimulation ? "Simulator" : $"v{Source.Inventory.AgentVersion}";

    public static string FormatBytes(long bytes) => MetricFormatter.Bytes(bytes);

    private static System.Windows.Media.SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class FileRow(FileEntry source)
{
    public FileEntry Source { get; } = source;
    public string Name => Source.Name;
    public string Kind => Source.IsDirectory ? "Folder" : "File";
    public string Size => Source.IsDirectory ? "—" : DeviceRow.FormatBytes(Source.SizeBytes ?? 0);
    public string Modified => Source.LastModified.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
}

public sealed class AuditRow(AuditEventView source)
{
    public AuditEventView Source { get; } = source;
    public string Time => Source.Timestamp.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string Actor => Source.Actor;
    public string Action => Source.Action;
    public string Target => Source.Target;
    public string Outcome => Source.Outcome;
    public string Details => Source.Metadata ?? string.Empty;
}

public sealed class TransferRow(TransferView source)
{
    public TransferView Source { get; } = source;
    public Guid Id => Source.Id;
    public string Name => Source.Path;
    public string Direction => Source.Direction == TransferDirection.Upload ? "Upload" : "Download";
    public string State => Source.State.ToString();
    public double Progress => Source.TotalBytes == 0 ? 0 : Math.Clamp(Source.BytesTransferred * 100d / Source.TotalBytes, 0, 100);
    public string Transferred => Source.TotalBytes == 0
        ? DeviceRow.FormatBytes(Source.BytesTransferred)
        : $"{DeviceRow.FormatBytes(Source.BytesTransferred)} / {DeviceRow.FormatBytes(Source.TotalBytes)}";
    public string Detail => string.IsNullOrWhiteSpace(Source.Error) ? Source.RequestedBy : Source.Error!;
}

public sealed class ProcessAdminRow(ProcessSnapshot source)
{
    public ProcessSnapshot Source { get; } = source;
    public int Id => Source.Id;
    public string Name => Source.Name;
    public string Memory => DeviceRow.FormatBytes(Source.WorkingSetBytes);
    public string CpuTime => TimeSpan.FromSeconds(Source.TotalProcessorSeconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    public int Threads => Source.ThreadCount;
}

public sealed class ServiceAdminRow(ServiceSnapshot source)
{
    public ServiceSnapshot Source { get; } = source;
    public string Name => Source.Name;
    public string DisplayName => Source.DisplayName;
    public string Status => Source.Status;
    public string StartType => Source.StartType;
}

public sealed class SoftwarePackageRow(SoftwarePackageView source)
{
    public SoftwarePackageView Source { get; } = source;
    public Guid Id => Source.Id;
    public string Name => Source.Name;
    public string Source2 => Source.Source;
    public string PackageId => Source.PackageId;
    public string Version => string.IsNullOrWhiteSpace(Source.Version) ? "Any" : Source.Version!;
    public string Verified => string.IsNullOrWhiteSpace(Source.Sha256) ? "WinGet catalog" : "Checksum pinned";
}

public sealed class DeploymentRow(SoftwareDeploymentView source)
{
    public SoftwareDeploymentView Source { get; } = source;
    public Guid Id => Source.Id;
    public string Package => Source.PackageName;
    public string Action => Source.Action;
    public string State => Source.State;
    public string Progress => $"{Source.SuccessCount} ok · {Source.FailureCount} failed";
    public string Requested => Source.CreatedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
}

public sealed class DeploymentTargetRow(SoftwareDeploymentTargetView source)
{
    public string Device => string.IsNullOrWhiteSpace(source.DeviceName) ? source.DeviceId.ToString("D") : source.DeviceName;
    public string State => source.State;
    public string Detail => source.Error ?? string.Empty;
}

public sealed class WindowsUpdateRow(WindowsUpdateInfo source)
{
    public WindowsUpdateInfo Source { get; } = source;
    public bool Selected { get; set; }
    public string Title => Source.Title;
    public string Kb => Source.KbArticle ?? string.Empty;
    public string Severity => Source.Severity ?? string.Empty;
    public string Size => Source.SizeBytes is null ? string.Empty : DeviceRow.FormatBytes(Source.SizeBytes.Value);
    public string Downloaded => Source.Downloaded ? "Downloaded" : "Pending";
}

public sealed class BackupRowView(BackupView source)
{
    public BackupView Source { get; } = source;
    public Guid Id => Source.Id;
    public string Name => Source.Name;
    public string Source2 => Source.SourcePath;
    public string State => Source.State;
    public string Size => Source.SizeBytes == 0 ? "—" : DeviceRow.FormatBytes(Source.SizeBytes);
    public string Created => Source.CreatedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string Detail => Source.Error ?? Source.ArchivePath;
}

public sealed class AlertRowView(AlertView source)
{
    public AlertView Source { get; } = source;
    public Guid Id => Source.Id;
    public string Severity => Source.Severity;
    public string Code => Source.Code;
    public string Title => Source.Title;
    public string Message => Source.Message;
    public string State => Source.State;
    public string Created => Source.CreatedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string Acknowledged => Source.AcknowledgedBy ?? string.Empty;
}

public sealed class AutomationRowView(AutomationView source)
{
    public AutomationView Source { get; } = source;
    public Guid Id => Source.Id;
    public string Name => Source.Name;
    public string Enabled => Source.Enabled ? "Enabled" : "Disabled";
    public string Trigger => Source.TriggerJson;
    public string Action => Source.ActionJson;
    public string Runs => Source.RunCount.ToString(CultureInfo.CurrentCulture);
    public string LastRun => Source.LastRunAt?.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) ?? "Never";
}

public sealed class AutomationRunRowView(AutomationRunView source)
{
    public string Automation => source.AutomationId.ToString("N")[..8];
    public string Device => source.DeviceId?.ToString("N")[..8] ?? "—";
    public string State => source.State;
    public string Detail => source.Detail ?? string.Empty;
    public string Started => source.StartedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
}

public sealed class ComputeJobRowView(ComputeJobView source)
{
    public ComputeJobView Source { get; } = source;
    public Guid Id => Source.Id;
    public string Name => Source.Name;
    public string State => Source.State;
    public int Priority => Source.Priority;
    public string Device => Source.AssignedDeviceId?.ToString("N")[..8] ?? "—";
    public string Attempts => $"{Source.Attempts}/{Source.MaxAttempts}";
    public string Created => Source.CreatedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string Detail => Source.Error ?? Source.ResultJson ?? string.Empty;
}

public sealed class GameServerRowView(GameServerView source)
{
    public GameServerView Source { get; } = source;
    public Guid Id => Source.Id;
    public string Name => Source.Name;
    public string Device => Source.DeviceName;
    public string Adapter => Source.Adapter;
    public string State => Source.State;
    public string Address => $":{Source.Port}";
    public string Version => Source.Version ?? "—";
    public string AutoRestart => Source.AutoRestart ? "Auto restart" : "Manual";
}

public sealed class IntegrationRowView(IntegrationView source)
{
    public IntegrationView Source { get; } = source;
    public Guid Id => Source.Id;
    public string Name => Source.Name;
    public string Kind => Source.Kind;
    public string Endpoint => Source.BaseUrl;
    public string Enabled => Source.Enabled ? "Enabled" : "Disabled";
    public string Health => Source.HealthState ?? "Unknown";
    public string LastChecked => Source.LastCheckedAt?.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) ?? "Never";
    public string Detail => Source.HealthDetail ?? (Source.HasCredential ? "Credential stored" : "No credential");
}

public sealed class UserRowView(UserView source)
{
    public Guid Id => source.Id;
    public string Username => source.Username;
    public string Role => source.Role;
    public string Created => source.CreatedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
}
