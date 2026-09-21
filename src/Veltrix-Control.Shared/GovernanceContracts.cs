namespace VeltrixControl.Contracts;

public sealed record DeviceTagRequest(string Tag);

public sealed record DeviceTagView(
    Guid DeviceId,
    string Tag,
    string AddedBy,
    DateTimeOffset AddedAt);

public sealed record NotificationSettingsRequest(
    bool Enabled,
    string? WebhookUrl,
    string? Secret);

public sealed record NotificationSettingsView(
    bool Enabled,
    string? WebhookUrl,
    bool HasSecret,
    DateTimeOffset? UpdatedAt);

public static class TagLimits
{
    public const int MaxTagLength = 32;
    public const int MaxTagsPerDevice = 20;

    public static bool IsValidTag(string tag) =>
        !string.IsNullOrWhiteSpace(tag) && tag.Length <= MaxTagLength &&
        tag.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ' ');
}
