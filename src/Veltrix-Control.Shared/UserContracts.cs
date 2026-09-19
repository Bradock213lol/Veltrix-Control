namespace VeltrixControl.Contracts;

public sealed record UserView(
    Guid Id,
    string Username,
    string Role,
    DateTimeOffset CreatedAt);

public sealed record CreateUserRequest(
    string Username,
    string Password,
    string Role);

public sealed record UpdateUserRoleRequest(string Role);

public sealed record ResetPasswordRequest(string Password);

public static class UserRoles
{
    public static readonly string[] All = ["Owner", "Administrator", "Operator", "Viewer"];

    public static bool IsKnown(string role) => All.Contains(role, StringComparer.OrdinalIgnoreCase);

    public static string Canonical(string role) => All.First(item => string.Equals(item, role, StringComparison.OrdinalIgnoreCase));
}
