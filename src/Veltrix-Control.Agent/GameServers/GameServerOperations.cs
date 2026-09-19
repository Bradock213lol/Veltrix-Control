using System.Text.Json;
using System.Text.Json.Serialization;
using VeltrixControl.Contracts;
using VeltrixControl.Core.Files;

namespace VeltrixControl.Agent.GameServers;

public sealed partial class GameServerOperations(
    AgentOptions options,
    MinecraftJavaAdapter adapter,
    GameServerManager manager,
    ILogger<GameServerOperations> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool Handles(OperationKind kind) => kind is
        OperationKind.GameServerProvision or OperationKind.GameServerStart or OperationKind.GameServerStop or
        OperationKind.GameServerUpdate or OperationKind.GameServerInput or OperationKind.GameServerOutput;

    public async Task<OperationResultPayload> ExecuteAsync(OperationAssignment operation, CancellationToken cancellationToken)
    {
        try
        {
            return operation.Kind switch
            {
                OperationKind.GameServerProvision => await ProvisionAsync(operation, cancellationToken),
                OperationKind.GameServerUpdate => await UpdateAsync(operation, cancellationToken),
                OperationKind.GameServerStart => await StartAsync(operation, cancellationToken),
                OperationKind.GameServerStop => Stop(operation),
                OperationKind.GameServerInput => Input(operation),
                OperationKind.GameServerOutput => Output(operation),
                _ => Failure(operation.Id, "Unsupported game server operation.")
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or InvalidDataException or System.ComponentModel.Win32Exception or HttpRequestException or JsonException or TaskCanceledException)
        {
            LogGameServerFailed(logger, exception, operation.Id, operation.Kind);
            return Failure(operation.Id, exception.Message);
        }
    }

    private async Task<OperationResultPayload> ProvisionAsync(OperationAssignment operation, CancellationToken cancellationToken)
    {
        var argument = Deserialize<GameServerProvisionArgument>(operation);
        Validate(argument.ServerId, argument.Adapter, argument.InstallPath);
        var result = await adapter.ProvisionAsync(argument, cancellationToken);
        return Success(operation.Id, result);
    }

    private async Task<OperationResultPayload> UpdateAsync(OperationAssignment operation, CancellationToken cancellationToken)
    {
        var argument = Deserialize<GameServerUpdateArgument>(operation);
        Validate(argument.ServerId, argument.Adapter, argument.InstallPath);
        var result = await adapter.UpdateAsync(argument, cancellationToken);
        return Success(operation.Id, result);
    }

    private Task<OperationResultPayload> StartAsync(OperationAssignment operation, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var argument = Deserialize<GameServerStartArgument>(operation);
        Validate(argument.ServerId, "MinecraftJava", argument.InstallPath);
        var installPath = PathGuard.ResolveWithinRoot(options.ManagedRoot, argument.InstallPath, allowRoot: false);
        var jar = Path.Combine(installPath, "server.jar");
        if (!File.Exists(jar)) throw new FileNotFoundException("The game server is not provisioned yet.");
        var java = MinecraftJavaAdapter.FindJava() ?? throw new InvalidOperationException("Java 21 or newer is required to run Minecraft Java servers.");
        var memory = Math.Clamp(argument.MemoryMb, GameServerLimits.MinMemoryMb, GameServerLimits.MaxMemoryMb);
        var process = manager.Start(argument.ServerId, java, $"-Xms{memory}M -Xmx{memory}M -jar \"{jar}\" nogui", installPath);
        WaitForOutput(process, 2500);
        var (sequence, output) = process.Read(0);
        return Task.FromResult(Success(operation.Id, new GameServerStatusResult(argument.ServerId, !process.Exited, process.Exited, process.ExitCode, sequence, output)));
    }

    private OperationResultPayload Stop(OperationAssignment operation)
    {
        var argument = Deserialize<GameServerStopArgument>(operation);
        var stopped = manager.Stop(argument.ServerId, graceful: true);
        return Success(operation.Id, new GameServerStatusResult(argument.ServerId, false, true, null, 0, stopped ? "stop requested" : "The server was not running."));
    }

    private OperationResultPayload Input(OperationAssignment operation)
    {
        var argument = Deserialize<GameServerInputArgument>(operation);
        if (string.IsNullOrEmpty(argument.Data) || argument.Data.Length > AdminLimits.MaxCommandLength) throw new ArgumentException("The console command is empty or too long.");
        var process = manager.Get(argument.ServerId) ?? throw new InvalidOperationException("The game server is not running.");
        process.Write(argument.Data);
        WaitForOutput(process, 1200);
        var (sequence, output) = process.Read(0);
        return Success(operation.Id, new GameServerStatusResult(argument.ServerId, !process.Exited, process.Exited, process.ExitCode, sequence, output));
    }

    private OperationResultPayload Output(OperationAssignment operation)
    {
        var argument = Deserialize<GameServerOutputArgument>(operation);
        var process = manager.Get(argument.ServerId) ?? throw new InvalidOperationException("The game server is not running on this node.");
        var (sequence, output) = process.Read(argument.SinceSequence);
        return Success(operation.Id, new GameServerStatusResult(argument.ServerId, !process.Exited, process.Exited, process.ExitCode, sequence, output));
    }

    private static void WaitForOutput(GameServerProcess process, int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        var last = process.Sequence;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(100);
            if (process.Sequence != last)
            {
                last = process.Sequence;
                deadline = Environment.TickCount64 + 400;
            }
            if (process.Exited) break;
        }
    }

    private static void Validate(Guid serverId, string adapter, string installPath)
    {
        if (serverId == Guid.Empty) throw new ArgumentException("The server identifier is invalid.");
        if (!GameServerAdapters.IsKnown(adapter)) throw new ArgumentException($"Adapter '{adapter}' is not supported.");
        if (string.IsNullOrWhiteSpace(installPath) || installPath.Length > 1024) throw new ArgumentException("A valid install path is required.");
    }

    private static T Deserialize<T>(OperationAssignment operation) =>
        JsonSerializer.Deserialize<T>(operation.Argument ?? string.Empty, JsonOptions)
        ?? throw new ArgumentException("The operation argument is invalid.");

    private static OperationResultPayload Success(Guid id, object? result) =>
        new(id, OperationState.Succeeded, result is null ? null : JsonSerializer.Serialize(result, JsonOptions), null, DateTimeOffset.UtcNow);

    private static OperationResultPayload Failure(Guid id, string error) =>
        new(id, OperationState.Failed, null, error, DateTimeOffset.UtcNow);

    [LoggerMessage(160, LogLevel.Warning, "Game server operation {operationId} ({operationKind}) failed")]
    private static partial void LogGameServerFailed(ILogger logger, Exception exception, Guid operationId, OperationKind operationKind);
}
