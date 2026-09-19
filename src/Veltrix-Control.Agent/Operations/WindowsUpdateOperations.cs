using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VeltrixControl.Contracts;

namespace VeltrixControl.Agent.Operations;

public sealed partial class WindowsUpdateOperations(AgentOptions options, ILogger<WindowsUpdateOperations> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private const string ScanScript = """
        $ErrorActionPreference = 'Stop'
        $session = New-Object -ComObject Microsoft.Update.Session
        $searcher = $session.CreateUpdateSearcher()
        $result = $searcher.Search("IsInstalled=0 and IsHidden=0")
        $updates = @()
        foreach ($update in $result.Updates) {
            $updates += [pscustomobject]@{
                UpdateId = [string]$update.Identity.UpdateID
                Title = [string]$update.Title
                KbArticle = [string]($update.KBArticleIDs | Select-Object -First 1)
                SizeBytes = [long]$update.MaxDownloadSize
                Severity = [string]$update.MsrcSeverity
                Downloaded = [bool]$update.IsDownloaded
            }
        }
        [pscustomobject]@{ Updates = $updates } | ConvertTo-Json -Depth 4 -Compress
        """;

    private const string InstallScript = """
        param([string[]]$UpdateIds)
        $ErrorActionPreference = 'Stop'
        $session = New-Object -ComObject Microsoft.Update.Session
        $searcher = $session.CreateUpdateSearcher()
        $result = $searcher.Search("IsInstalled=0 and IsHidden=0")
        $collection = New-Object -ComObject Microsoft.Update.UpdateColl
        foreach ($update in $result.Updates) {
            if ($UpdateIds -contains [string]$update.Identity.UpdateID) {
                if (-not $update.EulaAccepted) { $update.AcceptEula() }
                $collection.Add($update) | Out-Null
            }
        }
        $selected = $collection.Count
        $installed = 0
        $failed = 0
        $rebootRequired = $false
        if ($selected -gt 0) {
            $downloader = $session.CreateUpdateDownloader()
            $downloader.Updates = $collection
            $downloader.Download() | Out-Null
            $installer = $session.CreateUpdateInstaller()
            $installer.Updates = $collection
            $installResult = $installer.Install()
            $rebootRequired = [bool]$installResult.RebootRequired
            for ($i = 0; $i -lt $collection.Count; $i++) {
                $code = $installResult.GetUpdateResult($i).ResultCode
                if ($code -eq 2 -or $code -eq 3) { $installed++ } else { $failed++ }
            }
        }
        [pscustomobject]@{ Selected = $selected; Installed = $installed; Failed = $failed; RebootRequired = $rebootRequired } | ConvertTo-Json -Compress
        """;

    public static bool Handles(OperationKind kind) => kind is OperationKind.ScanWindowsUpdates or OperationKind.InstallWindowsUpdate;

    public async Task<OperationResultPayload> ExecuteAsync(OperationAssignment operation, CancellationToken cancellationToken)
    {
        try
        {
            return operation.Kind == OperationKind.ScanWindowsUpdates
                ? await ScanAsync(operation, cancellationToken)
                : await InstallAsync(operation, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.ComponentModel.Win32Exception or InvalidDataException or JsonException or TimeoutException)
        {
            LogUpdateFailed(logger, exception, operation.Id, operation.Kind);
            return Failure(operation.Id, exception.Message);
        }
    }

    private async Task<OperationResultPayload> ScanAsync(OperationAssignment operation, CancellationToken cancellationToken)
    {
        var json = await RunPowerShellAsync(ScanScript, null, TimeSpan.FromMinutes(10), cancellationToken);
        using var document = JsonDocument.Parse(json);
        var updates = new List<WindowsUpdateInfo>();
        if (document.RootElement.TryGetProperty("Updates", out var array))
        {
            foreach (var item in array.EnumerateArray())
            {
                updates.Add(new WindowsUpdateInfo(
                    item.GetProperty("UpdateId").GetString() ?? string.Empty,
                    item.GetProperty("Title").GetString() ?? string.Empty,
                    item.TryGetProperty("KbArticle", out var kb) ? kb.GetString() : null,
                    item.TryGetProperty("SizeBytes", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : null,
                    item.TryGetProperty("Severity", out var severity) ? severity.GetString() : null,
                    item.TryGetProperty("Downloaded", out var downloaded) && downloaded.ValueKind == JsonValueKind.True));
            }
        }
        return Success(operation.Id, new WindowsUpdateScanResult(DateTimeOffset.UtcNow, updates, null));
    }

    private async Task<OperationResultPayload> InstallAsync(OperationAssignment operation, CancellationToken cancellationToken)
    {
        var argument = Deserialize<WindowsUpdateInstallArgument>(operation);
        if (argument.UpdateIds is null || argument.UpdateIds.Length == 0)
            throw new ArgumentException("Select at least one update to install.");
        if (argument.UpdateIds.Length > SoftwareLimits.MaxWindowsUpdatesPerInstall)
            throw new ArgumentException("Too many updates were selected.");
        if (argument.UpdateIds.Any(id => id.Length != 36 || !Guid.TryParse(id, out _)))
            throw new ArgumentException("The update identifiers are invalid.");

        var parameterList = string.Join(",", argument.UpdateIds.Select(id => "'" + id.Replace("'", "''", StringComparison.Ordinal) + "'"));
        var json = await RunPowerShellAsync(InstallScript, parameterList, TimeSpan.FromMinutes(120), cancellationToken);
        using var document = JsonDocument.Parse(json);
        var result = new WindowsUpdateInstallResult(
            document.RootElement.GetProperty("Selected").GetInt32(),
            document.RootElement.GetProperty("Installed").GetInt32(),
            document.RootElement.GetProperty("Failed").GetInt32(),
            document.RootElement.GetProperty("RebootRequired").GetBoolean(),
            string.Empty);
        return result.Failed == 0
            ? Success(operation.Id, result)
            : Failure(operation.Id, $"{result.Failed} of {result.Selected} updates failed to install.");
    }

    private async Task<string> RunPowerShellAsync(string script, string? parameterList, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(options.DataDirectory, "updates");
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "operation.ps1");
        await File.WriteAllTextAsync(scriptPath, script, Encoding.UTF8, cancellationToken);
        var arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"";
        if (parameterList is not null) arguments += $" -UpdateIds {parameterList}";

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "powershell.exe"),
            Arguments = arguments,
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Windows did not start PowerShell.");

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
            throw new TimeoutException("The Windows Update operation did not finish within the allowed time.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Windows Update helper exited with code {process.ExitCode}. {LastLines(output.ToString())}");

        var text = output.ToString().Trim();
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("Windows Update helper returned no data.");
        return text[start..(end + 1)];
    }

    private static readonly object OutputSync = new();

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is null) return;
        lock (OutputSync)
        {
            if (builder.Length < SoftwareLimits.MaxOutputLength)
            {
                builder.AppendLine(line);
            }
        }
    }

    private static string LastLines(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", lines.TakeLast(3));
    }

    private static T Deserialize<T>(OperationAssignment operation) =>
        JsonSerializer.Deserialize<T>(operation.Argument ?? string.Empty, JsonOptions)
        ?? throw new ArgumentException("The operation argument is invalid.");

    private static OperationResultPayload Success(Guid id, object? result) =>
        new(id, OperationState.Succeeded, result is null ? null : JsonSerializer.Serialize(result, JsonOptions), null, DateTimeOffset.UtcNow);

    private static OperationResultPayload Failure(Guid id, string error) =>
        new(id, OperationState.Failed, null, error, DateTimeOffset.UtcNow);

    [LoggerMessage(80, LogLevel.Warning, "Windows Update operation {operationId} ({operationKind}) failed")]
    private static partial void LogUpdateFailed(ILogger logger, Exception exception, Guid operationId, OperationKind operationKind);
}
