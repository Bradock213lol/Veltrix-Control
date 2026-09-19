using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Files;

namespace VeltrixControl.Agent.GameServers;

public sealed partial class MinecraftJavaAdapter(AgentOptions options, ILogger<MinecraftJavaAdapter> logger)
{
    private const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public async Task<GameServerProvisionResult> ProvisionAsync(GameServerProvisionArgument argument, CancellationToken cancellationToken)
    {
        var installPath = PathGuard.ResolveWithinRoot(options.ManagedRoot, argument.InstallPath, allowRoot: false);
        Directory.CreateDirectory(installPath);
        var (version, url, sha1) = await ResolveServerAsync(argument.Version, cancellationToken);
        var jarPath = Path.Combine(installPath, "server.jar");
        if (!File.Exists(jarPath) || !HashMatches(jarPath, sha1))
        {
            await DownloadAsync(url, jarPath, cancellationToken);
            if (!HashMatches(jarPath, sha1))
            {
                TryDelete(jarPath);
                throw new InvalidDataException("The Minecraft server jar failed SHA-1 verification and was removed.");
            }
        }

        var eulaPath = Path.Combine(installPath, "eula.txt");
        if (!File.Exists(eulaPath))
        {
            await File.WriteAllTextAsync(eulaPath, $"# Accepted by Veltrix-Control on {DateTimeOffset.UtcNow:O}\neula=true\n", cancellationToken);
        }

        var propertiesPath = Path.Combine(installPath, "server.properties");
        if (!File.Exists(propertiesPath))
        {
            await File.WriteAllTextAsync(propertiesPath, BuildProperties(argument.Port), cancellationToken);
        }

        var java = FindJava() ?? throw new InvalidOperationException("Java 21 or newer is required to run Minecraft Java servers. Install the Microsoft OpenJDK package and retry.");
        var memory = Math.Clamp(argument.MemoryMb, GameServerLimits.MinMemoryMb, GameServerLimits.MaxMemoryMb);
        var result = new GameServerProvisionResult("MinecraftJava", PathGuard.RelativeToRoot(options.ManagedRoot, installPath), version, java,
            $"-Xms{memory}M -Xmx{memory}M -jar \"{jarPath}\" nogui", new FileInfo(jarPath).Length, sha1);
        LogProvisioned(logger, argument.ServerId, version);
        return result;
    }

    public async Task<GameServerProvisionResult> UpdateAsync(GameServerUpdateArgument argument, CancellationToken cancellationToken)
    {
        var installPath = PathGuard.ResolveWithinRoot(options.ManagedRoot, argument.InstallPath, allowRoot: false);
        if (!Directory.Exists(installPath)) throw new DirectoryNotFoundException("The game server install path does not exist.");
        return await ProvisionAsync(new GameServerProvisionArgument(argument.ServerId, argument.Adapter, argument.InstallPath, 25565, GameServerLimits.MinMemoryMb, argument.Version), cancellationToken);
    }

    public static string? FindJava()
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var candidate = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "java.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }
        return null;
    }

    private static async Task<(string Version, string Url, string Sha1)> ResolveServerAsync(string? requestedVersion, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var manifestJson = await client.GetStringAsync(VersionManifestUrl, cancellationToken);
        using var manifest = JsonDocument.Parse(manifestJson);
        var latest = manifest.RootElement.GetProperty("latest").GetProperty("release").GetString()
            ?? throw new InvalidDataException("The Minecraft version manifest did not include a release version.");
        var version = string.IsNullOrWhiteSpace(requestedVersion) ? latest : requestedVersion.Trim();

        string? versionUrl = null;
        foreach (var entry in manifest.RootElement.GetProperty("versions").EnumerateArray())
        {
            if (string.Equals(entry.GetProperty("id").GetString(), version, StringComparison.OrdinalIgnoreCase))
            {
                versionUrl = entry.GetProperty("url").GetString();
                break;
            }
        }
        if (versionUrl is null) throw new InvalidDataException($"Minecraft version '{version}' was not found in the official manifest.");

        var versionJson = await client.GetStringAsync(versionUrl, cancellationToken);
        using var versionDocument = JsonDocument.Parse(versionJson);
        var server = versionDocument.RootElement.GetProperty("downloads").GetProperty("server");
        return (version, server.GetProperty("url").GetString()!, server.GetProperty("sha1").GetString()!);
    }

    private static async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static string BuildProperties(int port) => string.Join('\n',
        "#Minecraft server properties",
        $"server-port={Math.Clamp(port, 1, GameServerLimits.MaxPort)}",
        "max-players=20",
        "motd=Managed by Veltrix-Control",
        "online-mode=true",
        "enable-command-block=false",
        "view-distance=10",
        "difficulty=normal",
        "spawn-protection=16",
        string.Empty);

    private static bool HashMatches(string path, string expectedSha1)
    {
        // Mojang publishes only SHA-1 for server jars; this matches the official manifest,
        // so the weak algorithm is an integrity check, not a security boundary.
#pragma warning disable CA5350
        using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(SHA1.HashData(stream)), expectedSha1, StringComparison.OrdinalIgnoreCase);
#pragma warning restore CA5350
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(150, LogLevel.Information, "Provisioned Minecraft Java server {serverId} version {version}")]
    private static partial void LogProvisioned(ILogger logger, Guid serverId, string version);
}
