using System.Text.Json;
using VeltrixControl.Contracts;
using VeltrixControl.Infrastructure;

namespace VeltrixControl.Controller.Services;

public sealed partial class ComputeScheduler(VeltrixControlStore store, ILogger<ComputeScheduler> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(EvaluationInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ScheduleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException or Microsoft.Data.Sqlite.SqliteException)
            {
                LogScheduleFailed(logger, exception);
            }
        }
    }

    private async Task ScheduleAsync(CancellationToken cancellationToken)
    {
        var jobs = await store.GetQueuedComputeJobsAsync(cancellationToken);
        if (jobs.Count == 0) return;

        var devices = await store.GetDevicesAsync(TimeSpan.FromSeconds(90), cancellationToken);
        var policies = (await store.GetComputePoliciesAsync(cancellationToken)).ToDictionary(policy => policy.DeviceId);
        var running = await store.GetRunningComputeJobsAsync(cancellationToken);
        var usage = new Dictionary<Guid, (int Cpu, long Memory, long Disk)>();
        foreach (var job in running)
        {
            if (job.AssignedDeviceId is not { } deviceId) continue;
            if (!TryRequirements(job, out var requirements)) continue;
            var current = usage.GetValueOrDefault(deviceId);
            usage[deviceId] = (current.Cpu + requirements.CpuThreads, current.Memory + requirements.MemoryBytes, current.Disk + requirements.DiskBytes);
        }

        foreach (var job in jobs)
        {
            if (!TryRequirements(job, out var requirements) || !TryCommand(job, out var command))
            {
                await store.CompleteComputeJobAsync(job.Id, "Failed", null, "The job definition is invalid.", cancellationToken);
                continue;
            }
            if (requirements.RequiresGpu)
            {
                await store.CompleteComputeJobAsync(job.Id, "Failed", null,
                    "GPU capability is not reported by managed nodes, so GPU jobs cannot be scheduled.", cancellationToken);
                continue;
            }

            var candidates = devices
                .Where(device => device.Online &&
                    (requirements.DeviceId is null || device.Id == requirements.DeviceId) &&
                    (requirements.DeviceNameContains is null || device.Name.Contains(requirements.DeviceNameContains, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(device => device.Telemetry?.CpuPercent ?? 100)
                .ToList();

            foreach (var device in candidates)
            {
                var policy = policies.GetValueOrDefault(device.Id);
                var mode = policy?.Mode ?? "Idle";
                if (!ComputeModes.AllowsJobs(mode)) continue;

                var used = usage.GetValueOrDefault(device.Id);
                var reservedCpu = policy?.ReservedCpuThreads ?? 0;
                var reservedMemory = policy?.ReservedMemoryBytes ?? 0;
                var reservedDisk = policy?.ReservedDiskBytes ?? 0;

                var cores = device.Inventory.LogicalProcessors;
                if (cores - reservedCpu - used.Cpu < requirements.CpuThreads) continue;

                var totalMemory = device.Inventory.TotalMemoryBytes;
                var usedMemory = device.Telemetry?.UsedMemoryBytes ?? 0;
                if (totalMemory - usedMemory - reservedMemory - used.Memory < requirements.MemoryBytes) continue;

                var availableDisk = device.Telemetry?.Disks.Count > 0 ? device.Telemetry.Disks.Max(disk => disk.AvailableBytes) : 0;
                if (availableDisk - reservedDisk - used.Disk < requirements.DiskBytes) continue;

                var assigned = await AssignAsync(job, command, device.Id, cancellationToken);
                if (assigned)
                {
                    var current = usage.GetValueOrDefault(device.Id);
                    usage[device.Id] = (current.Cpu + requirements.CpuThreads, current.Memory + requirements.MemoryBytes, current.Disk + requirements.DiskBytes);
                    break;
                }
            }
        }
    }

    private async Task<bool> AssignAsync(ComputeJobView job, ComputeCommand command, Guid deviceId, CancellationToken cancellationToken)
    {
        if (!await store.AssignComputeJobAsync(job.Id, deviceId, cancellationToken)) return false;
        var argument = JsonSerializer.Serialize(new ComputeJobArgument(job.Id, command.Executable, command.Arguments, command.WorkingDirectory, job.TimeoutSeconds), JsonOptions);
        try
        {
            await store.CreateOperationAsync(deviceId, "scheduler", new OperationRequest(OperationKind.RunComputeJob, argument, true), cancellationToken);
            return true;
        }
        catch (KeyNotFoundException)
        {
            await store.RequeueOrFailComputeJobAsync(job.Id, "The assigned device no longer exists.", cancellationToken);
            return false;
        }
    }

    private static bool TryRequirements(ComputeJobView job, out ComputeRequirements requirements)
    {
        try
        {
            requirements = JsonSerializer.Deserialize<ComputeRequirements>(job.RequirementJson, JsonOptions) ?? new ComputeRequirements(1, 0, 0, false, null, null);
            return true;
        }
        catch (JsonException)
        {
            requirements = new ComputeRequirements(1, 0, 0, false, null, null);
            return false;
        }
    }

    private static bool TryCommand(ComputeJobView job, out ComputeCommand command)
    {
        try
        {
            command = JsonSerializer.Deserialize<ComputeCommand>(job.CommandJson, JsonOptions) ?? new ComputeCommand(string.Empty, null, null);
            return !string.IsNullOrWhiteSpace(command.Executable);
        }
        catch (JsonException)
        {
            command = new ComputeCommand(string.Empty, null, null);
            return false;
        }
    }

    [LoggerMessage(130, LogLevel.Warning, "Compute scheduling cycle failed")]
    private static partial void LogScheduleFailed(ILogger logger, Exception exception);
}
