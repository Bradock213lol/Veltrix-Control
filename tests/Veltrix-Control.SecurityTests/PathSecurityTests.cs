using VeltrixControl.Core.Files;

namespace VeltrixControl.SecurityTests;

public sealed class PathSecurityTests
{
    [Fact]
    public void RelativeChildPathIsAllowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "veltrixcontrol-root");
        var result = PathGuard.ResolveWithinRoot(root, Path.Combine("logs", "today"));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "logs", "today")), result);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\secret.txt")]
    [InlineData("folder\\..\\..\\Windows")]
    [InlineData("C:\\Windows")]
    public void TraversalAndAbsolutePathsAreRejected(string request)
    {
        var root = Path.Combine(Path.GetTempPath(), "veltrixcontrol-root");
        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.ResolveWithinRoot(root, request));
    }

    [Fact]
    public void PrefixCollisionCannotEscapeRoot()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "nexa");
        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.ResolveWithinRoot(basePath, "..\\nexa-secret\\file.txt"));
    }

    [Theory]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("\\\\?\\C:\\Windows\\system32")]
    [InlineData("C:relative.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("folder\0name")]
    public void NonLocalAndStreamPathsAreRejected(string request)
    {
        var root = Path.Combine(Path.GetTempPath(), "veltrixcontrol-root");
        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.ResolveWithinRoot(root, request));
    }

    [Fact]
    public void ManagedRootCannotBeModifiedButCanBeListed()
    {
        var root = Path.Combine(Path.GetTempPath(), "veltrixcontrol-root");
        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.ResolveWithinRoot(root, string.Empty));
        Assert.Equal(Path.GetFullPath(root), PathGuard.ResolveWithinRoot(root, string.Empty, allowRoot: true));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("folder/../../evil.txt")]
    [InlineData("C:/evil.txt")]
    public void ArchiveEntriesCannotEscape(string entry)
    {
        var root = Path.Combine(Path.GetTempPath(), "veltrixcontrol-root");
        Assert.Throws<UnauthorizedAccessException>(() => PathGuard.ResolveArchiveEntry(root, entry));
    }

    [Fact]
    public void ArchiveEntriesInsideRootAreAllowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "veltrixcontrol-root");
        var result = PathGuard.ResolveArchiveEntry(root, "sub/folder/file.txt");
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "sub", "folder", "file.txt")), result);
    }
}
