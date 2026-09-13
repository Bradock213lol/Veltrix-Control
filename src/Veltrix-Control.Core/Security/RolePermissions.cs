namespace VeltrixControl.Core.Security;

public static class RolePermissions
{
    private static readonly Dictionary<string, HashSet<string>> Permissions =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Owner"] = ["*"],
            ["Administrator"] = ["device.view", "device.power", "device.files", "admin.manage", "audit.view"],
            ["Operator"] = ["device.view", "device.power", "device.files", "audit.view"],
            ["Viewer"] = ["device.view", "audit.view"]
        };

    public static bool HasPermission(string role, string permission) =>
        Permissions.TryGetValue(role, out var permissions) &&
        (permissions.Contains("*") || permissions.Contains(permission));
}
