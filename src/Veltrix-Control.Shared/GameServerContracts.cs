namespace VeltrixControl.Contracts;

public sealed record GameServerRequest(
    string Name,
    string Adapter,
    string InstallPath,
    int Port,
    int MemoryMb,
    int CpuThreads,
    bool AutoRestart,
    string? Version,
    string? EnvironmentJson);

public sealed record GameServerView(
    Guid Id,
    Guid DeviceId,
    string DeviceName,
    string Name,
    string Adapter,
    string State,
    string InstallPath,
    int Port,
    int MemoryMb,
    int CpuThreads,
    bool AutoRestart,
    string? Version,
    string? EnvironmentJson,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastStoppedAt);

public sealed record GameServerEventView(
    long Id,
    Guid ServerId,
    string Kind,
    string? Data,
    DateTimeOffset CreatedAt);

public sealed record GameServerProvisionArgument(
    Guid ServerId,
    string Adapter,
    string InstallPath,
    int Port,
    int MemoryMb,
    string? Version);

public sealed record GameServerStartArgument(
    Guid ServerId,
    string InstallPath,
    int MemoryMb,
    int Port);

public sealed record GameServerStopArgument(
    Guid ServerId);

public sealed record GameServerUpdateArgument(
    Guid ServerId,
    string Adapter,
    string InstallPath,
    string? Version);

public sealed record GameServerInputArgument(
    Guid ServerId,
    string Data);

public sealed record GameServerOutputArgument(
    Guid ServerId,
    long SinceSequence);

public sealed record GameServerProvisionResult(
    string Adapter,
    string InstallPath,
    string Version,
    string Executable,
    string Arguments,
    long JarBytes,
    string Sha1);

public sealed record GameServerStatusResult(
    Guid ServerId,
    bool Running,
    bool Exited,
    int? ExitCode,
    long Sequence,
    string Output);

public static class GameServerAdapters
{
    public static readonly string[] Known = ["MinecraftJava"];

    public static bool IsKnown(string adapter) => Known.Contains(adapter, StringComparer.OrdinalIgnoreCase);
}

public static class GameServerLimits
{
    public const int MaxServersPerDevice = 20;
    public const int MaxPort = 65535;
    public const int MinMemoryMb = 512;
    public const int MaxMemoryMb = 1_048_576;
    public const int MaxThreads = 256;
    public const int MaxOutputChunk = 64 * 1024;
    public const int BufferBytes = 512 * 1024;
    public const int CrashLoopThreshold = 3;
    public const int CrashLoopWindowMinutes = 10;
}
