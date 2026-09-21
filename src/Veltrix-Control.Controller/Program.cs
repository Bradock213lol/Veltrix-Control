using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using VeltrixControl.Controller;
using VeltrixControl.Controller.Endpoints;
using VeltrixControl.Controller.Hubs;
using VeltrixControl.Controller.Security;
using VeltrixControl.Controller.Services;
using VeltrixControl.Controller.Validation;
using VeltrixControl.Core.Security;
using VeltrixControl.Infrastructure;
using VeltrixControl.Integrations;
using VeltrixControl.Contracts;

if (args is ["--prepare-combined-role"])
{
    var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    CombinedRoleBootstrap.Prepare(
        Path.Combine(commonData, "Veltrix-Control"),
        Path.Combine(commonData, "Veltrix-Control", "Agent"));
    return;
}

if (args is ["--reset-owner", var resetUsername])
{
    var resetOptions = new ControllerOptions();
    var resetStore = new VeltrixControlStore(Microsoft.Extensions.Options.Options.Create(new StoreOptions
    {
        DatabasePath = Path.Combine(resetOptions.DataDirectory, "controller.db")
    }));
    await resetStore.InitializeAsync();
    var newPassword = Environment.GetEnvironmentVariable("VELTRIX_OWNER_PASSWORD");
    if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 12)
    {
        Console.Error.WriteLine("Set VELTRIX_OWNER_PASSWORD (at least 12 characters) before running --reset-owner.");
        return;
    }
    var resetUser = await resetStore.ValidateUsernameAsync(resetUsername);
    if (resetUser is null)
    {
        Console.Error.WriteLine($"User '{resetUsername}' does not exist. Existing users: {string.Join(", ", await resetStore.GetUsernamesAsync())}");
        return;
    }
    await resetStore.ResetUserPasswordAsync(resetUser.Value.Id, PasswordHasher.Hash(newPassword), "cli-reset");
    Console.WriteLine($"Password reset for '{resetUser.Value.Username}' ({resetUser.Value.Role}). The new password is active immediately.");
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Host.UseWindowsService(options => options.ServiceName = "Veltrix-Control Controller");
builder.Services.Configure<ControllerOptions>(builder.Configuration.GetSection("Controller"));
var controllerConfiguration = builder.Configuration.GetSection("Controller").Get<ControllerOptions>() ?? new ControllerOptions();
builder.Services.AddOptions<StoreOptions>().Configure<IConfiguration>((options, configuration) =>
{
    var configured = configuration.GetSection("Controller").Get<ControllerOptions>() ?? new ControllerOptions();
    options.DatabasePath = Path.Combine(configured.DataDirectory, "controller.db");
});

X509Certificate2? controllerCertificate = null;
if (!builder.Environment.IsEnvironment("Testing"))
{
    controllerCertificate = controllerConfiguration.EnableHttpsListener
        ? ControllerCertificate.LoadOrCreate(controllerConfiguration.DataDirectory)
        : null;
    builder.WebHost.ConfigureKestrel(options =>
    {
        if (controllerCertificate is not null)
        {
            options.ListenAnyIP(controllerConfiguration.HttpsPort, listen => listen.UseHttps(controllerCertificate));
        }
        if (controllerConfiguration.EnableLocalHttpListener)
        {
            options.ListenLocalhost(controllerConfiguration.LocalHttpPort);
        }
    });
}
builder.Services.AddSingleton(new ControllerRuntimeInfo(
    controllerConfiguration.HttpsPort,
    controllerCertificate?.GetCertHashString(HashAlgorithmName.SHA256)));

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<VeltrixControlStore>();
builder.Services.AddScoped<AgentMessageVerifier>();
builder.Services.AddHostedService<AlertEvaluator>();
builder.Services.AddHostedService<AutomationEngine>();
builder.Services.AddHostedService<ComputeScheduler>();
builder.Services.AddSingleton<GameServerCoordinator>();
builder.Services.AddHostedService<GameServerMonitor>();
builder.Services.AddHostedService<RetentionService>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationService>());
// Integration credential protection uses a local AES-GCM key stored beside the database.
builder.Services.AddSingleton<IntegrationCredentialProtector>();
builder.Services.AddSingleton<IIntegrationAdapter, PterodactylAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, DockerAdapter>();
builder.Services.AddSignalR();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Veltrix-Control.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("authentication", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("agent", context => RateLimitPartition.GetTokenBucketLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "agent",
        _ => new TokenBucketRateLimiterOptions { TokenLimit = 120, TokensPerPeriod = 120, ReplenishmentPeriod = TimeSpan.FromMinutes(1), AutoReplenishment = true }));
});

