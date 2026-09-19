using System.Security.Claims;
using VeltrixControl.Contracts;
using VeltrixControl.Controller.Security;
using VeltrixControl.Core.Security;
using VeltrixControl.Infrastructure;

namespace VeltrixControl.Controller.Endpoints;

public static class ComputeEndpoints
{
    public static void MapManagementCompute(this RouteGroupBuilder management)
    {
        management.MapPut("/devices/{deviceId:guid}/compute-policy", async (Guid deviceId, ComputePolicyRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.AdminManage)) return Results.Forbid();
            var error = ValidatePolicy(request);
            if (error is not null) return Results.BadRequest(new { error });
            try
            {
                return Results.Ok(await database.UpsertComputePolicyAsync(deviceId, user.Identity!.Name!, request, ct));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        management.MapGet("/devices/{deviceId:guid}/compute-policy", async (Guid deviceId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceCompute)) return Results.Forbid();
            var policy = await database.GetComputePolicyAsync(deviceId, ct);
            return Results.Ok(policy ?? new ComputePolicyView(deviceId, "Idle", 0, 0, 0, DateTimeOffset.MinValue));
        });

        management.MapPost("/compute/jobs", async (ComputeJobRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceCompute)) return Results.Forbid();
            var error = ValidateJob(request);
            if (error is not null) return Results.BadRequest(new { error });
            try
            {
                var job = await database.CreateComputeJobAsync(user.Identity!.Name!, request, ct);
                return Results.Accepted($"/api/compute/jobs/{job.Id:D}", job);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        management.MapGet("/compute/jobs", async (string? state, int? limit, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
            user.HasPermission(RolePermissions.DeviceCompute) ? Results.Ok(await database.GetComputeJobsAsync(state, limit ?? 100, ct)) : Results.Forbid());

        management.MapGet("/compute/jobs/{jobId:guid}", async (Guid jobId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceCompute)) return Results.Forbid();
            var job = await database.GetComputeJobAsync(jobId, ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        management.MapPost("/compute/jobs/{jobId:guid}/cancel", async (Guid jobId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceCompute)) return Results.Forbid();
            var job = await database.GetComputeJobAsync(jobId, ct);
            if (job is null) return Results.NotFound();
            if (job.State == "Running" && job.AssignedDeviceId is { } deviceId)
            {
                try
                {
                    var argument = System.Text.Json.JsonSerializer.Serialize(new ComputeCancelArgument(jobId));
                    await database.CreateOperationAsync(deviceId, user.Identity!.Name!, new OperationRequest(OperationKind.CancelComputeJob, argument, true), ct);
                }
                catch (KeyNotFoundException)
                {
                    // The device disappeared; the job is still cancelled below.
                }
            }
            return await database.CancelComputeJobAsync(jobId, user.Identity!.Name!, ct) ? Results.NoContent() : Results.Conflict(new { error = "The job is no longer active." });
        });
    }

    private static string? ValidatePolicy(ComputePolicyRequest request)
    {
        if (!ComputeModes.All.Contains(request.Mode, StringComparer.OrdinalIgnoreCase)) return "The compute mode is invalid.";
        if (request.ReservedCpuThreads is < 0 or > 4096) return "The reserved CPU threads are outside the allowed range.";
        if (request.ReservedMemoryBytes is < 0 or > 1L << 50) return "The reserved memory is outside the allowed range.";
        if (request.ReservedDiskBytes is < 0 or > 1L << 50) return "The reserved disk is outside the allowed range.";
        return null;
    }

    private static string? ValidateJob(ComputeJobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128) return "A job name is required and must not exceed 128 characters.";
        if (request.Priority is < 0 or > ComputeLimits.MaxPriority) return $"Priority must be between 0 and {ComputeLimits.MaxPriority}.";
        if (request.TimeoutSeconds is < ComputeLimits.MinTimeoutSeconds or > ComputeLimits.MaxTimeoutSeconds) return "The timeout is outside the allowed range.";
        if (request.MaxAttempts is < 1 or > ComputeLimits.MaxAttempts) return $"Attempts must be between 1 and {ComputeLimits.MaxAttempts}.";
        if (request.Requirements is null || request.Command is null) return "Requirements and a command are required.";
        if (request.Requirements.CpuThreads is < 1 or > 4096) return "The CPU requirement is outside the allowed range.";
        if (request.Requirements.MemoryBytes is < 0 or > 1L << 50) return "The memory requirement is outside the allowed range.";
        if (request.Requirements.DiskBytes is < 0 or > 1L << 50) return "The disk requirement is outside the allowed range.";
        if (request.Requirements.DeviceNameContains is { Length: > 128 }) return "The device filter is too long.";
        var executable = request.Command.Executable;
        if (string.IsNullOrWhiteSpace(executable) || executable.Length > 1024) return "A valid executable path is required.";
        if (executable.Contains('\0') || executable.StartsWith(@"\\", StringComparison.Ordinal)) return "The executable path is invalid.";
        if (!Path.IsPathRooted(executable)) return "The executable path must be absolute.";
        if (!string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)) return "Only .exe programs can run as compute jobs.";
        if (request.Command.Arguments is { Length: > 2048 }) return "The job arguments exceed the allowed size.";
        if (request.Command.WorkingDirectory is { Length: > 1024 }) return "The working directory is invalid.";
        return null;
    }
}
