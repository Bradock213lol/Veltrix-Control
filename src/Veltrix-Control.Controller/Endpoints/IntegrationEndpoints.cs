using System.Security.Claims;
using VeltrixControl.Contracts;
using VeltrixControl.Controller.Security;
using VeltrixControl.Core.Security;
using VeltrixControl.Infrastructure;
using VeltrixControl.Integrations;

namespace VeltrixControl.Controller.Endpoints;

public static class IntegrationEndpoints
{
    public static void MapManagementIntegrations(this RouteGroupBuilder management)
    {
        management.MapPost("/integrations", async (IntegrationRequest request, IEnumerable<IIntegrationAdapter> adapters, IntegrationCredentialProtector protector, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.IntegrationManage)) return Results.Forbid();
            var error = Validate(request, adapters);
            if (error is not null) return Results.BadRequest(new { error });
            var protectedCredential = string.IsNullOrWhiteSpace(request.Credential) ? null : protector.Protect(request.Credential);
            try
            {
                return Results.Ok(await database.CreateIntegrationAsync(user.Identity!.Name!, request, protectedCredential, ct));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        management.MapGet("/integrations", async (ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
            user.HasPermission(RolePermissions.IntegrationManage) ? Results.Ok(await database.GetIntegrationsAsync(ct)) : Results.Forbid());

        management.MapGet("/integrations/{integrationId:guid}", async (Guid integrationId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.IntegrationManage)) return Results.Forbid();
            var integration = await database.GetIntegrationAsync(integrationId, ct);
            return integration is null ? Results.NotFound() : Results.Ok(integration);
        });

        management.MapDelete("/integrations/{integrationId:guid}", async (Guid integrationId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.IntegrationManage)) return Results.Forbid();
            return await database.DeleteIntegrationAsync(integrationId, user.Identity!.Name!, ct) ? Results.NoContent() : Results.NotFound();
        });

        management.MapPost("/integrations/{integrationId:guid}/health", async (Guid integrationId, IEnumerable<IIntegrationAdapter> adapters, IntegrationCredentialProtector protector, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.IntegrationManage)) return Results.Forbid();
            var resolved = await ResolveAsync(integrationId, adapters, protector, database, ct);
            if (resolved is null) return Results.NotFound();
            var (adapter, connection) = resolved.Value;
            try
            {
                var (healthy, detail) = await adapter.CheckHealthAsync(connection, ct);
                await database.UpdateIntegrationHealthAsync(integrationId, healthy ? "Healthy" : "Unhealthy", detail, ct);
                return Results.Ok(new { state = healthy ? "Healthy" : "Unhealthy", detail });
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Net.Sockets.SocketException)
            {
                await database.UpdateIntegrationHealthAsync(integrationId, "Unavailable", exception.Message, ct);
                return Results.Ok(new { state = "Unavailable", detail = exception.Message });
            }
        });

        management.MapGet("/integrations/{integrationId:guid}/resources", async (Guid integrationId, IEnumerable<IIntegrationAdapter> adapters, IntegrationCredentialProtector protector, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.IntegrationManage)) return Results.Forbid();
            var resolved = await ResolveAsync(integrationId, adapters, protector, database, ct);
            if (resolved is null) return Results.NotFound();
            var (adapter, connection) = resolved.Value;
            try
            {
                var resources = await adapter.ListResourcesAsync(connection, ct);
                await database.UpdateIntegrationHealthAsync(integrationId, "Healthy", $"Listed {resources.Count} resource(s).", ct);
                return Results.Ok(resources);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Net.Sockets.SocketException)
            {
                await database.UpdateIntegrationHealthAsync(integrationId, "Unavailable", exception.Message, ct);
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        management.MapPost("/integrations/{integrationId:guid}/actions", async (Guid integrationId, IntegrationActionRequest request, IEnumerable<IIntegrationAdapter> adapters, IntegrationCredentialProtector protector, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.IntegrationManage)) return Results.Forbid();
            if (!request.Confirmed) return Results.BadRequest(new { error = "Explicit confirmation is required." });
            if (!IntegrationActions.IsKnown(request.Action)) return Results.BadRequest(new { error = "The action is invalid." });
            if (string.IsNullOrWhiteSpace(request.ResourceId) || request.ResourceId.Length > 256) return Results.BadRequest(new { error = "The resource identifier is invalid." });
            var resolved = await ResolveAsync(integrationId, adapters, protector, database, ct);
            if (resolved is null) return Results.NotFound();
            var (adapter, connection) = resolved.Value;
            await database.RecordAuditEventAsync(user.Identity!.Name!, $"integration.{request.Action.ToLowerInvariant()}", request.ResourceId, "queued", $"integration={integrationId:D}", ct);
            try
            {
                var detail = await adapter.ExecuteAsync(connection, request.Action, request.ResourceId, ct);
                await database.RecordAuditEventAsync(user.Identity!.Name!, $"integration.{request.Action.ToLowerInvariant()}", request.ResourceId, "succeeded", detail, ct);
                return Results.Ok(new { detail });
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Net.Sockets.SocketException or ArgumentException)
            {
                await database.RecordAuditEventAsync(user.Identity!.Name!, $"integration.{request.Action.ToLowerInvariant()}", request.ResourceId, "failed", exception.Message, ct);
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        management.MapGet("/integrations/{integrationId:guid}/logs", async (Guid integrationId, string? resourceId, IEnumerable<IIntegrationAdapter> adapters, IntegrationCredentialProtector protector, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
        {
            if (!user.HasPermission(RolePermissions.IntegrationManage)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(resourceId) || resourceId.Length > 256) return Results.BadRequest(new { error = "The resource identifier is invalid." });
            var resolved = await ResolveAsync(integrationId, adapters, protector, database, ct);
            if (resolved is null) return Results.NotFound();
            var (adapter, connection) = resolved.Value;
            try
            {
                return Results.Ok(new { logs = await adapter.GetLogsAsync(connection, resourceId, ct) });
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or ArgumentException)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    private static async Task<(IIntegrationAdapter Adapter, IntegrationConnection Connection)?> ResolveAsync(
        Guid integrationId,
        IEnumerable<IIntegrationAdapter> adapters,
        IntegrationCredentialProtector protector,
        VeltrixControlStore database,
        CancellationToken cancellationToken)
    {
        var record = await database.GetIntegrationConnectionAsync(integrationId, cancellationToken);
        if (record is null) return null;
        var adapter = adapters.FirstOrDefault(item => string.Equals(item.Kind, record.Value.Kind, StringComparison.OrdinalIgnoreCase));
        if (adapter is null) return null;
        var credential = record.Value.ProtectedCredential is null ? null : protector.Unprotect(record.Value.ProtectedCredential);
        return (adapter, new IntegrationConnection(record.Value.BaseUrl, credential));
    }

    private static string? Validate(IntegrationRequest request, IEnumerable<IIntegrationAdapter> adapters)
    {
        if (!IntegrationKinds.IsKnown(request.Kind)) return $"Kind must be one of: {string.Join(", ", IntegrationKinds.Known)}.";
        if (!adapters.Any(item => string.Equals(item.Kind, request.Kind, StringComparison.OrdinalIgnoreCase))) return "The requested integration kind is not available.";
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > IntegrationLimits.MaxNameLength) return "A name is required and must not exceed 128 characters.";
        if (string.IsNullOrWhiteSpace(request.BaseUrl) || request.BaseUrl.Length > IntegrationLimits.MaxUrlLength) return "A valid endpoint URL is required.";
        if (request.Credential is { Length: > IntegrationLimits.MaxCredentialLength }) return "The credential is too long.";

        var url = request.BaseUrl.Trim();
        if (request.Kind.Equals("Docker", StringComparison.OrdinalIgnoreCase))
        {
            if (url.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
            if (url.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                var host = url.Contains("://", StringComparison.Ordinal) ? url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..] : url;
                var authority = host.Split('/', 2)[0];
                var hostName = authority.Split(':')[0];
                if (hostName is "localhost" or "127.0.0.1" or "::1") return null;
                return "Plaintext Docker endpoints are allowed only for loopback. Use npipe:// or https:// for remote engines.";
            }
            return "Use npipe://, tcp:// (loopback), http:// (loopback), or https:// for Docker.";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "A valid absolute URL is required.";
        if (uri.Scheme == Uri.UriSchemeHttps) return null;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return null;
        return "The Pterodactyl panel must use HTTPS. HTTP is allowed only for loopback.";
    }
}
