using VeltrixControl.Core.Security;

namespace VeltrixControl.Controller.Security;

public static class PermissionExtensions
{
    public static bool HasPermission(this System.Security.Claims.ClaimsPrincipal user, string permission) =>
        RolePermissions.HasPermission(user.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? string.Empty, permission);
}