var app = builder.Build();
var store = app.Services.GetRequiredService<VeltrixControlStore>();
await store.InitializeAsync();
if (controllerConfiguration.EnableRecoveryAccount && await store.HasOwnerAsync())
{
    var recoveryCreated = await store.EnsureRecoveryAccountAsync(controllerConfiguration.RecoveryAccountUsername, controllerConfiguration.RecoveryAccountPassword);
    if (recoveryCreated)
    {
        RecoveryAccountLog.Created(app.Logger, controllerConfiguration.RecoveryAccountUsername);
    }
}
var runtimeControllerOptions = app.Services.GetRequiredService<IOptions<ControllerOptions>>().Value;
var bootstrapCodePath = Path.Combine(runtimeControllerOptions.DataDirectory, CombinedRoleBootstrap.BootstrapFileName);
if (File.Exists(bootstrapCodePath))
{
    var bootstrapCode = (await File.ReadAllTextAsync(bootstrapCodePath)).Trim();
    await store.ImportBootstrapEnrollmentCodeAsync(bootstrapCode);
    File.Delete(bootstrapCodePath);
}

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1
});
app.Use(async (context, next) =>
{
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    var methodChangesState = HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) ||
        HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method);
    var browserApi = context.Request.Path.StartsWithSegments("/api") &&
        !context.Request.Path.StartsWithSegments("/api/agent") &&
        !context.Request.Path.StartsWithSegments("/api/auth/login") &&
        !context.Request.Path.StartsWithSegments("/api/setup");
    if (methodChangesState && browserApi && context.User.Identity?.IsAuthenticated == true &&
        context.Request.Headers["X-Veltrix-Control-Request"] != "ui")
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "Missing request verification header." });
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "0.10.8" }));
app.MapGet("/api/setup/status", async (VeltrixControlStore database, CancellationToken ct) => Results.Ok(new { required = !await database.HasUsersAsync(ct) }));

app.MapPost("/api/setup", async (SetupRequest request, HttpContext context, VeltrixControlStore database, IOptions<ControllerOptions> controllerOptions, CancellationToken ct) =>
{
    var error = InputValidator.Setup(request);
    if (error is not null) return Results.BadRequest(new { error });
    if (!await database.CreateOwnerAsync(request.Username, PasswordHasher.Hash(request.Password), ct)) return Results.Conflict(new { error = "Controller setup is already complete." });
    var options = controllerOptions.Value;
    if (options.EnableRecoveryAccount)
    {
        await database.EnsureRecoveryAccountAsync(options.RecoveryAccountUsername, options.RecoveryAccountPassword, ct);
    }
    await SignInAsync(context, request.Username, "Owner");
    return Results.Ok(new { username = request.Username, role = "Owner" });
}).RequireRateLimiting("authentication");

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context, VeltrixControlStore database, CancellationToken ct) =>
{
    var error = InputValidator.Login(request);
    if (error is not null) return Results.BadRequest(new { error });
    var user = await database.ValidateUserAsync(request.Username, request.Password, ct);
    if (user is null)
    {
        await database.RecordAuditEventAsync(request.Username, "auth.login", "controller", "failed", null, ct);
        await Task.Delay(RandomNumberGenerator.GetInt32(80, 180), ct);
        return Results.Unauthorized();
    }
    await SignInAsync(context, user.Value.Username, user.Value.Role);
    await database.RecordAuditEventAsync(user.Value.Username, "auth.login", "controller", "succeeded", null, ct);
    return Results.Ok(new { user.Value.Username, user.Value.Role });
}).RequireRateLimiting("authentication");

app.MapPost("/api/auth/logout", async (HttpContext context, VeltrixControlStore database, CancellationToken ct) =>
{
    await database.RecordAuditEventAsync(context.User.Identity!.Name!, "auth.logout", "controller", "succeeded", null, ct);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/auth/me", (ClaimsPrincipal user) => Results.Ok(new
{
    username = user.Identity?.Name,
    role = user.FindFirstValue(ClaimTypes.Role)
})).RequireAuthorization();

