using System.Text.Json;
using System.Text.RegularExpressions;
using VeltrixControl.Contracts;

namespace VeltrixControl.Controller.Validation;

public static partial class InputValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[a-zA-Z0-9._-]{3,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    public static string? Setup(SetupRequest request)
    {
        if (request.Username is null || request.Password is null) return "Username and password are required.";
        if (!UsernamePattern().IsMatch(request.Username)) return "Username must be 3-64 characters and use letters, numbers, dots, dashes, or underscores.";
        if (request.Password.Length < 12 || request.Password.Length > 256) return "Password must be between 12 and 256 characters.";
        return null;
    }

    public static string? Enrollment(EnrollmentRequest request)
    {
        if (request.Code is null || request.DeviceName is null || request.DevicePublicKey is null || request.Inventory is null) return "Enrollment request is incomplete.";
        if (request.Code.Length is < 16 or > 64) return "Enrollment code is invalid.";
        if (string.IsNullOrWhiteSpace(request.DeviceName) || request.DeviceName.Length > 128) return "Device name is required and must not exceed 128 characters.";
        if (request.DevicePublicKey.Length is < 64 or > 2048) return "Device public key is invalid.";
        if (!ValidInventory(request.Inventory)) return "Hardware inventory is invalid.";
        return null;
    }

    public static string? Heartbeat(HeartbeatPayload payload)
    {
        if (payload.DeviceName is null || payload.Inventory is null || payload.Telemetry is null) return "Heartbeat is incomplete.";
        if (string.IsNullOrWhiteSpace(payload.DeviceName) || payload.DeviceName.Length > 128) return "Device name is invalid.";
        if (!ValidInventory(payload.Inventory) || !ValidDisks(payload.Telemetry.Disks)) return "Hardware telemetry is invalid.";
        if (payload.Telemetry.CpuPercent is < 0 or > 100) return "CPU telemetry is outside the allowed range.";
        if (payload.Telemetry.UsedMemoryBytes < 0 || payload.Telemetry.TotalMemoryBytes < payload.Telemetry.UsedMemoryBytes) return "Memory telemetry is invalid.";
        if (payload.Telemetry.UptimeSeconds < 0) return "Uptime telemetry is invalid.";
        return null;
    }

