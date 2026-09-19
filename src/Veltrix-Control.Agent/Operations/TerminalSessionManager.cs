using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using VeltrixControl.Contracts;

namespace VeltrixControl.Agent.Operations;

public sealed partial class TerminalSessionManager(ILogger<TerminalSessionManager> logger)
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<Guid, TerminalSession> _sessions = new();

    public int ActiveCount => _sessions.Count;

    public TerminalSession Start(TerminalStartArgument argument)
    {
        CleanupIdle();
        var isCmd = argument.Shell == nameof(TerminalShell.CommandPrompt);
        var executable = isCmd
            ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
            : Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "powershell.exe");
        if (!File.Exists(executable)) executable = isCmd ? "cmd.exe" : "powershell.exe";

        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = argument.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!isCmd)
        {
            info.ArgumentList.Add("-NoLogo");
            info.ArgumentList.Add("-NoProfile");
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("Windows did not start the requested shell.");
        var session = new TerminalSession(argument.SessionId, argument.Shell, argument.WorkingDirectory, process);
        if (!_sessions.TryAdd(argument.SessionId, session))
        {
            session.Dispose();
            throw new InvalidOperationException("A terminal session with that identifier already exists.");
        }
        LogSessionStarted(logger, argument.SessionId, argument.Shell);
        return session;
    }

    public TerminalSession? Get(Guid sessionId) => _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public bool Stop(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return false;
        session.Dispose();
        LogSessionStopped(logger, sessionId);
        return true;
    }

    private void CleanupIdle()
    {
        foreach (var pair in _sessions)
        {
            if (pair.Value.Exited || DateTimeOffset.UtcNow - pair.Value.LastActivity > IdleTimeout)
            {
                Stop(pair.Key);
            }
        }
    }

    [LoggerMessage(40, LogLevel.Information, "Terminal session {sessionId} started ({shell})")]
    private static partial void LogSessionStarted(ILogger logger, Guid sessionId, string shell);

    [LoggerMessage(41, LogLevel.Information, "Terminal session {sessionId} stopped")]
    private static partial void LogSessionStopped(ILogger logger, Guid sessionId);
}

public sealed class TerminalSession : IDisposable
{
    private readonly object _sync = new();
    private readonly StringBuilder _buffer = new();
    private readonly Process _process;
    private long _startSequence;
    private long _sequence;

    public TerminalSession(Guid id, string shell, string workingDirectory, Process process)
    {
        Id = id;
        Shell = shell;
        WorkingDirectory = workingDirectory;
        _process = process;
        _ = Task.Run(() => PumpAsync(process.StandardOutput));
        _ = Task.Run(() => PumpAsync(process.StandardError));
    }

    public Guid Id { get; }
    public string Shell { get; }
    public string WorkingDirectory { get; }
    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.UtcNow;
    public bool Exited => _process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    private async Task PumpAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer);
                if (read <= 0) break;
                Append(new string(buffer, 0, read));
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The shell exited or the stream was closed.
        }
    }

    private void Append(string text)
    {
        lock (_sync)
        {
            _buffer.Append(text);
            _sequence += text.Length;
            if (_buffer.Length > AdminLimits.TerminalBufferBytes)
            {
                var excess = _buffer.Length - AdminLimits.TerminalBufferBytes;
                _buffer.Remove(0, excess);
                _startSequence += excess;
            }
        }
        LastActivity = DateTimeOffset.UtcNow;
    }

    public void Write(string data)
    {
        if (_process.HasExited) throw new InvalidOperationException("The shell has exited.");
        _process.StandardInput.Write(data);
        _process.StandardInput.Flush();
        LastActivity = DateTimeOffset.UtcNow;
    }

    public TerminalOutputResult Read(long since)
    {
        lock (_sync)
        {
            var start = Math.Clamp(since, _startSequence, _sequence);
            var available = (int)Math.Min(_sequence - start, AdminLimits.MaxTerminalOutputChunk);
            var offset = (int)(start - _startSequence);
            var output = available <= 0 ? string.Empty : _buffer.ToString(offset, available);
            return new TerminalOutputResult(Id, start + output.Length, output, _process.HasExited, ExitCode);
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The shell exited between the check and the kill.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
