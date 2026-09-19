using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeltrixControl.Contracts;

namespace VeltrixControl.Agent.Operations;

public sealed partial class AdminOperations(
    AgentOptions options,
    TerminalSessionManager terminalSessions,
    ILogger<AdminOperations> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool Handles(OperationKind kind) => kind is
        OperationKind.StartProcess or OperationKind.StopProcess or OperationKind.SetProcessPriority or
        OperationKind.StartService or OperationKind.StopService or OperationKind.SetServiceStartType or
        OperationKind.ScheduleRestart or OperationKind.ScheduleShutdown or OperationKind.Logoff or
        OperationKind.Sleep or OperationKind.Hibernate or
        OperationKind.TerminalStart or OperationKind.TerminalInput or OperationKind.TerminalOutput or OperationKind.TerminalStop;

    public OperationResultPayload Execute(OperationAssignment operation)
    {
        try
        {
            return operation.Kind switch
            {
                OperationKind.StartProcess => StartProcess(operation),
                OperationKind.StopProcess => StopProcess(operation),
                OperationKind.SetProcessPriority => SetProcessPriority(operation),
                OperationKind.StartService => ControlService(operation, "start"),
                OperationKind.StopService => ControlService(operation, "stop"),
                OperationKind.SetServiceStartType => SetServiceStartType(operation),
                OperationKind.ScheduleRestart => SchedulePower(operation, restart: true),
                OperationKind.ScheduleShutdown => SchedulePower(operation, restart: false),
                OperationKind.Logoff => Logoff(operation),
                OperationKind.Sleep => Suspend(operation, hibernate: false),
                OperationKind.Hibernate => Suspend(operation, hibernate: true),
                OperationKind.TerminalStart => StartTerminal(operation),
                OperationKind.TerminalInput => InputTerminal(operation),
                OperationKind.TerminalOutput => OutputTerminal(operation),
                OperationKind.TerminalStop => StopTerminal(operation),
                _ => Failure(operation.Id, "Unsupported administrative operation.")
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
            System.ComponentModel.Win32Exception or UnauthorizedAccessException or SecurityException or IOException or NotSupportedException)
        {
            LogAdminOperationFailed(logger, exception, operation.Id, operation.Kind);
            return Failure(operation.Id, exception.Message);
        }
    }

    private OperationResultPayload StartProcess(OperationAssignment operation)
    {
        if (!options.AllowProcessActions) return Failure(operation.Id, "Process actions are disabled by this node's local policy.");
        var argument = Deserialize<StartProcessArgument>(operation);
        if (string.IsNullOrWhiteSpace(argument.FileName) || argument.FileName.Length > 1024) throw new ArgumentException("A valid executable path is required.");
        if (argument.FileName.Contains('\0') || argument.FileName.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("The executable path is invalid.");
        if (!Path.IsPathRooted(argument.FileName)) throw new ArgumentException("The executable path must be absolute.");
        if (!File.Exists(argument.FileName)) throw new FileNotFoundException("The executable does not exist.");
        var extension = Path.GetExtension(argument.FileName);
        if (!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only .exe programs can be started remotely.");
        if (argument.Arguments is { Length: > 2048 }) throw new ArgumentException("The argument list is too long.");
        if (argument.WorkingDirectory is { Length: > 1024 }) throw new ArgumentException("The working directory is invalid.");
        if (argument.WorkingDirectory is not null && !Directory.Exists(argument.WorkingDirectory)) throw new DirectoryNotFoundException("The working directory does not exist.");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = argument.FileName,
            Arguments = argument.Arguments ?? string.Empty,
            WorkingDirectory = argument.WorkingDirectory ?? Path.GetDirectoryName(argument.FileName) ?? Environment.SystemDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Windows did not start the program.");
        return Success(operation.Id, new ProcessActionResult(process.Id, Path.GetFileName(argument.FileName), "started"));
    }

    private OperationResultPayload StopProcess(OperationAssignment operation)
    {
        if (!options.AllowProcessActions) return Failure(operation.Id, "Process actions are disabled by this node's local policy.");
        var argument = Deserialize<ProcessTargetArgument>(operation);
        using var process = Process.GetProcessById(argument.ProcessId);
        if (AdminLimits.IsProtectedProcess(process.ProcessName) || IsAgentProcess(process.Id, process.ProcessName))
            return Failure(operation.Id, $"Process '{process.ProcessName}' is protected and cannot be stopped remotely.");
        if (!string.Equals(process.ProcessName, argument.ProcessName, StringComparison.OrdinalIgnoreCase))
            return Failure(operation.Id, "The process identity changed before the request was executed.");
        process.Kill();
        process.WaitForExit(5000);
        return Success(operation.Id, new ProcessActionResult(argument.ProcessId, argument.ProcessName, "stopped"));
    }

    private OperationResultPayload SetProcessPriority(OperationAssignment operation)
    {
        if (!options.AllowProcessActions) return Failure(operation.Id, "Process actions are disabled by this node's local policy.");
        var argument = Deserialize<ProcessPriorityArgument>(operation);
        if (!AdminLimits.ProcessPriorities.Contains(argument.PriorityClass, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("The requested priority class is invalid.");
        using var process = Process.GetProcessById(argument.ProcessId);
        if (AdminLimits.IsProtectedProcess(process.ProcessName) || IsAgentProcess(process.Id, process.ProcessName))
            return Failure(operation.Id, $"Process '{process.ProcessName}' is protected.");
        if (!string.Equals(process.ProcessName, argument.ProcessName, StringComparison.OrdinalIgnoreCase))
            return Failure(operation.Id, "The process identity changed before the request was executed.");
        process.PriorityClass = Enum.Parse<ProcessPriorityClass>(argument.PriorityClass, ignoreCase: true);
        return Success(operation.Id, new ProcessActionResult(argument.ProcessId, argument.ProcessName, $"priority={argument.PriorityClass}"));
    }

    private OperationResultPayload ControlService(OperationAssignment operation, string action)
    {
        if (!options.AllowServiceActions) return Failure(operation.Id, "Service actions are disabled by this node's local policy.");
        var argument = Deserialize<ServiceTargetArgument>(operation);
        ValidateServiceName(argument.Name);
        if (action == "stop" && AdminLimits.IsProtectedService(argument.Name))
            return Failure(operation.Id, $"Service '{argument.Name}' is protected and cannot be stopped remotely.");

        using var service = new ServiceController(argument.Name);
        var current = service.Status;
        if (action == "start")
        {
            if (current is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return Success(operation.Id, new ServiceActionResult(argument.Name, action, "already running"));
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
        else
        {
            if (current is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending) return Success(operation.Id, new ServiceActionResult(argument.Name, action, "already stopped"));
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        return Success(operation.Id, new ServiceActionResult(argument.Name, action, service.Status.ToString()));
    }

    private OperationResultPayload SetServiceStartType(OperationAssignment operation)
    {
        if (!options.AllowServiceActions) return Failure(operation.Id, "Service actions are disabled by this node's local policy.");
        var argument = Deserialize<ServiceStartTypeArgument>(operation);
        ValidateServiceName(argument.Name);
        if (!AdminLimits.ServiceStartTypes.Contains(argument.StartType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("The requested startup type is invalid.");
        if (AdminLimits.IsProtectedService(argument.Name))
            return Failure(operation.Id, $"Service '{argument.Name}' is protected and its startup type cannot be changed remotely.");

        var value = argument.StartType.ToLowerInvariant() switch
        {
            "automatic" => "auto",
            "disabled" => "disabled",
            _ => "demand"
        };
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
            Arguments = $"config \"{argument.Name}\" start= {value}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Windows did not start the service control utility.");
        process.WaitForExit(15000);
        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd().Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"sc.exe returned {process.ExitCode}." : error);
        }
        return Success(operation.Id, new ServiceActionResult(argument.Name, "startup=" + argument.StartType, null));
    }

    private OperationResultPayload SchedulePower(OperationAssignment operation, bool restart)
    {
        if (!options.AllowPowerActions) return Failure(operation.Id, "Power actions are disabled by this node's local policy.");
        var argument = Deserialize<ScheduledPowerArgument>(operation);
        if (argument.DelaySeconds is < 0 or > AdminLimits.MaxScheduledPowerSeconds)
            throw new ArgumentException("The scheduled delay is outside the allowed range.");
        var verb = restart ? "/r" : "/s";
        var comment = restart ? "Authorized Veltrix-Control restart" : "Authorized Veltrix-Control shutdown";
        var exitCode = RunShutdown($"{verb} /t {argument.DelaySeconds} /d p:0:0 /c \"{comment}\"");
        return exitCode == 0
            ? Success(operation.Id, new { action = verb, delaySeconds = argument.DelaySeconds })
            : Failure(operation.Id, $"shutdown.exe returned {exitCode}.");
    }

    private OperationResultPayload Logoff(OperationAssignment operation)
    {
        if (!options.AllowPowerActions) return Failure(operation.Id, "Power actions are disabled by this node's local policy.");
        var exitCode = RunShutdown("/l");
        return exitCode == 0 ? Success(operation.Id, new { action = "logoff" }) : Failure(operation.Id, $"shutdown.exe returned {exitCode}.");
    }

    private OperationResultPayload Suspend(OperationAssignment operation, bool hibernate)
    {
        if (!options.AllowPowerActions) return Failure(operation.Id, "Power actions are disabled by this node's local policy.");
        if (!SetSuspendState(hibernate, false, false))
            throw new InvalidOperationException(hibernate ? "Windows could not hibernate this computer." : "Windows could not suspend this computer.");
        return Success(operation.Id, new { action = hibernate ? "hibernate" : "sleep" });
    }

    private OperationResultPayload StartTerminal(OperationAssignment operation)
    {
        if (!options.AllowTerminal) return Failure(operation.Id, "Terminal access is disabled by this node's local policy.");
        var argument = Deserialize<TerminalStartArgument>(operation);
        if (terminalSessions.ActiveCount >= AdminLimits.MaxTerminalSessionsPerDevice)
            return Failure(operation.Id, "The maximum number of terminal sessions is already active.");
        if (string.IsNullOrWhiteSpace(argument.WorkingDirectory) || !Directory.Exists(argument.WorkingDirectory))
            return Failure(operation.Id, "The working directory does not exist.");
        if (!Enum.TryParse<TerminalShell>(argument.Shell, out _)) return Failure(operation.Id, "The requested shell is invalid.");
        var session = terminalSessions.Start(argument with { WorkingDirectory = Path.GetFullPath(argument.WorkingDirectory) });
        var output = session.Read(0);
        return Success(operation.Id, output with { Output = "Veltrix-Control terminal session started.\n" + output.Output });
    }

    private OperationResultPayload InputTerminal(OperationAssignment operation)
    {
        if (!options.AllowTerminal) return Failure(operation.Id, "Terminal access is disabled by this node's local policy.");
        var argument = Deserialize<TerminalInputArgument>(operation);
        if (argument.Data is { Length: > AdminLimits.MaxCommandLength }) throw new ArgumentException("The command exceeds the allowed length.");
        var session = terminalSessions.Get(argument.SessionId) ?? throw new InvalidOperationException("The terminal session is not active.");
        session.Write(argument.Data);
        WaitForOutput(session, 1500);
        return Success(operation.Id, session.Read(0));
    }

    private OperationResultPayload OutputTerminal(OperationAssignment operation)
    {
        if (!options.AllowTerminal) return Failure(operation.Id, "Terminal access is disabled by this node's local policy.");
        var argument = Deserialize<TerminalOutputArgument>(operation);
        var session = terminalSessions.Get(argument.SessionId) ?? throw new InvalidOperationException("The terminal session is not active.");
        return Success(operation.Id, session.Read(argument.SinceSequence));
    }

    private OperationResultPayload StopTerminal(OperationAssignment operation)
    {
        if (!options.AllowTerminal) return Failure(operation.Id, "Terminal access is disabled by this node's local policy.");
        var argument = Deserialize<TerminalStopArgument>(operation);
        var stopped = terminalSessions.Stop(argument.SessionId);
        return stopped ? Success(operation.Id, new { sessionId = argument.SessionId, action = "stopped" }) : Failure(operation.Id, "The terminal session is not active.");
    }

    private static void WaitForOutput(TerminalSession session, int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        var lastLength = session.Read(0).Sequence;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(120);
            var current = session.Read(0).Sequence;
            if (current != lastLength)
            {
                lastLength = current;
                deadline = Environment.TickCount64 + 350;
            }
            if (session.Exited) break;
        }
    }

    private static void ValidateServiceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 ||
            !name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '(' or ')' or ' ' or '$'))
        {
            throw new ArgumentException("The service name is invalid.");
        }
    }

    private static bool IsAgentProcess(int processId, string processName) =>
        processId == Environment.ProcessId || string.Equals(processName, "Veltrix-Control.Agent", StringComparison.OrdinalIgnoreCase);

    private static int RunShutdown(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Windows did not start the power operation.");
        process.WaitForExit(15000);
        return process.ExitCode;
    }

    private static T Deserialize<T>(OperationAssignment operation) =>
        JsonSerializer.Deserialize<T>(operation.Argument ?? string.Empty, JsonOptions)
        ?? throw new ArgumentException("The operation argument is invalid.");

    private static OperationResultPayload Success(Guid id, object? result) =>
        new(id, OperationState.Succeeded, result is null ? null : JsonSerializer.Serialize(result, JsonOptions), null, DateTimeOffset.UtcNow);

    private static OperationResultPayload Failure(Guid id, string error) =>
        new(id, OperationState.Failed, null, error, DateTimeOffset.UtcNow);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [LoggerMessage(50, LogLevel.Warning, "Administrative operation {operationId} ({operationKind}) failed")]
    private static partial void LogAdminOperationFailed(ILogger logger, Exception exception, Guid operationId, OperationKind operationKind);
}