    public static string? Login(LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 64) return "Login request is invalid.";
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 256) return "Login request is invalid.";
        return null;
    }

    public static string? Transfer(TransferRequest request)
    {
        if (request.Path is null) return "A file path is required.";
        if (!ValidPath(request.Path)) return "The transfer path is invalid.";
        if (request.TotalBytes is < 0 or > FileOperationLimits.MaxTransferBytes) return "The transfer size is outside the allowed range.";
        if (request.Sha256 is not null && !Sha256Pattern().IsMatch(request.Sha256)) return "The transfer checksum is invalid.";
        if (request.Direction == TransferDirection.Download && request.TotalBytes != 0) return "Download size is determined by the managed node.";
        return null;
    }

    public static string? OperationArgument(OperationKind kind, string? argument)
    {
        switch (kind)
        {
            case OperationKind.Restart:
            case OperationKind.Shutdown:
            case OperationKind.Logoff:
            case OperationKind.Sleep:
            case OperationKind.Hibernate:
                return string.IsNullOrEmpty(argument) ? null : "This operation does not accept an argument.";
            case OperationKind.ListDirectory:
            case OperationKind.DirectorySize:
            case OperationKind.ListFileBackups:
                return ValidOptionalPath(argument) ? null : "A valid file path is required.";
            case OperationKind.CreateDirectory:
            case OperationKind.CreateFile:
            case OperationKind.DeleteFile:
            case OperationKind.ReadTextFile:
                if (!ValidPath(argument)) return "A valid file path is required.";
                return null;
            case OperationKind.RenameFile:
            case OperationKind.MoveFile:
            case OperationKind.CopyFile:
            case OperationKind.CreateArchive:
            case OperationKind.ExtractArchive:
                var pair = Parse<TwoPathArgument>(argument);
                if (pair is null || !ValidPath(pair.Path) || !ValidPath(pair.Destination)) return "A valid source and destination are required.";
                return null;
            case OperationKind.SearchFiles:
                var search = Parse<SearchArgument>(argument);
                if (search is null || !ValidOptionalPath(search.Path)) return "A valid search path is required.";
                if (string.IsNullOrWhiteSpace(search.Query) || search.Query.Length > 128) return "The search query must be between 1 and 128 characters.";
                if (search.Query.Contains(':') || search.Query.Contains('\\') || search.Query.Contains('/')) return "The search query contains invalid characters.";
                return null;
            case OperationKind.WriteTextFile:
                var write = Parse<WriteTextFileArgument>(argument);
                if (write is null || !ValidPath(write.Path)) return "A valid file path is required.";
                if (write.Content is null || write.Content.Length > FileOperationLimits.MaxEditorContentLength) return "The editor content exceeds the allowed size.";
                if (write.ExpectedHash is not null && !Sha256Pattern().IsMatch(write.ExpectedHash)) return "The expected file hash is invalid.";
                return null;
            case OperationKind.RestoreFileBackup:
                var restore = Parse<RestoreFileBackupArgument>(argument);
                if (restore is null || !ValidPath(restore.Path) || restore.BackupName is null || restore.BackupName.Length is < 5 or > 64) return "A valid file path and backup name are required.";
                return null;
            case OperationKind.ReadFileBackup:
                var readBackup = Parse<ReadFileBackupArgument>(argument);
                if (readBackup is null || !ValidPath(readBackup.Path) || readBackup.BackupName is null || readBackup.BackupName.Length is < 5 or > 64) return "A valid file path and backup name are required.";
                return null;
            case OperationKind.StartProcess:
                var startProcess = Parse<StartProcessArgument>(argument);
                if (startProcess is null || string.IsNullOrWhiteSpace(startProcess.FileName) || startProcess.FileName.Length > 1024) return "A valid executable path is required.";
                if (startProcess.Arguments is { Length: > 2048 } || startProcess.WorkingDirectory is { Length: > 1024 }) return "The process arguments exceed the allowed size.";
                return null;
            case OperationKind.StopProcess:
                var stopProcess = Parse<ProcessTargetArgument>(argument);
                if (stopProcess is null || stopProcess.ProcessId <= 0 || string.IsNullOrWhiteSpace(stopProcess.ProcessName) || stopProcess.ProcessName.Length > 256) return "A valid process target is required.";
                return null;
            case OperationKind.SetProcessPriority:
                var priority = Parse<ProcessPriorityArgument>(argument);
                if (priority is null || priority.ProcessId <= 0 || string.IsNullOrWhiteSpace(priority.ProcessName) || priority.ProcessName.Length > 256) return "A valid process target is required.";
                if (!AdminLimits.ProcessPriorities.Contains(priority.PriorityClass, StringComparer.OrdinalIgnoreCase)) return "The requested priority class is invalid.";
                return null;
            case OperationKind.StartService:
            case OperationKind.StopService:
                var service = Parse<ServiceTargetArgument>(argument);
                if (service is null || string.IsNullOrWhiteSpace(service.Name) || service.Name.Length > 256) return "A valid service name is required.";
                return null;
            case OperationKind.SetServiceStartType:
                var startType = Parse<ServiceStartTypeArgument>(argument);
                if (startType is null || string.IsNullOrWhiteSpace(startType.Name) || startType.Name.Length > 256) return "A valid service name is required.";
                if (!AdminLimits.ServiceStartTypes.Contains(startType.StartType, StringComparer.OrdinalIgnoreCase)) return "The requested startup type is invalid.";
                return null;
            case OperationKind.ScheduleRestart:
            case OperationKind.ScheduleShutdown:
                var scheduled = Parse<ScheduledPowerArgument>(argument);
                if (scheduled is null || scheduled.DelaySeconds is < 0 or > AdminLimits.MaxScheduledPowerSeconds) return "The scheduled delay is outside the allowed range.";
                return null;
            case OperationKind.TerminalStart:
                var terminalStart = Parse<TerminalStartArgument>(argument);
                if (terminalStart is null || terminalStart.SessionId == Guid.Empty) return "A valid terminal session is required.";
                if (!Enum.TryParse<TerminalShell>(terminalStart.Shell, ignoreCase: true, out _)) return "The requested shell is invalid.";
                if (string.IsNullOrWhiteSpace(terminalStart.WorkingDirectory) || terminalStart.WorkingDirectory.Length > 1024 || terminalStart.WorkingDirectory.Contains('\0')) return "A valid working directory is required.";
                return null;
            case OperationKind.TerminalInput:
                var terminalInput = Parse<TerminalInputArgument>(argument);
                if (terminalInput is null || terminalInput.SessionId == Guid.Empty) return "A valid terminal session is required.";
                if (string.IsNullOrEmpty(terminalInput.Data) || terminalInput.Data.Length > AdminLimits.MaxCommandLength) return "The command is empty or exceeds the allowed length.";
                return null;
            case OperationKind.TerminalOutput:
                var terminalOutput = Parse<TerminalOutputArgument>(argument);
                if (terminalOutput is null || terminalOutput.SessionId == Guid.Empty || terminalOutput.SinceSequence < 0) return "A valid terminal output request is required.";
                return null;
            case OperationKind.TerminalStop:
                var terminalStop = Parse<TerminalStopArgument>(argument);
                if (terminalStop is null || terminalStop.SessionId == Guid.Empty) return "A valid terminal session is required.";
                return null;
            case OperationKind.CreateBackup:
                var backup = Parse<BackupArgument>(argument);
                if (backup is null || backup.BackupId == Guid.Empty || !ValidPath(backup.SourcePath) || !ValidPath(backup.DestinationPath)) return "A valid backup request is required.";
                return null;
            case OperationKind.RestoreBackup:
                var restoreBackup = Parse<RestoreBackupArgument>(argument);
                if (restoreBackup is null || !ValidPath(restoreBackup.ArchivePath) || !ValidPath(restoreBackup.DestinationPath)) return "A valid restore request is required.";
                return null;
            case OperationKind.VerifyBackup:
                var verifyBackup = Parse<VerifyBackupArgument>(argument);
                if (verifyBackup is null || !ValidPath(verifyBackup.ArchivePath)) return "A valid backup archive path is required.";
                return null;
            case OperationKind.InstallSoftware:
                var install = Parse<SoftwareInstallArgument>(argument);
                if (install is null || string.IsNullOrWhiteSpace(install.PackageId) || install.PackageId.Length > 512) return "A valid software package is required.";
                if (!Enum.IsDefined(install.Source)) return "The package source is invalid.";
                if (install.SilentArgs is { Length: > 512 } || install.Version is { Length: > 64 }) return "The package arguments exceed the allowed size.";
                if (install.Source is SoftwareSource.Msi or SoftwareSource.Exe)
                {
                    if (string.IsNullOrWhiteSpace(install.Sha256) || install.Sha256.Length != 64) return "MSI and EXE packages require a SHA-256 checksum.";
                    if (install.Url is null || !Uri.TryCreate(install.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) return "MSI and EXE packages require an HTTPS download URL.";
                }
                return null;
            case OperationKind.UninstallSoftware:
            case OperationKind.UpgradeSoftware:
                var uninstall = Parse<SoftwareUninstallArgument>(argument);
                if (uninstall is null || string.IsNullOrWhiteSpace(uninstall.PackageId) || uninstall.PackageId.Length > 512) return "A valid software package is required.";
                if (!Enum.IsDefined(uninstall.Source)) return "The package source is invalid.";
                return null;
            case OperationKind.InstallWindowsUpdate:
                var updates = Parse<WindowsUpdateInstallArgument>(argument);
                if (updates?.UpdateIds is null || updates.UpdateIds.Length is 0 or > SoftwareLimits.MaxWindowsUpdatesPerInstall) return "Select between 1 and 100 updates.";
                if (updates.UpdateIds.Any(id => !Guid.TryParse(id, out _))) return "The update identifiers are invalid.";
                return null;
            case OperationKind.RunComputeJob:
                var job = Parse<ComputeJobArgument>(argument);
                if (job is null || job.JobId == Guid.Empty || string.IsNullOrWhiteSpace(job.Executable) || job.Executable.Length > 1024) return "A valid compute job is required.";
                if (job.Arguments is { Length: > 2048 } || job.WorkingDirectory is { Length: > 1024 }) return "The job arguments exceed the allowed size.";
                if (job.TimeoutSeconds is < ComputeLimits.MinTimeoutSeconds or > ComputeLimits.MaxTimeoutSeconds) return "The job timeout is outside the allowed range.";
                return null;
            case OperationKind.CancelComputeJob:
                var cancelJob = Parse<ComputeCancelArgument>(argument);
                if (cancelJob is null || cancelJob.JobId == Guid.Empty) return "A valid job identifier is required.";
                return null;
            default:
                return argument is null || argument.Length <= 4096 ? null : "The operation argument exceeds the allowed size.";
        }
    }

    private static bool ValidOptionalPath(string? path) =>
        string.IsNullOrEmpty(path) || ValidPath(path);

    private static bool ValidPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= FileOperationLimits.MaxPathLength && !path.Contains('\0') && !path.Contains(':');

    private static T? Parse<T>(string? argument) where T : class
    {
        if (string.IsNullOrWhiteSpace(argument) || argument.Length > 300_000) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(argument, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ValidInventory(HardwareInventory inventory) =>
        !string.IsNullOrWhiteSpace(inventory.OperatingSystem) && inventory.OperatingSystem.Length <= 256 &&
        !string.IsNullOrWhiteSpace(inventory.OsVersion) && inventory.OsVersion.Length <= 128 &&
        !string.IsNullOrWhiteSpace(inventory.Architecture) && inventory.Architecture.Length <= 64 &&
        inventory.LogicalProcessors is > 0 and <= 4096 &&
        inventory.TotalMemoryBytes >= 0 &&
        !string.IsNullOrWhiteSpace(inventory.AgentVersion) && inventory.AgentVersion.Length <= 64 &&
        ValidDisks(inventory.Disks);

    private static bool ValidDisks(IReadOnlyList<DiskInventory>? disks) =>
        disks is not null && disks.Count <= 256 && disks.All(disk =>
            disk is not null &&
            !string.IsNullOrWhiteSpace(disk.Name) && disk.Name.Length <= 256 &&
            disk.Format is not null && disk.Format.Length <= 64 &&
            disk.TotalBytes >= 0 && disk.AvailableBytes >= 0 && disk.AvailableBytes <= disk.TotalBytes);
}
