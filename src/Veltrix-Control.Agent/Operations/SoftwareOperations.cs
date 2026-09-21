using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeltrixControl.Contracts;

namespace VeltrixControl.Agent.Operations;

public sealed partial class SoftwareOperations(AgentOptions options, ILogger<SoftwareOperations> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool Handles(OperationKind kind) => kind is
        OperationKind.InstallSoftware or OperationKind.UninstallSoftware or OperationKind.UpgradeSoftware;

    public async Task<OperationResultPayload> ExecuteAsync(OperationAssignment operation, CancellationToken cancellationToken)
    {
        try
        {
            return operation.Kind switch
            {
                OperationKind.InstallSoftware => await InstallAsync(operation, cancellationToken),
                OperationKind.UninstallSoftware => await RunWingetAsync(operation, "uninstall", cancellationToken),
                OperationKind.UpgradeSoftware => await RunWingetAsync(operation, "upgrade", cancellationToken),
                _ => Failure(operation.Id, "Unsupported software operation.")
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.ComponentModel.Win32Exception or InvalidDataException or CryptographicException or HttpRequestException or TaskCanceledException)
        {
            LogSoftwareFailed(logger, exception, operation.Id, operation.Kind);
            return Failure(operation.Id, exception.Message);
        }
    }

    private async Task<OperationResultPayload> InstallAsync(OperationAssignment operation, CancellationToken cancellationToken)
    {
        var argument = Deserialize<SoftwareInstallArgument>(operation);
        ValidatePackageId(argument.PackageId);
        return argument.Source switch
        {
            SoftwareSource.Winget => await WingetAsync(operation, "install", argument.PackageId, argument.Version, cancellationToken),
            SoftwareSource.Choco => await ChocoAsync(operation, "install", argument.PackageId, argument.Version, cancellationToken),
            SoftwareSource.Msi => await InstallFromDownloadAsync(operation, argument, msiexec: true, cancellationToken),
            SoftwareSource.Exe => await InstallFromDownloadAsync(operation, argument, msiexec: false, cancellationToken),
            _ => Failure(operation.Id, "The package source is invalid.")
        };
    }

    private async Task<OperationResultPayload> RunWingetAsync(OperationAssignment operation, string action, CancellationToken cancellationToken)
    {
        var argument = Deserialize<SoftwareUninstallArgument>(operation);
        ValidatePackageId(argument.PackageId);
        return argument.Source switch
        {
            SoftwareSource.Winget => await WingetAsync(operation, action, argument.PackageId, null, cancellationToken),
            SoftwareSource.Choco => await ChocoAsync(operation, action, argument.PackageId, null, cancellationToken),
            _ => Failure(operation.Id, "Only WinGet or Chocolatey packages can be changed with this action. Use the vendor uninstaller for MSI or EXE packages.")
        };
    }

    private async Task<OperationResultPayload> ChocoAsync(OperationAssignment operation, string action, string packageId, string? version, CancellationToken cancellationToken)
    {
        var choco = FindChoco()
            ?? throw new InvalidOperationException("Chocolatey is not installed on this node. Publish an MSI or EXE package instead.");
        var arguments = action switch
        {
            "uninstall" => $"uninstall {packageId} -y --no-progress",
            "upgrade" => $"upgrade {packageId} -y --no-progress",
            _ => $"install {packageId} -y --no-progress"
        };
        if (!string.IsNullOrWhiteSpace(version) && action != "uninstall") arguments += $" --version={version}";
        var (exitCode, output) = await RunAsync(choco, arguments, options.DataDirectory, TimeSpan.FromMinutes(60), cancellationToken);
        var result = new SoftwareActionResult("Choco", packageId, action, exitCode,
            output.Length > SoftwareLimits.MaxOutputLength ? output[^SoftwareLimits.MaxOutputLength..] : output);
        return exitCode == 0 || exitCode == 3010
            ? Success(operation.Id, result)
            : Failure(operation.Id, $"{action} exited with code {exitCode}. {FirstLines(output)}");
    }

    private static string? FindChoco()
    {
        var candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", "choco.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private async Task<OperationResultPayload> WingetAsync(OperationAssignment operation, string action, string packageId, string? version, CancellationToken cancellationToken)
    {
        var winget = FindWinget()
            ?? throw new InvalidOperationException("WinGet is not available to the service account. Publish an MSI or EXE package instead.");
        var arguments = $"{action} --id \"{packageId}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
        if (!string.IsNullOrWhiteSpace(version)) arguments += $" --version \"{version}\"";
        var (exitCode, output) = await RunAsync(winget, arguments, options.DataDirectory, TimeSpan.FromMinutes(60), cancellationToken);
        var result = new SoftwareActionResult("Winget", packageId, action, exitCode,
            output.Length > SoftwareLimits.MaxOutputLength ? output[^SoftwareLimits.MaxOutputLength..] : output);
        return exitCode == 0 ? Success(operation.Id, result) : Failure(operation.Id, $"{action} exited with code {exitCode}. {FirstLines(output)}");
    }

    private async Task<OperationResultPayload> InstallFromDownloadAsync(OperationAssignment operation, SoftwareInstallArgument argument, bool msiexec, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(argument.Url) || !Uri.TryCreate(argument.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("A verified HTTPS download URL is required for MSI and EXE packages.");
        if (string.IsNullOrWhiteSpace(argument.Sha256) || argument.Sha256.Length != 64)
            throw new ArgumentException("A SHA-256 checksum is required for MSI and EXE packages.");

        var directory = Path.Combine(options.DataDirectory, "software", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var extension = msiexec ? ".msi" : ".exe";
        var download = Path.Combine(directory, "package" + extension);
        try
        {
            await DownloadAsync(uri, download, cancellationToken);
            var hash = ComputeHash(download);
            if (!string.Equals(hash, argument.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded package failed checksum verification and was not executed.");

            string fileName;
            string arguments;
            if (msiexec)
            {
                fileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
                arguments = $"/i \"{download}\" /qn /norestart {argument.SilentArgs ?? string.Empty}".Trim();
            }
            else
            {
                fileName = download;
                arguments = argument.SilentArgs ?? "/S";
            }

            var (exitCode, output) = await RunAsync(fileName, arguments, directory, TimeSpan.FromMinutes(60), cancellationToken);
            var result = new SoftwareActionResult(msiexec ? "Msi" : "Exe", argument.PackageId, "install", exitCode,
                output.Length > SoftwareLimits.MaxOutputLength ? output[^SoftwareLimits.MaxOutputLength..] : output);
            return exitCode == 0 || exitCode == 3010
                ? Success(operation.Id, result with { Output = exitCode == 3010 ? "Reboot required. " + result.Output : result.Output })
                : Failure(operation.Id, $"The installer exited with code {exitCode}. {FirstLines(output)}");
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static async Task DownloadAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > SoftwareLimits.MaxInstallerBytes)
            throw new InvalidDataException("The installer exceeds the allowed size.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > SoftwareLimits.MaxInstallerBytes) throw new InvalidDataException("The installer exceeds the allowed size.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string fileName, string arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Windows did not start the installer.");

        var output = new StringBuilder();
        process.OutputDataReceived += (_, args) => Append(output, args.Data);
        process.ErrorDataReceived += (_, args) => Append(output, args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            throw new TimeoutException("The installer did not finish within the allowed time.");
        }
        return (process.ExitCode, output.ToString());
    }

    private static readonly object OutputSync = new();

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is null) return;
        lock (OutputSync)
        {
            if (builder.Length < SoftwareLimits.MaxOutputLength * 2)
            {
                builder.AppendLine(line);
            }
        }
    }

    private static string? FindWinget()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(local)) return local;
        var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        var candidate = Path.Combine(programFiles, "winget.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static void ValidatePackageId(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId) || packageId.Length > 256 || packageId.Contains('\n') || packageId.Contains('\r') || packageId.Contains('"'))
            throw new ArgumentException("The package identifier is invalid.");
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string FirstLines(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", lines.Take(4));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static T Deserialize<T>(OperationAssignment operation) =>
        JsonSerializer.Deserialize<T>(operation.Argument ?? string.Empty, JsonOptions)
        ?? throw new ArgumentException("The operation argument is invalid.");

    private static OperationResultPayload Success(Guid id, object? result) =>
        new(id, OperationState.Succeeded, result is null ? null : JsonSerializer.Serialize(result, JsonOptions), null, DateTimeOffset.UtcNow);

    private static OperationResultPayload Failure(Guid id, string error) =>
        new(id, OperationState.Failed, null, error, DateTimeOffset.UtcNow);

    [LoggerMessage(70, LogLevel.Warning, "Software operation {operationId} ({operationKind}) failed")]
    private static partial void LogSoftwareFailed(ILogger logger, Exception exception, Guid operationId, OperationKind operationKind);
}
