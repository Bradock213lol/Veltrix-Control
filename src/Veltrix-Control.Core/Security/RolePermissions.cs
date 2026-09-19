using VeltrixControl.Contracts;

namespace VeltrixControl.Core.Security;

public static class RolePermissions
{
    public const string DeviceView = "device.view";
    public const string DevicePower = "device.power";
    public const string DeviceFiles = "device.files";
    public const string DeviceProcesses = "device.processes";
    public const string DeviceServices = "device.services";
    public const string DeviceTerminal = "device.terminal";
    public const string DeviceSoftware = "device.software";
    public const string DeviceDiagnostics = "device.diagnostics";
    public const string DeviceUpdates = "device.updates";
    public const string DeviceBackup = "device.backup";
    public const string DeviceCompute = "device.compute";
    public const string GameManage = "game.manage";
    public const string DockerManage = "docker.manage";
    public const string IntegrationManage = "integration.manage";
    public const string AdminManage = "admin.manage";
    public const string AuditView = "audit.view";

    private static readonly Dictionary<string, HashSet<string>> Permissions =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Owner"] = ["*"],
            ["Administrator"] =
            [
                DeviceView, DevicePower, DeviceFiles, DeviceProcesses, DeviceServices, DeviceTerminal,
                DeviceSoftware, DeviceDiagnostics, DeviceUpdates, DeviceBackup, DeviceCompute,
                GameManage, DockerManage, IntegrationManage, AdminManage, AuditView
            ],
            ["Operator"] =
            [
                DeviceView, DevicePower, DeviceFiles, DeviceProcesses, DeviceServices, DeviceTerminal,
                DeviceDiagnostics, DeviceBackup, DeviceCompute, GameManage, AuditView
            ],
            ["Viewer"] = [DeviceView, DeviceDiagnostics, AuditView]
        };

    public static bool HasPermission(string role, string permission) =>
        Permissions.TryGetValue(role, out var permissions) &&
        (permissions.Contains("*") || permissions.Contains(permission));

    public static IReadOnlyCollection<string> ForRole(string role) =>
        Permissions.TryGetValue(role, out var permissions)
            ? permissions.Where(permission => permission != "*").Order(StringComparer.Ordinal).ToArray()
            : [];

    public static string PermissionFor(OperationKind kind) => kind switch
    {
        OperationKind.Restart or OperationKind.Shutdown or OperationKind.ScheduleRestart or OperationKind.ScheduleShutdown or
            OperationKind.Logoff or OperationKind.Sleep or OperationKind.Hibernate => DevicePower,
        OperationKind.ListDirectory or OperationKind.CreateDirectory or OperationKind.CreateFile or OperationKind.RenameFile or
            OperationKind.MoveFile or OperationKind.CopyFile or OperationKind.DeleteFile or OperationKind.SearchFiles or
            OperationKind.DirectorySize or OperationKind.CreateArchive or OperationKind.ExtractArchive or
            OperationKind.ReadTextFile or OperationKind.WriteTextFile or OperationKind.ListFileBackups or
            OperationKind.RestoreFileBackup or OperationKind.ReadFileBackup => DeviceFiles,
        OperationKind.ListProcesses or OperationKind.StartProcess or OperationKind.StopProcess or OperationKind.SetProcessPriority => DeviceProcesses,
        OperationKind.ListServices or OperationKind.StartService or OperationKind.StopService or OperationKind.SetServiceStartType => DeviceServices,
        OperationKind.TerminalStart or OperationKind.TerminalInput or OperationKind.TerminalOutput or OperationKind.TerminalStop => DeviceTerminal,
        OperationKind.ListSoftware or OperationKind.InstallSoftware or OperationKind.UninstallSoftware or OperationKind.UpgradeSoftware => DeviceSoftware,
        OperationKind.ScanWindowsUpdates or OperationKind.InstallWindowsUpdate => DeviceUpdates,
        OperationKind.CreateBackup or OperationKind.RestoreBackup or OperationKind.VerifyBackup => DeviceBackup,
        OperationKind.RunComputeJob or OperationKind.CancelComputeJob => DeviceCompute,
        OperationKind.GameServerProvision or OperationKind.GameServerStart or OperationKind.GameServerStop or OperationKind.GameServerUpdate or OperationKind.GameServerInput or OperationKind.GameServerOutput => GameManage,
        _ => DeviceDiagnostics
    };
}
