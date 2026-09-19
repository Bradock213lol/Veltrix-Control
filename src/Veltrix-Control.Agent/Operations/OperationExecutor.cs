using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using VeltrixControl.Core.Files;
using VeltrixControl.Contracts;

namespace VeltrixControl.Agent.Operations;

public sealed partial class OperationExecutor(
    AgentOptions options,
    FileOperations fileOperations,
    AdminOperations adminOperations,
    SoftwareOperations softwareOperations,
    WindowsUpdateOperations windowsUpdateOperations,
    BackupOperations backupOperations,
    ILogger<OperationExecutor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<OperationResultPayload> ExecuteAsync(OperationAssignment operation, CancellationToken cancellationToken)
    {
        try
        {
            if (FileOperations.Handles(operation.Kind))
            {
                return fileOperations.Execute(operation);
            }
            if (AdminOperations.Handles(operation.Kind))
            {
                return adminOperations.Execute(operation);
            }
            if (SoftwareOperations.Handles(operation.Kind))
            {
                return await softwareOperations.ExecuteAsync(operation, cancellationToken);
            }
            if (WindowsUpdateOperations.Handles(operation.Kind))
            {
                return await windowsUpdateOperations.ExecuteAsync(operation, cancellationToken);
            }
            if (BackupOperations.Handles(operation.Kind))
            {
                return backupOperations.Execute(operation);
            }

            return operation.Kind switch
            {
                OperationKind.ListDirectory => ListDirectory(operation),
                OperationKind.ListProcesses => ListProcesses(operation),
                OperationKind.ListServices => ListServices(operation),
                OperationKind.ListSoftware => ListSoftware(operation),
                OperationKind.ListNetworkAdapters => ListNetworkAdapters(operation),
                OperationKind.Restart => await ExecutePowerAsync(operation, "/r /t 5 /d p:0:0 /c \"Authorized Veltrix-Control restart\"", cancellationToken),
                OperationKind.Shutdown => await ExecutePowerAsync(operation, "/s /t 5 /d p:0:0 /c \"Authorized Veltrix-Control shutdown\"", cancellationToken),
                _ => Failed(operation.Id, "Unsupported operation.")
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.ComponentModel.Win32Exception or NetworkInformationException or SecurityException)
        {
            LogOperationFailed(logger, exception, operation.Id, operation.Kind);
            return Failed(operation.Id, exception.Message);
        }
    }

    private OperationResultPayload ListDirectory(OperationAssignment operation)
    {
        var path = PathGuard.ResolveWithinRoot(options.ManagedRoot, operation.Argument, allowRoot: true);
        if (!Directory.Exists(path)) return Failed(operation.Id, "Directory does not exist.");
        var root = Path.GetFullPath(options.ManagedRoot);
        var entries = new DirectoryInfo(path).EnumerateFileSystemInfos()
            .Take(1000)
            .Select(entry => new FileEntry(
                entry.Name,
                Path.GetRelativePath(root, entry.FullName),
                entry is DirectoryInfo,
                entry is FileInfo file ? file.Length : null,
                entry.LastWriteTimeUtc))
            .ToArray();
        return Succeeded(operation.Id, entries);
    }

    private static OperationResultPayload ListProcesses(OperationAssignment operation)
    {
        var processes = new List<ProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                processes.Add(new ProcessSnapshot(
                    process.Id,
                    process.ProcessName,
                    process.WorkingSet64,
                    process.TotalProcessorTime.TotalSeconds,
                    process.Threads.Count));
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // A process can exit or become protected while the snapshot is collected.
            }
            finally
            {
                process.Dispose();
            }
        }

        return Succeeded(operation.Id, processes
            .OrderByDescending(process => process.WorkingSetBytes)
            .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToArray());
    }

    private static OperationResultPayload ListServices(OperationAssignment operation)
    {
        var services = new List<ServiceSnapshot>();
        foreach (var service in ServiceController.GetServices())
        {
            try
            {
                services.Add(new ServiceSnapshot(
                    service.ServiceName,
                    service.DisplayName,
                    service.Status.ToString(),
                    service.StartType.ToString()));
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Continue when a service disappears or refuses inspection mid-snapshot.
            }
            finally
            {
                service.Dispose();
            }
        }

        var result = services
            .OrderBy(service => service.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(1000)
            .ToArray();
        return Succeeded(operation.Id, result);
    }

    private static OperationResultPayload ListSoftware(OperationAssignment operation)
    {
        const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var software = new List<SoftwareSnapshot>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = baseKey.OpenSubKey(uninstallPath);
            if (uninstall is null) continue;
            foreach (var subKeyName in uninstall.GetSubKeyNames().Take(2500))
            {
                using var entry = uninstall.OpenSubKey(subKeyName);
                var name = entry?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                software.Add(new SoftwareSnapshot(
                    name.Trim(),
                    (entry!.GetValue("DisplayVersion") as string)?.Trim(),
                    (entry.GetValue("Publisher") as string)?.Trim(),
                    (entry.GetValue("InstallDate") as string)?.Trim()));
            }
        }

        return Succeeded(operation.Id, software
            .DistinctBy(item => $"{item.Name}\u001f{item.Version}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(1500)
            .ToArray());
    }

    private static OperationResultPayload ListNetworkAdapters(OperationAssignment operation)
    {
        var adapters = new List<NetworkAdapterSnapshot>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var properties = adapter.GetIPProperties();
                var statistics = adapter.GetIPStatistics();
                adapters.Add(new NetworkAdapterSnapshot(
                    adapter.Name,
                    adapter.Description,
                    adapter.OperationalStatus.ToString(),
                    adapter.Speed,
                    properties.UnicastAddresses.Select(address => address.Address.ToString()).ToArray(),
                    properties.GatewayAddresses.Select(address => address.Address.ToString()).ToArray(),
                    properties.DnsAddresses.Select(address => address.ToString()).ToArray(),
                    statistics.BytesSent,
                    statistics.BytesReceived));
            }
            catch (NetworkInformationException)
            {
                // Keep collecting other adapters when one driver cannot provide statistics.
            }
        }

        return Succeeded(operation.Id, adapters
            .OrderByDescending(adapter => adapter.Status == OperationalStatus.Up.ToString())
            .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private async Task<OperationResultPayload> ExecutePowerAsync(OperationAssignment operation, string arguments, CancellationToken cancellationToken)
    {
        if (!options.AllowPowerActions)
        {
            return Failed(operation.Id, "Power actions are disabled by this node's local policy.");
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null) return Failed(operation.Id, "Windows did not start the power operation.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0 ? Succeeded(operation.Id, null) : Failed(operation.Id, $"shutdown.exe returned {process.ExitCode}.");
    }

    private static OperationResultPayload Succeeded(Guid id, object? result) =>
        new(id, OperationState.Succeeded, result is null ? null : JsonSerializer.Serialize(result, JsonOptions), null, DateTimeOffset.UtcNow);

    private static OperationResultPayload Failed(Guid id, string error) =>
        new(id, OperationState.Failed, null, error, DateTimeOffset.UtcNow);

    [LoggerMessage(1, LogLevel.Warning, "Operation {operationId} ({operationKind}) failed validation or I/O")]
    private static partial void LogOperationFailed(ILogger logger, Exception exception, Guid operationId, OperationKind operationKind);
}
