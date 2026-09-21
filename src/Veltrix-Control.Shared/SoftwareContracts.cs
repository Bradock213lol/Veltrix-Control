namespace VeltrixControl.Contracts;

public enum SoftwareSource
{
    Winget,
    Msi,
    Exe,
    Choco
}

public enum SoftwareAction
{
    Install,
    Uninstall,
    Upgrade
}

public enum DeploymentState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
    PartialSuccess
}

public sealed record SoftwarePackageRequest(
    string Name,
    SoftwareSource Source,
    string PackageId,
    string? Version,
    string? Sha256,
    string? SilentArgs);

public sealed record SoftwarePackageView(
    Guid Id,
    string Name,
    string Source,
    string PackageId,
    string? Version,
    string? Sha256,
    string? SilentArgs,
    string CreatedBy,
    DateTimeOffset CreatedAt);

public sealed record SoftwareDeploymentRequest(
    Guid PackageId,
    SoftwareAction Action,
    Guid[] DeviceIds,
    bool Confirmed);

public sealed record SoftwareDeploymentTargetView(
    Guid Id,
    Guid DeviceId,
    string DeviceName,
    string State,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record SoftwareDeploymentView(
    Guid Id,
    Guid PackageId,
    string PackageName,
    string Action,
    string State,
    string RequestedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    int SuccessCount,
    int FailureCount,
    IReadOnlyList<SoftwareDeploymentTargetView> Targets);

public sealed record SoftwareInstallArgument(
    SoftwareSource Source,
    string PackageId,
    string? Version,
    string? Url,
    string? Sha256,
    string? SilentArgs);

public sealed record SoftwareUninstallArgument(
    SoftwareSource Source,
    string PackageId,
    string? Name);

public sealed record SoftwareActionResult(
    string Source,
    string PackageId,
    string Action,
    int ExitCode,
    string Output);

public sealed record WindowsUpdateInfo(
    string UpdateId,
    string Title,
    string? KbArticle,
    long? SizeBytes,
    string? Severity,
    bool Downloaded);

public sealed record WindowsUpdateScanResult(
    DateTimeOffset ScannedAt,
    IReadOnlyList<WindowsUpdateInfo> Updates,
    string? Error);

public sealed record WindowsUpdateInstallArgument(string[] UpdateIds);

public sealed record WindowsUpdateInstallResult(
    int Selected,
    int Installed,
    int Failed,
    bool RebootRequired,
    string Output);

public static class SoftwareLimits
{
    public const int MaxPackages = 500;
    public const int MaxDeploymentTargets = 200;
    public const int MaxOutputLength = 16 * 1024;
    public const long MaxInstallerBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxWindowsUpdatesPerInstall = 100;
}
