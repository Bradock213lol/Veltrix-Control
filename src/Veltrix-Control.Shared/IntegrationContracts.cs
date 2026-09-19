namespace VeltrixControl.Contracts;

public sealed record IntegrationRequest(
    string Kind,
    string Name,
    string BaseUrl,
    string? Credential,
    bool Enabled);

public sealed record IntegrationView(
    Guid Id,
    string Kind,
    string Name,
    string BaseUrl,
    bool Enabled,
    bool HasCredential,
    string? HealthState,
    string? HealthDetail,
    DateTimeOffset? LastCheckedAt,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record IntegrationResourceView(
    string Id,
    string Name,
    string Status,
    string Detail);

public sealed record IntegrationActionRequest(
    string Action,
    string ResourceId,
    bool Confirmed);

public static class IntegrationKinds
{
    public static readonly string[] Known = ["Pterodactyl", "Docker"];

    public static bool IsKnown(string kind) => Known.Contains(kind, StringComparer.OrdinalIgnoreCase);
}

public static class IntegrationActions
{
    public static readonly string[] Known = ["start", "stop", "restart"];

    public static bool IsKnown(string action) => Known.Contains(action, StringComparer.OrdinalIgnoreCase);
}

public static class IntegrationLimits
{
    public const int MaxIntegrations = 50;
    public const int MaxNameLength = 128;
    public const int MaxUrlLength = 512;
    public const int MaxCredentialLength = 4096;
    public const int MaxResources = 500;
}
