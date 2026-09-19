using Microsoft.Extensions.Logging.Abstractions;
using VeltrixControl.Agent;
using VeltrixControl.Agent.GameServers;
using VeltrixControl.Contracts;

namespace VeltrixControl.AgentTests;

public sealed class GameServerOperationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"veltrix-gameserver-{Guid.NewGuid():N}");
    private readonly AgentOptions _options;

    public GameServerOperationsTests()
    {
        Directory.CreateDirectory(_root);
        _options = new AgentOptions { ManagedRoot = _root, DataDirectory = Path.Combine(_root, ".agent") };
    }

    [Fact]
    public async Task UnknownAdapterAndUnsafePathsAreRejected()
    {
        var operations = Create();
        var unknown = await operations.ExecuteAsync(Assignment(OperationKind.GameServerProvision,
            new GameServerProvisionArgument(Guid.NewGuid(), "Terraria", "terraria", 7777, 1024, null)), CancellationToken.None);
        Assert.Equal(OperationState.Failed, unknown.State);

        var escape = await operations.ExecuteAsync(Assignment(OperationKind.GameServerProvision,
            new GameServerProvisionArgument(Guid.NewGuid(), "MinecraftJava", "..\\..\\escape", 25565, 1024, null)), CancellationToken.None);
        Assert.Equal(OperationState.Failed, escape.State);

        var empty = await operations.ExecuteAsync(Assignment(OperationKind.GameServerProvision,
            new GameServerProvisionArgument(Guid.Empty, "MinecraftJava", "servers\\one", 25565, 1024, null)), CancellationToken.None);
        Assert.Equal(OperationState.Failed, empty.State);
    }

    [Fact]
    public async Task StartRequiresProvisionedFiles()
    {
        var operations = Create();
        var start = await operations.ExecuteAsync(Assignment(OperationKind.GameServerStart,
            new GameServerStartArgument(Guid.NewGuid(), "servers\\missing", 1024, 25565)), CancellationToken.None);
        Assert.Equal(OperationState.Failed, start.State);
        Assert.Contains("not provisioned", start.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutputForStoppedServerIsRejected()
    {
        var operations = Create();
        var output = await operations.ExecuteAsync(Assignment(OperationKind.GameServerOutput,
            new GameServerOutputArgument(Guid.NewGuid(), 0)), CancellationToken.None);
        Assert.Equal(OperationState.Failed, output.State);
        Assert.Contains("not running", output.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GameServerManagerTracksFixtureProcess()
    {
        var manager = new GameServerManager(NullLogger<GameServerManager>.Instance);
        var serverId = Guid.NewGuid();
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var process = manager.Start(serverId, ping, "-n 30 127.0.0.1", _root);

        Assert.True(process.Sequence >= 0);
        Assert.NotNull(manager.Get(serverId));
        Assert.True(manager.Stop(serverId, graceful: false));
        Assert.Null(manager.Get(serverId));

        Assert.False(manager.Stop(Guid.NewGuid(), graceful: true));
    }

    [Fact]
    public void GameServerManagerRejectsDoubleStart()
    {
        var manager = new GameServerManager(NullLogger<GameServerManager>.Instance);
        var serverId = Guid.NewGuid();
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        manager.Start(serverId, ping, "-n 30 127.0.0.1", _root);
        try
        {
            Assert.Throws<InvalidOperationException>(() => manager.Start(serverId, ping, "-n 30 127.0.0.1", _root));
        }
        finally
        {
            manager.Stop(serverId, graceful: false);
        }
    }

    private GameServerOperations Create() => new(_options,
        new MinecraftJavaAdapter(_options, NullLogger<MinecraftJavaAdapter>.Instance),
        new GameServerManager(NullLogger<GameServerManager>.Instance),
        NullLogger<GameServerOperations>.Instance);

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static OperationAssignment Assignment<T>(OperationKind kind, T argument) => new(Guid.NewGuid(), kind,
        System.Text.Json.JsonSerializer.Serialize(argument, JsonOptions),
        DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
