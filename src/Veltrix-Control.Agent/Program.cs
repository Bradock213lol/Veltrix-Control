using VeltrixControl.Agent;
using VeltrixControl.Agent.Operations;
using VeltrixControl.Agent.Security;
using VeltrixControl.Agent.Telemetry;
using VeltrixControl.Agent.Transfers;
using VeltrixControl.Agent.Transport;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AddWindowsService(options => options.ServiceName = "Veltrix-Control Managed Node");
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Logging.AddEventLog(settings => settings.SourceName = "Veltrix-Control Managed Node");
var options = builder.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();
ValidateOptions(options);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<DeviceIdentityStore>();
builder.Services.AddSingleton<WindowsHardwareProbe>();
builder.Services.AddSingleton<FileOperations>();
builder.Services.AddSingleton<TerminalSessionManager>();
builder.Services.AddSingleton<AdminOperations>();
builder.Services.AddSingleton<SoftwareOperations>();
builder.Services.AddSingleton<WindowsUpdateOperations>();
builder.Services.AddSingleton<BackupOperations>();
builder.Services.AddSingleton<OperationInbox>();
builder.Services.AddSingleton<OperationExecutor>();
builder.Services.AddSingleton(new HttpClient(CreateHandler(options))
{
    BaseAddress = new Uri(options.ControllerUrl.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromSeconds(30)
});
builder.Services.AddSingleton<AgentApiClient>();
builder.Services.AddSingleton<TransferService>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<TransferWorker>();
builder.Services.AddHostedService<OperationWorker>();
await builder.Build().RunAsync();

static void ValidateOptions(AgentOptions options)
{
    if (!Uri.TryCreate(options.ControllerUrl, UriKind.Absolute, out var controller))
        throw new InvalidOperationException("Agent:ControllerUrl must be an absolute URI.");
    var loopback = controller.IsLoopback;
    if (controller.Scheme != Uri.UriSchemeHttps && !(loopback && options.AllowInsecureLoopback))
        throw new InvalidOperationException("The controller must use HTTPS. HTTP is allowed only for explicit loopback development.");
    options.DataDirectory = Path.GetFullPath(options.DataDirectory);
    options.ManagedRoot = Path.GetFullPath(options.ManagedRoot);
}

static HttpClientHandler CreateHandler(AgentOptions options)
{
    var handler = new HttpClientHandler();
    var expectedThumbprint = NormalizeThumbprint(options.ControllerCertificateThumbprint);
    if (!string.IsNullOrEmpty(expectedThumbprint))
    {
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            certificate is not null && NormalizeThumbprint(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)) == expectedThumbprint;
    }
    return handler;
}

static string NormalizeThumbprint(string? value) =>
    new((value ?? string.Empty).Where(char.IsAsciiHexDigit).Select(char.ToUpperInvariant).ToArray());
