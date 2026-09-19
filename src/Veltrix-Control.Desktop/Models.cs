using VeltrixControl.Contracts;
using System.Globalization;

namespace VeltrixControl.Desktop;

public sealed record SetupStatus(bool Required);
public sealed record UserIdentity(string Username, string Role);
public sealed record ControllerInfo(int HttpsPort, string? CertificateSha256);
public sealed record AuditIntegrity(bool Valid);
public sealed record ErrorEnvelope(string? Error);

public sealed class DeviceRow(DeviceSummary source)
{
    public DeviceSummary Source { get; } = source;
    public Guid Id => Source.Id;
    public string Name => Source.Name;
    public string Status => Source.Online ? "Online" : "Offline";
    public string Cpu => Source.Telemetry is null ? "—" : $"{Source.Telemetry.CpuPercent:0}%";
    public string Memory => Source.Telemetry is null ? "—" : FormatBytes(Source.Telemetry.UsedMemoryBytes);
    public int Health => Source.HealthScore;
    public string LastSeen => Source.LastHeartbeat.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string OperatingSystem => Source.Inventory.OperatingSystem;
    public string Agent => Source.Inventory.IsSimulation ? "Simulator" : $"v{Source.Inventory.AgentVersion}";

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, (double)bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
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
