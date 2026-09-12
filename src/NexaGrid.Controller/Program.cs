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
using NexaGrid.Controller;
using NexaGrid.Controller.Hubs;
using NexaGrid.Controller.Security;
using NexaGrid.Controller.Services;
using NexaGrid.Controller.Validation;
using NexaGrid.Core.Security;
using NexaGrid.Infrastructure;
using NexaGrid.Contracts;

if (args is ["--prepare-combined-role"])
{
    var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    CombinedRoleBootstrap.Prepare(
        Path.Combine(commonData, "NexaGrid"),
        Path.Combine(commonData, "NexaGrid", "Agent"));
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "NexaGrid Controller");
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
builder.Services.AddSingleton<NexaGridStore>();
builder.Services.AddScoped<AgentMessageVerifier>();
builder.Services.AddSignalR();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "NexaGrid.Session";
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
var store = app.Services.GetRequiredService<NexaGridStore>();
await store.InitializeAsync();
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
        context.Request.Headers["X-NexaGrid-Request"] != "ui")
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "Missing request verification header." });
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "0.1.0" }));
app.MapGet("/api/setup/status", async (NexaGridStore database, CancellationToken ct) => Results.Ok(new { required = !await database.HasUsersAsync(ct) }));

app.MapPost("/api/setup", async (SetupRequest request, HttpContext context, NexaGridStore database, CancellationToken ct) =>
{
    var error = InputValidator.Setup(request);
    if (error is not null) return Results.BadRequest(new { error });
    if (!await database.CreateOwnerAsync(request.Username, PasswordHasher.Hash(request.Password), ct)) return Results.Conflict(new { error = "Controller setup is already complete." });
    await SignInAsync(context, request.Username, "Owner");
    return Results.Ok(new { username = request.Username, role = "Owner" });
}).RequireRateLimiting("authentication");

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context, NexaGridStore database, CancellationToken ct) =>
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

app.MapPost("/api/auth/logout", async (HttpContext context, NexaGridStore database, CancellationToken ct) =>
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

app.MapPost("/api/enrollment/tokens", async (CreateEnrollmentTokenRequest request, ClaimsPrincipal user, NexaGridStore database, CancellationToken ct) =>
{
    if (!user.HasPermission("admin.manage")) return Results.Forbid();
    if (request.LifetimeMinutes is < 1 or > 60) return Results.BadRequest(new { error = "Lifetime must be between 1 and 60 minutes." });
    return Results.Ok(await database.CreateEnrollmentTokenAsync(user.Identity!.Name!, request.LifetimeMinutes, ct));
}).RequireAuthorization();

app.MapPost("/api/enrollment/tokens/{tokenId:guid}/revoke", async (Guid tokenId, ClaimsPrincipal user, NexaGridStore database, CancellationToken ct) =>
{
    if (!user.HasPermission("admin.manage")) return Results.Forbid();
    return await database.RevokeEnrollmentTokenAsync(tokenId, user.Identity!.Name!, ct)
        ? Results.NoContent()
        : Results.NotFound();
}).RequireAuthorization();

app.MapPost("/api/agent/enroll", async (EnrollmentRequest request, NexaGridStore database, CancellationToken ct) =>
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

app.MapPost("/api/agent/heartbeat", async (SignedAgentMessage message, AgentMessageVerifier verifier, NexaGridStore database, IHubContext<FleetHub> hub, IOptions<ControllerOptions> options, CancellationToken ct) =>
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

app.MapPost("/api/agent/operation-result", async (SignedAgentMessage message, AgentMessageVerifier verifier, NexaGridStore database, IHubContext<FleetHub> hub, CancellationToken ct) =>
{
    var result = await verifier.VerifyAsync<OperationResultPayload>(message, ct);
    if (result is null) return Results.Unauthorized();
    if (result.State is OperationState.Queued or OperationState.Running || result.Error?.Length > 2048 || result.ResultJson?.Length > 1_000_000)
        return Results.BadRequest(new { error = "Operation result is invalid." });
    try
    {
        await database.CompleteOperationAsync(message.DeviceId, result, ct);
    }
    catch (InvalidOperationException)
    {
        return Results.Conflict(new { error = "Operation is not awaiting a result." });
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
management.MapGet("/devices", async (ClaimsPrincipal user, NexaGridStore database, IOptions<ControllerOptions> options, CancellationToken ct) =>
    user.HasPermission("device.view")
        ? Results.Ok(await database.GetDevicesAsync(TimeSpan.FromSeconds(options.Value.OfflineAfterSeconds), ct))
        : Results.Forbid());
management.MapGet("/audit", async (ClaimsPrincipal user, NexaGridStore database, int? limit, CancellationToken ct) =>
    user.HasPermission("audit.view") ? Results.Ok(await database.GetAuditEventsAsync(limit ?? 100, ct)) : Results.Forbid());
management.MapGet("/audit/integrity", async (ClaimsPrincipal user, NexaGridStore database, CancellationToken ct) =>
    user.HasPermission("admin.manage") ? Results.Ok(new { valid = await database.VerifyAuditChainAsync(ct) }) : Results.Forbid());
management.MapGet("/operations/{operationId:guid}", async (Guid operationId, NexaGridStore database, CancellationToken ct) =>
{
    var operation = await database.GetOperationAsync(operationId, ct);
    return operation is null ? Results.NotFound() : Results.Ok(operation);
});
management.MapPost("/devices/{deviceId:guid}/operations", async (Guid deviceId, OperationRequest request, ClaimsPrincipal user, NexaGridStore database, CancellationToken ct) =>
{
    var permission = request.Kind == OperationKind.ListDirectory ? "device.files" : "device.power";
    if (!user.HasPermission(permission)) return Results.Forbid();
    if (!request.Confirmed) return Results.BadRequest(new { error = "Explicit confirmation is required." });
    if (request.Kind == OperationKind.ListDirectory && (request.Argument?.Length ?? 0) > 1024) return Results.BadRequest(new { error = "Path is too long." });
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
