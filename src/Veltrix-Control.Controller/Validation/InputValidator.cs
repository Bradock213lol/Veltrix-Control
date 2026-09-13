using System.Text.RegularExpressions;
using VeltrixControl.Contracts;

namespace VeltrixControl.Controller.Validation;

public static partial class InputValidator
{
    [GeneratedRegex("^[a-zA-Z0-9._-]{3,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    public static string? Setup(SetupRequest request)
    {
        if (request.Username is null || request.Password is null) return "Username and password are required.";
        if (!UsernamePattern().IsMatch(request.Username)) return "Username must be 3–64 characters and use letters, numbers, dots, dashes, or underscores.";
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
