namespace VeltrixControl.Core.Files;

public static class PathGuard
{
    public static string ResolveWithinRoot(string root, string? relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath ?? string.Empty));
        var relative = Path.GetRelativePath(fullRoot, candidate);

        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The requested path is outside the managed root.");
        }

        return candidate;
    }
}