app.MapPost("/api/enrollment/tokens", async (CreateEnrollmentTokenRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
{
    if (!user.HasPermission("admin.manage")) return Results.Forbid();
    if (request.LifetimeMinutes is < 1 or > 60) return Results.BadRequest(new { error = "Lifetime must be between 1 and 60 minutes." });
    return Results.Ok(await database.CreateEnrollmentTokenAsync(user.Identity!.Name!, request.LifetimeMinutes, ct));
}).RequireAuthorization();

app.MapPost("/api/enrollment/tokens/{tokenId:guid}/revoke", async (Guid tokenId, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
{
    if (!user.HasPermission("admin.manage")) return Results.Forbid();
    return await database.RevokeEnrollmentTokenAsync(tokenId, user.Identity!.Name!, ct)
        ? Results.NoContent()
        : Results.NotFound();
}).RequireAuthorization();

app.MapPost("/api/agent/enroll", async (EnrollmentRequest request, VeltrixControlStore database, CancellationToken ct) =>
{
    var error = InputValidator.Enrollment(request);
    if (error is not null) return Results.BadRequest(new { error });
    try
    {
        var enrollment = await database.EnrollDeviceAsync(request, ct);
        return enrollment is null ? Results.Unauthorized() : Results.Ok(enrollment);
    }
    catch (CryptographicException)
    {
        return Results.BadRequest(new { error = "Device public key is invalid." });
    }
}).RequireRateLimiting("authentication");

app.MapPost("/api/agent/heartbeat", async (SignedAgentMessage message, AgentMessageVerifier verifier, VeltrixControlStore database, IHubContext<FleetHub> hub, IOptions<ControllerOptions> options, CancellationToken ct) =>
{
    var payload = await verifier.VerifyAsync<HeartbeatPayload>(message, ct);
    if (payload is null) return Results.Unauthorized();
    var error = InputValidator.Heartbeat(payload);
    if (error is not null) return Results.BadRequest(new { error });
    await database.RecordHeartbeatAsync(message.DeviceId, payload, ct);
    var operations = await database.ClaimPendingOperationsAsync(message.DeviceId, ct);
    await hub.Clients.All.SendAsync("deviceUpdated", message.DeviceId, ct);
    return Results.Ok(new HeartbeatResponse(operations, Math.Clamp(options.Value.HeartbeatSeconds, 2, 60)));
}).RequireRateLimiting("agent");

app.MapPost("/api/agent/operation-result", async (SignedAgentMessage message, AgentMessageVerifier verifier, VeltrixControlStore database, GameServerCoordinator gameServers, IHubContext<FleetHub> hub, CancellationToken ct) =>
{
    var result = await verifier.VerifyAsync<OperationResultPayload>(message, ct);
    if (result is null) return Results.Unauthorized();
    if (result.State is OperationState.Queued or OperationState.Running || result.Error?.Length > 2048 || result.ResultJson?.Length > 1_000_000)
        return Results.BadRequest(new { error = "Operation result is invalid." });
    var operation = await database.GetOperationAsync(result.OperationId, ct);
    try
    {
        await database.CompleteOperationAsync(message.DeviceId, result, ct);
    }
    catch (InvalidOperationException)
    {
        return Results.Conflict(new { error = "Operation is not awaiting a result." });
    }
    if (operation?.Kind == OperationKind.ScanWindowsUpdates && result.State == OperationState.Succeeded && result.ResultJson is not null)
    {
        try
        {
            var scan = System.Text.Json.JsonSerializer.Deserialize<WindowsUpdateScanResult>(result.ResultJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (scan is not null) await database.RecordWindowsUpdateScanAsync(message.DeviceId, scan, ct);
        }
        catch (System.Text.Json.JsonException)
        {
            // The scan payload is validated before it is stored; malformed data is ignored.
        }
    }
    if (operation?.Kind == OperationKind.CreateBackup && operation.Argument is not null)
    {
        try
        {
            using var argumentDocument = System.Text.Json.JsonDocument.Parse(operation.Argument);
            if (argumentDocument.RootElement.TryGetProperty("backupId", out var backupIdElement) && backupIdElement.TryGetGuid(out var backupId))
            {
                if (result.State == OperationState.Succeeded && result.ResultJson is not null)
                {
                    var backupResult = System.Text.Json.JsonSerializer.Deserialize<BackupOperationResult>(result.ResultJson,
                        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                    await database.CompleteBackupAsync(backupId, backupResult is not null, backupResult?.SizeBytes ?? 0, backupResult?.Sha256, result.Error, ct);
                }
                else
                {
                    await database.CompleteBackupAsync(backupId, false, 0, null, result.Error ?? "The backup did not complete.", ct);
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed argument is rejected before the operation is queued.
        }
    }
    if (operation?.Kind == OperationKind.RunComputeJob && operation.Argument is not null)
    {
        try
        {
            using var argumentDocument = System.Text.Json.JsonDocument.Parse(operation.Argument);
            if (argumentDocument.RootElement.TryGetProperty("jobId", out var jobIdElement) && jobIdElement.TryGetGuid(out var jobId))
            {
                var succeeded = false;
                string? error = result.Error;
                if (result.State == OperationState.Succeeded && result.ResultJson is not null)
                {
                    var jobResult = System.Text.Json.JsonSerializer.Deserialize<ComputeJobResult>(result.ResultJson,
                        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                    if (jobResult is not null)
                    {
                        succeeded = !jobResult.TimedOut && jobResult.ExitCode == 0;
                        if (!succeeded) error = jobResult.TimedOut ? "The job timed out." : $"The job exited with code {jobResult.ExitCode}.";
                    }
                }
                if (succeeded)
                {
                    await database.CompleteComputeJobAsync(jobId, "Succeeded", result.ResultJson, null, ct);
                }
                else
                {
                    await database.RequeueOrFailComputeJobAsync(jobId, error ?? "The job failed.", ct);
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed argument is rejected before the operation is queued.
        }
    }
    if (operation is not null)
    {
        await gameServers.HandleResultAsync(operation, result, ct);
    }
    await hub.Clients.All.SendAsync("operationUpdated", result.OperationId, ct);
    return Results.NoContent();
}).RequireRateLimiting("agent");

var management = app.MapGroup("/api").RequireAuthorization();
management.MapGet("/controller/info", (ControllerRuntimeInfo info) => Results.Ok(new
{
    httpsPort = info.HttpsPort,
    certificateSha256 = info.CertificateSha256
}));
management.MapGet("/devices", async (ClaimsPrincipal user, VeltrixControlStore database, IOptions<ControllerOptions> options, CancellationToken ct) =>
    user.HasPermission("device.view")
        ? Results.Ok(await database.GetDevicesAsync(TimeSpan.FromSeconds(options.Value.OfflineAfterSeconds), ct))
        : Results.Forbid());
management.MapGet("/audit", async (ClaimsPrincipal user, VeltrixControlStore database, int? limit, CancellationToken ct) =>
    user.HasPermission("audit.view") ? Results.Ok(await database.GetAuditEventsAsync(limit ?? 100, ct)) : Results.Forbid());
management.MapGet("/audit/integrity", async (ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
    user.HasPermission("admin.manage") ? Results.Ok(new { valid = await database.VerifyAuditChainAsync(ct) }) : Results.Forbid());
management.MapGet("/operations/{operationId:guid}", async (Guid operationId, VeltrixControlStore database, CancellationToken ct) =>
{
    var operation = await database.GetOperationAsync(operationId, ct);
    return operation is null ? Results.NotFound() : Results.Ok(operation);
});
management.MapPost("/devices/{deviceId:guid}/operations", async (Guid deviceId, OperationRequest request, ClaimsPrincipal user, VeltrixControlStore database, CancellationToken ct) =>
{
    if (!Enum.IsDefined(request.Kind)) return Results.BadRequest(new { error = "Operation kind is invalid." });
    if (!user.HasPermission(RolePermissions.PermissionFor(request.Kind))) return Results.Forbid();
    if (!request.Confirmed) return Results.BadRequest(new { error = "Explicit confirmation is required." });
    var argumentError = InputValidator.OperationArgument(request.Kind, request.Argument);
    if (argumentError is not null) return Results.BadRequest(new { error = argumentError });
    try
    {
        var operation = await database.CreateOperationAsync(deviceId, user.Identity!.Name!, request, ct);
        return Results.Accepted($"/api/operations/{operation.Id:D}", operation);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

management.MapManagementTransfers();
management.MapManagementAdmin();
management.MapManagementSoftware();
management.MapManagementMonitoring();
management.MapManagementCompute();
management.MapManagementGameServers();
management.MapManagementIntegrations();
management.MapManagementUsers();
management.MapManagementGovernance();

app.MapAgentTransfers();

app.MapHub<FleetHub>("/hubs/fleet");
app.MapFallbackToFile("index.html");
app.Run();

static async Task SignInAsync(HttpContext context, string username, string role)
{
    var identity = new ClaimsIdentity([
        new Claim(ClaimTypes.Name, username),
        new Claim(ClaimTypes.Role, role)
    ], CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
}

public partial class Program;

namespace VeltrixControl.Controller
{
    public static partial class RecoveryAccountLog
    {
        [LoggerMessage(300, LogLevel.Warning, "Recovery account '{username}' was created with the configured default password. Change or delete it in Settings -> User accounts once Owner access is verified.")]
        public static partial void Created(ILogger logger, string username);
    }
}
