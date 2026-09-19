using System.Security.Claims;
using System.Text.Json;
using VeltrixControl.Contracts;
using VeltrixControl.Controller.Security;
using VeltrixControl.Core.Security;
using VeltrixControl.Infrastructure;

namespace VeltrixControl.Controller.Endpoints;

public static class SoftwareEndpoints
{
    public static void MapManagementSoftware(this RouteGroupBuilder management)
    {
        management.MapPost("/software/packages", async (SoftwarePackageRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceSoftware)) return Results.Forbid();
            var error = ValidatePackage(request);
            if (error is not null) return Results.BadRequest(new { error });
            try
            {
                return Results.Ok(await database.CreateSoftwarePackageAsync(user.Identity!.Name!, request, ct));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        management.MapGet("/software/packages", async (ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
            user.HasPermission(RolePermissions.DeviceSoftware) ? Results.Ok(await database.GetSoftwarePackagesAsync(ct)) : Results.Forbid());

        management.MapGet("/software/deployments", async (int? limit, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
            user.HasPermission(RolePermissions.DeviceSoftware) ? Results.Ok(await database.GetSoftwareDeploymentsAsync(limit ?? 50, ct)) : Results.Forbid());

        management.MapGet("/software/deployments/{deploymentId:guid}", async (Guid deploymentId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceSoftware)) return Results.Forbid();
            var deployment = await database.GetSoftwareDeploymentAsync(deploymentId, ct);
            return deployment is null ? Results.NotFound() : Results.Ok(deployment);
        });

        management.MapPost("/software/deployments", async (SoftwareDeploymentRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceSoftware)) return Results.Forbid();
            if (!request.Confirmed) return Results.BadRequest(new { error = "Explicit confirmation is required for a deployment." });
            if (!Enum.IsDefined(request.Action)) return Results.BadRequest(new { error = "The deployment action is invalid." });
            if (request.DeviceIds is null || request.DeviceIds.Length is 0 or > SoftwareLimits.MaxDeploymentTargets)
                return Results.BadRequest(new { error = $"Choose between 1 and {SoftwareLimits.MaxDeploymentTargets} devices." });
            var packages = await database.GetSoftwarePackagesAsync(ct);
            if (!packages.Any(item => item.Id == request.PackageId)) return Results.NotFound();
            try
            {
                var deployment = await database.CreateSoftwareDeploymentAsync(user.Identity!.Name!, request, ct);
                return Results.Accepted($"/api/software/deployments/{deployment.Id:D}", deployment);
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        });

        management.MapPost("/software/deployments/{deploymentId:guid}/cancel", async (Guid deploymentId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceSoftware)) return Results.Forbid();
            return await database.CancelSoftwareDeploymentAsync(deploymentId, user.Identity!.Name!, ct) ? Results.NoContent() : Results.NotFound();
        });

        management.MapGet("/devices/{deviceId:guid}/updates", async (Guid deviceId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceUpdates)) return Results.Forbid();
            var scan = await database.GetLatestWindowsUpdateScanAsync(deviceId, ct);
            return scan is null ? Results.NoContent() : Results.Ok(scan);
        });

        management.MapPost("/devices/{deviceId:guid}/updates/scan", async (Guid deviceId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceUpdates)) return Results.Forbid();
            try
            {
                var operation = await database.CreateOperationAsync(deviceId, user.Identity!.Name!, new OperationRequest(OperationKind.ScanWindowsUpdates, null, true), ct);
                return Results.Accepted($"/api/operations/{operation.Id:D}", operation);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        management.MapPost("/devices/{deviceId:guid}/updates/install", async (Guid deviceId, WindowsUpdateInstallArgument request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.DeviceUpdates)) return Results.Forbid();
            if (request.UpdateIds is null || request.UpdateIds.Length is 0 or > SoftwareLimits.MaxWindowsUpdatesPerInstall)
                return Results.BadRequest(new { error = "Select at least one update to install." });
            if (request.UpdateIds.Any(id => !Guid.TryParse(id, out _)))
                return Results.BadRequest(new { error = "The update identifiers are invalid." });
            try
            {
                var operation = await database.CreateOperationAsync(deviceId, user.Identity!.Name!,
                    new OperationRequest(OperationKind.InstallWindowsUpdate, JsonSerializer.Serialize(request), true), ct);
                return Results.Accepted($"/api/operations/{operation.Id:D}", operation);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });
    }

    private static string? ValidatePackage(SoftwarePackageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128) return "A package name is required and must not exceed 128 characters.";
        if (!Enum.IsDefined(request.Source)) return "The package source is invalid.";
        if (string.IsNullOrWhiteSpace(request.PackageId) || request.PackageId.Length > 512) return "A package identifier is required.";
        if (request.PackageId.Contains('\n') || request.PackageId.Contains('\r') || request.PackageId.Contains('"')) return "The package identifier contains invalid characters.";
        if (request.Version is { Length: > 64 }) return "The version is too long.";
        if (request.SilentArgs is { Length: > 512 }) return "The silent arguments are too long.";
        if (request.Source is SoftwareSource.Msi or SoftwareSource.Exe)
        {
            if (!Uri.TryCreate(request.PackageId, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return "MSI and EXE packages require an HTTPS download URL.";
            if (string.IsNullOrWhiteSpace(request.Sha256) || request.Sha256.Length != 64 || !request.Sha256.All(char.IsAsciiHexDigit))
                return "MSI and EXE packages require a SHA-256 checksum.";
        }
        return null;
    }
}
