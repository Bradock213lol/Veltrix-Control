using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace VeltrixControl.Agent.GameServers;

public sealed partial class GameServerManager(ILogger<GameServerManager> logger)
{
    private readonly ConcurrentDictionary<Guid, GameServerProcess> _servers = new();

    public GameServerProcess Start(Guid serverId, string executable, string arguments, string workingDirectory)
    {
        if (_servers.TryGetValue(serverId, out var existing) && !existing.Exited)
            throw new InvalidOperationException("The game server is already running.");

        var info = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var process = Process.Start(info) ?? throw new InvalidOperationException("Windows did not start the game server process.");
        var server = new GameServerProcess(serverId, process);
        _servers[serverId] = server;
        LogStarted(logger, serverId, process.Id);
        return server;
    }

    public GameServerProcess? Get(Guid serverId) => _servers.TryGetValue(serverId, out var server) ? server : null;

    public bool Stop(Guid serverId, bool graceful)
    {
        if (!_servers.TryRemove(serverId, out var server)) return false;
        server.Stop(graceful);
        LogStopped(logger, serverId);
        return true;
    }

    [LoggerMessage(140, LogLevel.Information, "Game server {serverId} started as process {processId}")]
    private static partial void LogStarted(ILogger logger, Guid serverId, int processId);

    [LoggerMessage(141, LogLevel.Information, "Game server {serverId} stopped")]
    private static partial void LogStopped(ILogger logger, Guid serverId);
}

public sealed class GameServerProcess
{
    private readonly object _sync = new();
    private readonly StringBuilder _buffer = new();
    private readonly Process _process;
    private long _startSequence;
    private long _sequence;

    public GameServerProcess(Guid serverId, Process process)
    {
        ServerId = serverId;
        _process = process;
        _ = Task.Run(() => PumpAsync(process.StandardOutput));
        _ = Task.Run(() => PumpAsync(process.StandardError));
    }

    public Guid ServerId { get; }
    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.UtcNow;
    public bool Exited => _process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;
    public long Sequence { get { lock (_sync) return _sequence; } }

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
            // The server stopped or the stream closed.
        }
    }

    private void Append(string text)
    {
        lock (_sync)
        {
            _buffer.Append(text);
            _sequence += text.Length;
            if (_buffer.Length > GameServerBufferBytes)
            {
                var excess = _buffer.Length - GameServerBufferBytes;
                _buffer.Remove(0, excess);
                _startSequence += excess;
            }
        }
        LastActivity = DateTimeOffset.UtcNow;
    }

    public void Write(string data)
    {
        if (_process.HasExited) throw new InvalidOperationException("The game server process has exited.");
        _process.StandardInput.Write(data);
        _process.StandardInput.Flush();
        LastActivity = DateTimeOffset.UtcNow;
    }

    public (long Sequence, string Output) Read(long since)
    {
        lock (_sync)
        {
            var start = Math.Clamp(since, _startSequence, _sequence);
            var available = (int)Math.Min(_sequence - start, 64 * 1024);
            var offset = (int)(start - _startSequence);
            return (start + available, available <= 0 ? string.Empty : _buffer.ToString(offset, available));
        }
    }

    public void Stop(bool graceful)
    {
        try
        {
            if (_process.HasExited) return;
            if (graceful)
            {
                try
                {
                    _process.StandardInput.WriteLine("stop");
                    _process.StandardInput.Flush();
                    if (_process.WaitForExit(15000)) return;
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    // Fall through to a forced stop.
                }
            }
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
        finally
        {
            _process.Dispose();
        }
    }

    private const int GameServerBufferBytes = 512 * 1024;
}
