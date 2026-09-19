using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VeltrixControl.Agent;
using VeltrixControl.Agent.Operations;
using VeltrixControl.Contracts;

namespace VeltrixControl.AgentTests;

public sealed class AdminOperationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"veltrix-admin-{Guid.NewGuid():N}");

    public AdminOperationsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ProcessAndServiceActionsAreDeniedByDefault()
    {
        var operations = Create(allowProcess: false, allowService: false, allowTerminal: false);
        var stop = Execute(operations, OperationKind.StopProcess, Json(new ProcessTargetArgument(Environment.ProcessId, "Veltrix-Control.Agent")));
        Assert.Equal(OperationState.Failed, stop.State);
        Assert.Contains("local policy", stop.Error, StringComparison.OrdinalIgnoreCase);

        var service = Execute(operations, OperationKind.StopService, Json(new ServiceTargetArgument("Spooler")));
        Assert.Equal(OperationState.Failed, service.State);
        Assert.Contains("local policy", service.Error, StringComparison.OrdinalIgnoreCase);

        var terminal = Execute(operations, OperationKind.TerminalStart, Json(new TerminalStartArgument(Guid.NewGuid(), "PowerShell", _root)));
        Assert.Equal(OperationState.Failed, terminal.State);
        Assert.Contains("local policy", terminal.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtectedProcessesCannotBeStopped()
    {
        var operations = Create(allowProcess: true, allowService: false, allowTerminal: false);
        var result = Execute(operations, OperationKind.StopProcess, Json(new ProcessTargetArgument(4, "System")));
        Assert.Equal(OperationState.Failed, result.State);
        Assert.Contains("protected", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Process.GetProcesses(), process => process.Id == 4);
    }

    [Fact]
    public void ProtectedServicesCannotBeStopped()
    {
        var operations = Create(allowProcess: false, allowService: true, allowTerminal: false);
        var result = Execute(operations, OperationKind.StopService, Json(new ServiceTargetArgument("RpcSs")));
        Assert.Equal(OperationState.Failed, result.State);
        Assert.Contains("protected", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FixtureProcessCanBeStartedAndStopped()
    {
        var operations = Create(allowProcess: true, allowService: false, allowTerminal: false);
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        Assert.True(File.Exists(ping));

        var start = Execute(operations, OperationKind.StartProcess, Json(new StartProcessArgument(ping, "-n 60 127.0.0.1", _root)));
        Assert.Equal(OperationState.Succeeded, start.State);
        var started = JsonSerializer.Deserialize<ProcessActionResult>(start.ResultJson!, JsonOptions);
        Assert.NotNull(started);

        var stop = Execute(operations, OperationKind.StopProcess, Json(new ProcessTargetArgument(started!.ProcessId, "ping")));
        Assert.Equal(OperationState.Succeeded, stop.State);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && Process.GetProcesses().Any(process => process.Id == started.ProcessId))
        {
            await Task.Delay(200);
        }
        Assert.DoesNotContain(Process.GetProcesses(), process => process.Id == started.ProcessId);
    }

    [Fact]
    public void ProcessIdentityMismatchIsRejected()
    {
        var operations = Create(allowProcess: true, allowService: false, allowTerminal: false);
        var result = Execute(operations, OperationKind.StopProcess, Json(new ProcessTargetArgument(Environment.ProcessId, "not-the-agent")));
        Assert.Equal(OperationState.Failed, result.State);
    }

    [Fact]
    public void StartProcessRejectsNonExecutableAndMissingFiles()
    {
        var operations = Create(allowProcess: true, allowService: false, allowTerminal: false);
        var text = Path.Combine(_root, "not-a-program.txt");
        File.WriteAllText(text, "text");
        Assert.Equal(OperationState.Failed, Execute(operations, OperationKind.StartProcess, Json(new StartProcessArgument(text, null, null))).State);
        Assert.Equal(OperationState.Failed, Execute(operations, OperationKind.StartProcess, Json(new StartProcessArgument(Path.Combine(_root, "missing.exe"), null, null))).State);
        Assert.Equal(OperationState.Failed, Execute(operations, OperationKind.StartProcess, Json(new StartProcessArgument(@"\\server\share\tool.exe", null, null))).State);
    }

    [Fact]
    public async Task TerminalSessionRunsCommandsAndStops()
    {
        var operations = Create(allowProcess: false, allowService: false, allowTerminal: true);
        var sessionId = Guid.NewGuid();
        var start = Execute(operations, OperationKind.TerminalStart, Json(new TerminalStartArgument(sessionId, "CommandPrompt", _root)));
        Assert.Equal(OperationState.Succeeded, start.State);

        try
        {
            var input = Execute(operations, OperationKind.TerminalInput, Json(new TerminalInputArgument(sessionId, "echo veltrix-terminal-check\r\n")));
            Assert.Equal(OperationState.Succeeded, input.State);
            var output = JsonSerializer.Deserialize<TerminalOutputResult>(input.ResultJson!, JsonOptions);
            Assert.NotNull(output);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline && !output.Output.Contains("veltrix-terminal-check", StringComparison.OrdinalIgnoreCase) && !output.Exited)
            {
                await Task.Delay(300);
                var drain = Execute(operations, OperationKind.TerminalOutput, Json(new TerminalOutputArgument(sessionId, 0)));
                Assert.Equal(OperationState.Succeeded, drain.State);
                output = JsonSerializer.Deserialize<TerminalOutputResult>(drain.ResultJson!, JsonOptions)!;
            }
            Assert.Contains("veltrix-terminal-check", output.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            var stop = Execute(operations, OperationKind.TerminalStop, Json(new TerminalStopArgument(sessionId)));
            Assert.Equal(OperationState.Succeeded, stop.State);
        }

        Assert.Equal(OperationState.Failed, Execute(operations, OperationKind.TerminalOutput, Json(new TerminalOutputArgument(sessionId, 0))).State);
    }

    [Fact]
    public void ScheduledPowerRequiresLocalPolicy()
    {
        var operations = Create(allowProcess: false, allowService: false, allowTerminal: false);
        var result = Execute(operations, OperationKind.ScheduleRestart, Json(new ScheduledPowerArgument(600)));
        Assert.Equal(OperationState.Failed, result.State);
        Assert.Contains("local policy", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private AdminOperations Create(bool allowProcess, bool allowService, bool allowTerminal)
    {
        var options = new AgentOptions
        {
            ManagedRoot = _root,
            DataDirectory = Path.Combine(_root, ".agent"),
            AllowProcessActions = allowProcess,
            AllowServiceActions = allowService,
            AllowTerminal = allowTerminal,
            AllowPowerActions = false
        };
        return new AdminOperations(options, new TerminalSessionManager(NullLogger<TerminalSessionManager>.Instance), NullLogger<AdminOperations>.Instance);
    }

    private static OperationResultPayload Execute(AdminOperations operations, OperationKind kind, string argument) =>
        operations.Execute(new OperationAssignment(Guid.NewGuid(), kind, argument, DateTimeOffset.UtcNow));

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
    }
}
