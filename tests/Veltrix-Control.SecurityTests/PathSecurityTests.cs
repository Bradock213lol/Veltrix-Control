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
}
