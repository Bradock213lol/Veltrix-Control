using System.Data;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VeltrixControl.Contracts;

namespace VeltrixControl.Desktop;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly DesktopSettings _settings = DesktopSettings.Load();
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _terminalTimer = new();
    private ControllerApiClient? _api;
    private UserIdentity? _user;
    private List<DeviceSummary> _devices = [];
    private List<AuditEventView> _auditEvents = [];
    private List<FileEntry> _files = [];
    private List<WindowsUpdateRow> _updates = [];
    private DataTable? _diagnosticTable;
    private string _currentPath = string.Empty;
    private Guid? _terminalSessionId;
    private long _terminalSequence;
    private bool _terminalPolling;
    private bool _setupRequired;
    private bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();
        _refreshTimer.Tick += async (_, _) => await RefreshFleetAsync();
        _terminalTimer.Interval = TimeSpan.FromSeconds(1.2);
        _terminalTimer.Tick += TerminalPollTick;
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _terminalTimer.Stop();
        _api?.Dispose();
        base.OnClosed(e);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AuthControllerUrl.Text = _settings.ControllerUrl;
        SettingsControllerUrl.Text = _settings.ControllerUrl;
        AutoRefreshCheck.IsChecked = _settings.AutoRefresh;
        SelectRefreshInterval(_settings.RefreshSeconds);
        await InitializeConnectionAsync();
    }

    private async Task InitializeConnectionAsync()
    {
        try
        {
            SetStatus("Connecting to Controller…");
            _api ??= new ControllerApiClient(AuthControllerUrl.Text);
            _api.ChangeController(AuthControllerUrl.Text);
            var setup = await _api.GetSetupStatusAsync();
            _setupRequired = setup.Required;
            AuthTitle.Text = setup.Required ? "Create your control plane" : "Welcome back";
            AuthSubtitle.Text = setup.Required
                ? "Create the first Owner account. Use a password with at least 12 characters."
                : "Sign in to your authorized Windows fleet.";
            AuthActionButton.Content = setup.Required ? "Initialize Controller" : "Sign in";
            AuthError.Text = string.Empty;
            SetStatus("Controller ready", true);
            UsernameBox.Focus();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            AuthError.Text = $"Could not reach the Controller. {exception.Message}";
            SetStatus("Controller unavailable", false, true);
        }
    }

    private async void AuthAction_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        AuthActionButton.IsEnabled = false;
        AuthError.Text = string.Empty;
        try
        {
            _api.ChangeController(AuthControllerUrl.Text);
            _user = _setupRequired
                ? await _api.SetupAsync(UsernameBox.Text.Trim(), PasswordBox.Password)
                : await _api.LoginAsync(UsernameBox.Text.Trim(), PasswordBox.Password);
            PasswordBox.Clear();
            _settings.ControllerUrl = _api.BaseAddress.AbsoluteUri;
            _settings.Save();
            await EnterShellAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            AuthError.Text = exception is ControllerApiException { StatusCode: HttpStatusCode.Unauthorized }
                ? "The username or password is incorrect."
                : exception.Message;
        }
        finally
        {
            AuthActionButton.IsEnabled = true;
        }
    }

    private async Task EnterShellAsync()
    {
        if (_api is null || _user is null) return;
        LoginGrid.Visibility = Visibility.Collapsed;
        ShellGrid.Visibility = Visibility.Visible;
        SignedInName.Text = _user.Username;
        SignedInRole.Text = _user.Role;
        AddDeviceButton.IsEnabled = HasAdminPermission();
        VerifyAuditButton.IsEnabled = HasAdminPermission();
        FilesNav.IsEnabled = _user.Role is not "Viewer";
        SettingsControllerUrl.Text = _api.BaseAddress.AbsoluteUri;
        ConfigureTimer();
        try
        {
            var info = await _api.GetControllerInfoAsync();
            CertificateFingerprintBox.Text = info.CertificateSha256 ?? "HTTPS listener is disabled";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            CertificateFingerprintBox.Text = exception.Message;
        }
        NavigateTo("DashboardView");
        await RefreshFleetAsync();
    }

    private async Task RefreshFleetAsync()
    {
        if (_api is null || _user is null || _refreshing) return;
        _refreshing = true;
        try
        {
            SetStatus("Refreshing fleet…");
            _devices = [.. await _api.GetDevicesAsync()];
            var rows = _devices.Select(device => new DeviceRow(device)).ToList();
            DashboardDevicesGrid.ItemsSource = rows;
            ApplyDeviceFilter();
            RefreshDeviceSelectors(rows);

            var online = _devices.Where(device => device.Online).ToList();
            OnlineMetric.Text = online.Count.ToString(CultureInfo.CurrentCulture);
            OnlineCaption.Text = $"of {_devices.Count} enrolled";
            CpuMetric.Text = online.Count == 0 ? "0%" : $"{online.Average(device => device.Telemetry?.CpuPercent ?? 0):0}%";
            MemoryMetric.Text = DeviceRow.FormatBytes(online.Sum(device => device.Telemetry?.UsedMemoryBytes ?? 0));
            AttentionMetric.Text = _devices.Count(device => device.HealthScore < 75).ToString(CultureInfo.CurrentCulture);
            LastRefreshText.Text = $"Updated {DateTime.Now:t}";
            SetStatus("Connected", true);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus($"Refresh failed: {exception.Message}", false, true);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshDeviceSelectors(IReadOnlyList<DeviceRow> rows)
    {
        var diagnosticsId = (DiagnosticsDeviceBox.SelectedItem as DeviceRow)?.Id;
        var filesId = (FilesDeviceBox.SelectedItem as DeviceRow)?.Id;
        var adminId = (AdminDeviceBox.SelectedItem as DeviceRow)?.Id;
        var updateId = (UpdateDeviceBox.SelectedItem as DeviceRow)?.Id;
        var backupId = (BackupDeviceBox.SelectedItem as DeviceRow)?.Id;
        var computeId = (ComputeDeviceBox.SelectedItem as DeviceRow)?.Id;
        var onlineRows = rows.Where(row => row.Source.Online).ToList();
        DiagnosticsDeviceBox.ItemsSource = onlineRows;
        FilesDeviceBox.ItemsSource = onlineRows;
        AdminDeviceBox.ItemsSource = onlineRows;
        UpdateDeviceBox.ItemsSource = onlineRows;
        BackupDeviceBox.ItemsSource = onlineRows;
        ComputeDeviceBox.ItemsSource = onlineRows;
        DiagnosticsDeviceBox.SelectedItem = onlineRows.FirstOrDefault(row => row.Id == diagnosticsId) ?? onlineRows.FirstOrDefault();
        FilesDeviceBox.SelectedItem = onlineRows.FirstOrDefault(row => row.Id == filesId) ?? onlineRows.FirstOrDefault();
        AdminDeviceBox.SelectedItem = onlineRows.FirstOrDefault(row => row.Id == adminId) ?? onlineRows.FirstOrDefault();
        UpdateDeviceBox.SelectedItem = onlineRows.FirstOrDefault(row => row.Id == updateId) ?? onlineRows.FirstOrDefault();
        BackupDeviceBox.SelectedItem = onlineRows.FirstOrDefault(row => row.Id == backupId) ?? onlineRows.FirstOrDefault();
        ComputeDeviceBox.SelectedItem = onlineRows.FirstOrDefault(row => row.Id == computeId) ?? onlineRows.FirstOrDefault();
    }

    private void ApplyDeviceFilter()
    {
        IEnumerable<DeviceSummary> filtered = _devices;
        var query = DeviceSearchBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(device =>
                device.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                device.Inventory.OperatingSystem.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                device.Inventory.AgentVersion.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var status = (DeviceStatusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString();
        filtered = status switch
        {
            "Online" => filtered.Where(device => device.Online),
            "Offline" => filtered.Where(device => !device.Online),
            "Needs attention" => filtered.Where(device => device.HealthScore < 75),
            _ => filtered
        };
        DevicesGrid.ItemsSource = filtered.Select(device => new DeviceRow(device)).ToList();
    }

    private void DeviceFilter_Changed(object sender, EventArgs e)
    {
        if (DevicesGrid is not null) ApplyDeviceFilter();
    }

    private void DeviceGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_api is null || sender is not DataGrid grid || grid.SelectedItem is not DeviceRow row) return;
        new DeviceDetailsWindow(_api, row.Source, HasPowerPermission()) { Owner = this }.ShowDialog();
    }

    private async void Diagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || DiagnosticsDeviceBox.SelectedItem is not DeviceRow device || sender is not Button { Tag: string tag } ||
            !Enum.TryParse<OperationKind>(tag, out var kind))
        {
            SetStatus("Choose an online device first.", false, true);
            return;
        }

        await RunBusyAsync(sender as Button, async () =>
        {
            SetStatus($"Requesting {GetDiagnosticTitle(kind).ToLowerInvariant()}…");
            var queued = await _api.CreateOperationAsync(device.Id, kind, null, true);
            var completed = await _api.WaitForOperationAsync(queued.Id, TimeSpan.FromSeconds(25));
            if (completed.State != OperationState.Succeeded)
                throw new InvalidOperationException(completed.Error ?? $"Diagnostic ended with {completed.State}.");
            _diagnosticTable = CreateDiagnosticTable(completed.ResultJson);
            DiagnosticsGrid.ItemsSource = _diagnosticTable.DefaultView;
            DiagnosticSearchBox.Clear();
            DiagnosticCaption.Text = $"{GetDiagnosticTitle(kind)} on {device.Name} · {_diagnosticTable.Rows.Count} results";
            SetStatus("Diagnostic complete", true);
        });
    }

    private void DiagnosticSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_diagnosticTable is null) return;
        var query = EscapeDataViewLike(DiagnosticSearchBox.Text.Trim());
        _diagnosticTable.DefaultView.RowFilter = string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : string.Join(" OR ", _diagnosticTable.Columns.Cast<DataColumn>()
                .Select(column => $"Convert([{column.ColumnName.Replace("]", "]]", StringComparison.Ordinal)}], 'System.String') LIKE '%{query}%'"));
    }

    private async void FilesRefresh_Click(object sender, RoutedEventArgs e) => await LoadFilesAsync();

    private async void FilesDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        _currentPath = string.Empty;
        await LoadFilesAsync();
        await RefreshTransfersAsync();
    }

    private async void FilesUp_Click(object sender, RoutedEventArgs e)
    {
        _currentPath = Path.GetDirectoryName(_currentPath) ?? string.Empty;
        await LoadFilesAsync();
    }

    private async void FilesGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is not FileRow row || _api is null || FilesDeviceBox.SelectedItem is not DeviceRow device) return;
        if (row.Source.IsDirectory)
        {
            _currentPath = row.Source.RelativePath;
            await LoadFilesAsync();
            return;
        }
        new FileEditorWindow(_api, device.Id, row.Source.RelativePath, row.Source.Name) { Owner = this }.ShowDialog();
        await LoadFilesAsync();
    }

    private async Task LoadFilesAsync()
    {
        if (_api is null || FilesDeviceBox.SelectedItem is not DeviceRow device)
        {
            SetStatus("Choose an online device first.", false, true);
            return;
        }

        try
        {
            SetStatus("Loading managed folder…");
            var queued = await _api.CreateOperationAsync(device.Id, OperationKind.ListDirectory, _currentPath, true);
            var completed = await _api.WaitForOperationAsync(queued.Id, TimeSpan.FromSeconds(25));
            if (completed.State != OperationState.Succeeded)
                throw new InvalidOperationException(completed.Error ?? $"File request ended with {completed.State}.");
            _files = [.. JsonSerializer.Deserialize<FileEntry[]>(completed.ResultJson ?? "[]", JsonOptions) ?? []];
            ApplyFileFilter();
            CurrentPathBox.Text = string.IsNullOrWhiteSpace(_currentPath) ? "Managed root" : _currentPath;
            SetStatus($"Loaded {_files.Count} entries", true);
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException or JsonException)
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private void FilesSearch_Changed(object sender, TextChangedEventArgs e) => ApplyFileFilter();

    private void ApplyFileFilter()
    {
        if (FilesGrid is null) return;
        var query = FilesSearchBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _files
            : _files.Where(file => file.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        FilesGrid.ItemsSource = filtered
            .OrderByDescending(file => file.IsDirectory)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => new FileRow(file))
            .ToList();
    }

    private async Task RefreshTransfersAsync()
    {
        if (_api is null || FilesDeviceBox.SelectedItem is not DeviceRow device) return;
        try
        {
            var transfers = await _api.GetTransfersAsync(device.Id, activeOnly: false);
            var rows = transfers.Select(transfer => new TransferRow(transfer)).ToList();
            TransfersGrid.ItemsSource = rows;
            var active = transfers.Count(transfer => transfer.State is TransferState.Pending or TransferState.Active);
            TransferCaption.Text = transfers.Length == 0
                ? "No recent transfers"
                : $"{transfers.Length} recent · {active} active";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            TransferCaption.Text = exception.Message;
        }
    }

    private async void FilesReloadTransfers_Click(object sender, RoutedEventArgs e) => await RefreshTransfersAsync();

    private async void FilesCancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || TransfersGrid.SelectedItem is not TransferRow row) return;
        if (row.Source.State is not (TransferState.Pending or TransferState.Active))
        {
            SetStatus("Only pending or active transfers can be cancelled.", false, true);
            return;
        }
        await _api.CancelTransferAsync(row.Id);
        await RefreshTransfersAsync();
        SetStatus($"Cancelled {row.Name}", true);
    }

    private async void FilesNewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Show(this, "New folder", "Folder name inside the current path:");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunFileOperationAsync(OperationKind.CreateDirectory, Combine(_currentPath, name));
    }

    private async void FilesNewFile_Click(object sender, RoutedEventArgs e)
    {
        var name = TextPromptWindow.Show(this, "New file", "File name inside the current path:");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunFileOperationAsync(OperationKind.CreateFile, Combine(_currentPath, name));
    }

    private async void FilesRename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFile is not { } file) return;
        var name = TextPromptWindow.Show(this, "Rename", "New name:", file.Name);
        if (string.IsNullOrWhiteSpace(name) || name == file.Name) return;
        await RunFileOperationAsync(OperationKind.RenameFile, new TwoPathArgument(file.RelativePath, Combine(ParentPath(file), name)));
    }

    private async void FilesMove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFile is not { } file) return;
        var destination = TextPromptWindow.Show(this, "Move", "Destination relative path:", Combine(_currentPath, file.Name));
        if (string.IsNullOrWhiteSpace(destination)) return;
        await RunFileOperationAsync(OperationKind.MoveFile, new TwoPathArgument(file.RelativePath, destination));
    }

    private async void FilesCopy_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFile is not { } file) return;
        var destination = TextPromptWindow.Show(this, "Copy", "Destination relative path:", Combine(_currentPath, file.Name + ".copy"));
        if (string.IsNullOrWhiteSpace(destination)) return;
        await RunFileOperationAsync(OperationKind.CopyFile, new TwoPathArgument(file.RelativePath, destination));
    }

    private async void FilesDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFile is not { } file) return;
        if (MessageBox.Show(this, $"Delete {file.Name}? This cannot be undone.", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunFileOperationAsync(OperationKind.DeleteFile, file.RelativePath);
    }

    private async void FilesSize_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFile is not { IsDirectory: true } folder) return;
        var result = await RunFileOperationResultAsync(OperationKind.DirectorySize, folder.RelativePath, refresh: false);
        if (result is null) return;
        var size = JsonSerializer.Deserialize<DirectorySizeResult>(result.ResultJson ?? "{}", JsonOptions);
        MessageBox.Show(this, $"{folder.Name}\n{DeviceRow.FormatBytes(size?.TotalBytes ?? 0)}\n{size?.FileCount ?? 0} files · {size?.DirectoryCount ?? 0} folders", "Folder size", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void FilesZip_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFile is not { } file) return;
        var archiveName = TextPromptWindow.Show(this, "Create archive", "Archive name:", file.Name.TrimEnd('/') + ".zip");
        if (string.IsNullOrWhiteSpace(archiveName)) return;
        await RunFileOperationAsync(OperationKind.CreateArchive, new TwoPathArgument(file.RelativePath, Combine(_currentPath, archiveName)));
    }

    private async void FilesExtract_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFile is not { IsDirectory: false } file || !file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return;
        var destination = TextPromptWindow.Show(this, "Extract archive", "Destination folder:", Combine(_currentPath, Path.GetFileNameWithoutExtension(file.Name)));
        if (string.IsNullOrWhiteSpace(destination)) return;
        await RunFileOperationAsync(OperationKind.ExtractArchive, new TwoPathArgument(file.RelativePath, destination));
    }

    private async void FilesUpload_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || FilesDeviceBox.SelectedItem is not DeviceRow device) return;
        var dialog = new OpenFileDialog { Title = "Upload to managed node", Filter = "All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var progress = new Progress<double>(value => SetStatus($"Uploading {Path.GetFileName(dialog.FileName)} · {value:P0}"));
        try
        {
            var remotePath = Combine(_currentPath, Path.GetFileName(dialog.FileName));
            var transfer = await _api.UploadFileAsync(device.Id, dialog.FileName, remotePath, progress, CancellationToken.None);
            SetStatus($"Upload {transfer.State}: {remotePath}", transfer.State == TransferState.Completed);
            await RefreshTransfersAsync();
            await LoadFilesAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void FilesDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || SelectedFile is not { IsDirectory: false } file || FilesDeviceBox.SelectedItem is not DeviceRow device) return;
        var dialog = new SaveFileDialog { Title = "Download from managed node", FileName = file.Name, Filter = "All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        var progress = new Progress<double>(value => SetStatus($"Downloading {file.Name} · {value:P0}"));
        try
        {
            var request = new TransferRequest(TransferDirection.Download, file.RelativePath, 0, null);
            var transfer = await _api.CreateTransferAsync(device.Id, request);
            await _api.DownloadFileAsync(transfer, dialog.FileName, progress, CancellationToken.None);
            SetStatus($"Downloaded {file.Name}", true);
            await RefreshTransfersAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void FilesEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || SelectedFile is not { IsDirectory: false } file || FilesDeviceBox.SelectedItem is not DeviceRow device) return;
        new FileEditorWindow(_api, device.Id, file.RelativePath, file.Name) { Owner = this }.ShowDialog();
        await LoadFilesAsync();
    }

    private async void FilesHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || SelectedFile is not { IsDirectory: false } file || FilesDeviceBox.SelectedItem is not DeviceRow device) return;
        new FileEditorWindow(_api, device.Id, file.RelativePath, file.Name, showHistory: true) { Owner = this }.ShowDialog();
        await LoadFilesAsync();
    }

    private FileEntry? SelectedFile => (FilesGrid.SelectedItem as FileRow)?.Source;

    private async void BackupDevice_Changed(object sender, SelectionChangedEventArgs e) => await LoadBackupsAsync();

    private async void BackupRefresh_Click(object sender, RoutedEventArgs e) => await LoadBackupsAsync();

    private async Task LoadBackupsAsync()
    {
        if (_api is null || BackupDeviceBox.SelectedItem is not DeviceRow device)
        {
            BackupsGrid.ItemsSource = null;
            return;
        }
        try
        {
            var backups = await _api.GetBackupsAsync(device.Id);
            BackupsGrid.ItemsSource = backups.Select(backup => new BackupRowView(backup)).ToList();
            BackupCaption.Text = $"{backups.Length} backup record(s)";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            BackupCaption.Text = exception.Message;
        }
    }

    private async void BackupCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || BackupDeviceBox.SelectedItem is not DeviceRow device) return;
        if (string.IsNullOrWhiteSpace(BackupNameBox.Text) || string.IsNullOrWhiteSpace(BackupSourceBox.Text) || string.IsNullOrWhiteSpace(BackupDestinationBox.Text))
        {
            SetStatus("A name, source path, and destination path are required.", false, true);
            return;
        }
        try
        {
            SetStatus("Creating backup…");
            var response = await _api.CreateBackupAsync(device.Id, new BackupRequest(
                BackupNameBox.Text.Trim(), BackupSourceBox.Text.Trim(), BackupDestinationBox.Text.Trim(), 30));
            var operationId = response.GetProperty("operation").GetProperty("id").GetGuid();
            var completed = await _api.WaitForOperationAsync(operationId, TimeSpan.FromMinutes(30));
            if (completed.State != OperationState.Succeeded) throw new InvalidOperationException(completed.Error ?? $"The backup ended with {completed.State}.");
            SetStatus("Backup completed", true);
            await LoadBackupsAsync();
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException or JsonException)
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void BackupVerify_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || BackupsGrid.SelectedItem is not BackupRowView row) return;
        try
        {
            var operation = await _api.VerifyBackupAsync(row.Id);
            var completed = await _api.WaitForOperationAsync(operation.Id, TimeSpan.FromMinutes(10));
            if (completed.State != OperationState.Succeeded) throw new InvalidOperationException(completed.Error ?? $"Verification ended with {completed.State}.");
            var result = JsonSerializer.Deserialize<VerifyOperationResult>(completed.ResultJson ?? "{}", JsonOptions);
            SetStatus(result?.Valid == true ? "Backup verified" : "Backup verification failed", result?.Valid == true);
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException or JsonException)
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void BackupRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || BackupsGrid.SelectedItem is not BackupRowView row) return;
        var destination = TextPromptWindow.Show(this, "Restore backup", "Restore destination relative to the managed root:", "restored");
        if (string.IsNullOrWhiteSpace(destination)) return;
        try
        {
            var operation = await _api.RestoreBackupAsync(row.Id, destination.Trim());
            var completed = await _api.WaitForOperationAsync(operation.Id, TimeSpan.FromMinutes(30));
            if (completed.State != OperationState.Succeeded) throw new InvalidOperationException(completed.Error ?? $"Restore ended with {completed.State}.");
            SetStatus("Backup restored", true);
            await LoadBackupsAsync();
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException)
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void AlertsRefresh_Click(object sender, RoutedEventArgs e) => await LoadAlertsAsync();

    private async Task LoadAlertsAsync()
    {
        if (_api is null) return;
        try
        {
            var state = (AlertStateBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            var alerts = await _api.GetAlertsAsync(state == "All states" ? null : state);
            AlertsGrid.ItemsSource = alerts.Select(alert => new AlertRowView(alert)).ToList();
            AlertCaption.Text = $"{alerts.Length} alert(s)";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            AlertCaption.Text = exception.Message;
        }
    }

    private async void AlertAcknowledge_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || AlertsGrid.SelectedItem is not AlertRowView row) return;
        try
        {
            await _api.AcknowledgeAlertAsync(row.Id);
            SetStatus("Alert acknowledged", true);
            await LoadAlertsAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void AutomationRefresh_Click(object sender, RoutedEventArgs e) => await LoadAutomationsAsync();

    private async Task LoadAutomationsAsync()
    {
        if (_api is null) return;
        try
        {
            var automations = await _api.GetAutomationsAsync();
            AutomationsGrid.ItemsSource = automations.Select(automation => new AutomationRowView(automation)).ToList();
            var runs = await _api.GetAutomationRunsAsync();
            AutomationRunsGrid.ItemsSource = runs.Select(run => new AutomationRunRowView(run)).ToList();
            AutomationCaption.Text = $"{automations.Length} rule(s)";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            AutomationCaption.Text = exception.Message;
        }
    }

    private async void AutomationCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        var window = new AutomationWindow { Owner = this };
        if (window.ShowDialog() != true || window.Result is null) return;
        try
        {
            await _api.CreateAutomationAsync(window.Result);
            SetStatus("Automation created", true);
            await LoadAutomationsAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void AutomationEnable_Click(object sender, RoutedEventArgs e) => await SetAutomationEnabledAsync(true);

    private async void AutomationDisable_Click(object sender, RoutedEventArgs e) => await SetAutomationEnabledAsync(false);

    private async Task SetAutomationEnabledAsync(bool enabled)
    {
        if (_api is null || AutomationsGrid.SelectedItem is not AutomationRowView row) return;
        try
        {
            await _api.SetAutomationEnabledAsync(row.Id, enabled);
            await LoadAutomationsAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void AutomationDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || AutomationsGrid.SelectedItem is not AutomationRowView row) return;
        if (MessageBox.Show(this, $"Delete automation '{row.Name}'?", "Delete automation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _api.DeleteAutomationAsync(row.Id);
            await LoadAutomationsAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private void Automation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Run history is grouped globally; selection is retained for enable/disable/delete actions.
    }

    private async void ComputeDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        await LoadComputePolicyAsync();
        await LoadComputeJobsAsync();
    }

    private async void ComputeRefresh_Click(object sender, RoutedEventArgs e) => await LoadComputeJobsAsync();

    private async Task LoadComputePolicyAsync()
    {
        if (_api is null || ComputeDeviceBox.SelectedItem is not DeviceRow device) return;
        try
        {
            var policy = await _api.GetComputePolicyAsync(device.Id);
            ComputeModeBox.SelectedIndex = Math.Max(0, Array.IndexOf(ComputeModes.All, policy.Mode));
            ReservedCpuBox.Text = policy.ReservedCpuThreads.ToString(CultureInfo.InvariantCulture);
            ReservedMemoryBox.Text = (policy.ReservedMemoryBytes / (1024 * 1024 * 1024)).ToString(CultureInfo.InvariantCulture);
            ReservedDiskBox.Text = (policy.ReservedDiskBytes / (1024 * 1024 * 1024)).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ComputeCaption.Text = exception.Message;
        }
    }

    private async void ComputeSavePolicy_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || ComputeDeviceBox.SelectedItem is not DeviceRow device) return;
        if (!int.TryParse(ReservedCpuBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cpu) ||
            !long.TryParse(ReservedMemoryBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var memoryGb) ||
            !long.TryParse(ReservedDiskBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var diskGb))
        {
            SetStatus("Reservations must be whole numbers.", false, true);
            return;
        }
        var mode = (ComputeModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Idle";
        try
        {
            await _api.SetComputePolicyAsync(device.Id, new ComputePolicyRequest(mode, cpu, memoryGb * 1024 * 1024 * 1024, diskGb * 1024 * 1024 * 1024));
            SetStatus($"Compute policy saved for {device.Name}", true);
            await LoadComputeJobsAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void ComputeNewJob_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        var device = ComputeDeviceBox.SelectedItem as DeviceRow;
        var window = new ComputeJobWindow(device?.Id, device?.Name ?? "the selected node") { Owner = this };
        if (window.ShowDialog() != true || window.Result is null) return;
        try
        {
            await _api.CreateComputeJobAsync(window.Result);
            SetStatus("Compute job queued", true);
            await LoadComputeJobsAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void ComputeCancelJob_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || ComputeJobsGrid.SelectedItem is not ComputeJobRowView row) return;
        if (row.State is not ("Queued" or "Running"))
        {
            SetStatus("Only queued or running jobs can be cancelled.", false, true);
            return;
        }
        if (MessageBox.Show(this, $"Cancel job '{row.Name}'?", "Cancel compute job", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _api.CancelComputeJobAsync(row.Id);
            SetStatus("Compute job cancelled", true);
            await LoadComputeJobsAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async Task LoadComputeJobsAsync()
    {
        if (_api is null) return;
        try
        {
            var jobs = await _api.GetComputeJobsAsync();
            ComputeJobsGrid.ItemsSource = jobs.Select(job => new ComputeJobRowView(job)).ToList();
            ComputeCaption.Text = $"{jobs.Length} job(s)";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ComputeCaption.Text = exception.Message;
        }
    }

    private async void GameServerRefresh_Click(object sender, RoutedEventArgs e) => await LoadGameServersAsync();

    private async Task LoadGameServersAsync()
    {
        if (_api is null) return;
        try
        {
            var servers = await _api.GetGameServersAsync();
            GameServersGrid.ItemsSource = servers.Select(server => new GameServerRowView(server)).ToList();
            GameServerCaption.Text = $"{servers.Length} game server(s)";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            GameServerCaption.Text = exception.Message;
        }
    }

    private async void GameServerCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || ComputeDeviceBox.SelectedItem is not DeviceRow device)
        {
            SetStatus("Select a device in the Compute tab first.", false, true);
            return;
        }
        var window = new GameServerWindow { Owner = this };
        if (window.ShowDialog() != true || window.Result is null) return;
        try
        {
            var response = await _api.CreateGameServerAsync(device.Id, window.Result);
            var operationId = response.GetProperty("operation").GetProperty("id").GetGuid();
            var completed = await _api.WaitForOperationAsync(operationId, TimeSpan.FromMinutes(20));
            if (completed.State != OperationState.Succeeded) throw new InvalidOperationException(completed.Error ?? $"Provisioning ended with {completed.State}.");
            SetStatus("Game server provisioned", true);
            await LoadGameServersAsync();
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException or JsonException)
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void GameServerStart_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || GameServersGrid.SelectedItem is not GameServerRowView row) return;
        try
        {
            var operation = await _api.StartGameServerAsync(row.Id);
            var completed = await _api.WaitForOperationAsync(operation.Id, TimeSpan.FromMinutes(3));
            if (completed.State != OperationState.Succeeded) throw new InvalidOperationException(completed.Error ?? $"Start ended with {completed.State}.");
            SetStatus($"{row.Name} started", true);
            await LoadGameServersAsync();
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException)
        {
            SetStatus(exception.Message, false, true);
            await LoadGameServersAsync();
        }
    }

    private async void GameServerStop_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || GameServersGrid.SelectedItem is not GameServerRowView row) return;
        if (MessageBox.Show(this, $"Stop game server '{row.Name}'?", "Stop server", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var operation = await _api.StopGameServerAsync(row.Id);
            var completed = await _api.WaitForOperationAsync(operation.Id, TimeSpan.FromMinutes(3));
            if (completed.State != OperationState.Succeeded) throw new InvalidOperationException(completed.Error ?? $"Stop ended with {completed.State}.");
            SetStatus($"{row.Name} stopped", true);
            await LoadGameServersAsync();
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException)
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void GameServerUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || GameServersGrid.SelectedItem is not GameServerRowView row) return;
        try
        {
            var operation = await _api.UpdateGameServerAsync(row.Id);
            var completed = await _api.WaitForOperationAsync(operation.Id, TimeSpan.FromMinutes(20));
            if (completed.State != OperationState.Succeeded) throw new InvalidOperationException(completed.Error ?? $"Update ended with {completed.State}.");
            SetStatus($"{row.Name} updated", true);
            await LoadGameServersAsync();
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException)
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private void GameServerConsole_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || GameServersGrid.SelectedItem is not GameServerRowView row) return;
        new GameServerConsoleWindow(_api, row.Source) { Owner = this }.ShowDialog();
        _ = LoadGameServersAsync();
    }

    private async void IntegrationRefresh_Click(object sender, RoutedEventArgs e) => await LoadIntegrationsAsync();

    private async Task LoadIntegrationsAsync()
    {
        if (_api is null) return;
        try
        {
            var integrations = await _api.GetIntegrationsAsync();
            IntegrationsGrid.ItemsSource = integrations.Select(integration => new IntegrationRowView(integration)).ToList();
            IntegrationCaption.Text = $"{integrations.Length} integration(s)";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            IntegrationCaption.Text = exception.Message;
        }
    }

    private async void IntegrationCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        var window = new IntegrationWindow { Owner = this };
        if (window.ShowDialog() != true || window.Result is null) return;
        try
        {
            await _api.CreateIntegrationAsync(window.Result);
            SetStatus("Integration added", true);
            await LoadIntegrationsAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void IntegrationHealth_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || IntegrationsGrid.SelectedItem is not IntegrationRowView row) return;
        try
        {
            IntegrationCaption.Text = "Checking health…";
            var result = await _api.CheckIntegrationHealthAsync(row.Id);
            var state = result.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;
            var detail = result.TryGetProperty("detail", out var detailElement) ? detailElement.GetString() : null;
            IntegrationCaption.Text = $"{state}: {detail}";
            await LoadIntegrationsAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            IntegrationCaption.Text = exception.Message;
        }
    }

    private void IntegrationResources_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || IntegrationsGrid.SelectedItem is not IntegrationRowView row) return;
        new IntegrationResourcesWindow(_api, row.Source) { Owner = this }.ShowDialog();
    }

    private async void IntegrationDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || IntegrationsGrid.SelectedItem is not IntegrationRowView row) return;
        if (MessageBox.Show(this, $"Delete integration '{row.Name}'? Stored credentials are removed with it.", "Delete integration", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _api.DeleteIntegrationAsync(row.Id);
            await LoadIntegrationsAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void SoftwareRefresh_Click(object sender, RoutedEventArgs e) => await LoadSoftwareAsync();

    private async Task LoadSoftwareAsync()
    {
        if (_api is null) return;
        try
        {
            var packages = await _api.GetSoftwarePackagesAsync();
            PackagesGrid.ItemsSource = packages.Select(package => new SoftwarePackageRow(package)).ToList();
            var deployments = await _api.GetSoftwareDeploymentsAsync();
            DeploymentsGrid.ItemsSource = deployments.Select(deployment => new DeploymentRow(deployment)).ToList();
            SoftwareCaption.Text = $"{packages.Length} packages · {deployments.Length} deployments";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SoftwareCaption.Text = exception.Message;
        }
    }

    private async void SoftwareRegister_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        var window = new SoftwarePackageWindow { Owner = this };
        if (window.ShowDialog() != true || window.Result is null) return;
        try
        {
            await _api.CreateSoftwarePackageAsync(window.Result);
            SetStatus($"Registered {window.Result.Name}", true);
            await LoadSoftwareAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void SoftwareDeploy_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || PackagesGrid.SelectedItem is not SoftwarePackageRow row) return;
        var devices = _devices.Where(device => device.Online).Select(device => new DeviceRow(device)).ToList();
        if (devices.Count == 0)
        {
            SetStatus("No online devices are available for deployment.", false, true);
            return;
        }
        var window = new DeploymentWindow(row.Source, devices) { Owner = this };
        if (window.ShowDialog() != true) return;
        if (MessageBox.Show(this, $"Deploy {window.SelectedAction} of {row.Name} to {window.SelectedDeviceIds.Length} device(s)?", "Confirm deployment", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _api.CreateSoftwareDeploymentAsync(new SoftwareDeploymentRequest(row.Id, window.SelectedAction, window.SelectedDeviceIds, true));
            SetStatus("Deployment queued", true);
            await LoadSoftwareAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void SoftwareCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || DeploymentsGrid.SelectedItem is not DeploymentRow row) return;
        if (MessageBox.Show(this, $"Cancel deployment of {row.Package}?", "Cancel deployment", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _api.CancelSoftwareDeploymentAsync(row.Id);
            SetStatus("Deployment cancelled", true);
            await LoadSoftwareAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void Deployments_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_api is null || DeploymentsGrid.SelectedItem is not DeploymentRow row) return;
        try
        {
            var deployment = await _api.GetSoftwareDeploymentAsync(row.Id);
            DeploymentTargetsGrid.ItemsSource = deployment.Targets.Select(target => new DeploymentTargetRow(target)).ToList();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void UpdateDevice_Changed(object sender, SelectionChangedEventArgs e) => await LoadUpdateScanAsync();

    private async void UpdateRefresh_Click(object sender, RoutedEventArgs e) => await LoadUpdateScanAsync();

    private async Task LoadUpdateScanAsync()
    {
        if (_api is null || UpdateDeviceBox.SelectedItem is not DeviceRow device)
        {
            UpdatesGrid.ItemsSource = null;
            return;
        }
        try
        {
            var scan = await _api.GetWindowsUpdateScanAsync(device.Id);
            if (scan is null)
            {
                _updates = [];
                UpdatesGrid.ItemsSource = null;
                UpdateCaption.Text = "No scan has been recorded for this device yet.";
                return;
            }
            _updates = scan.Updates.Select(update => new WindowsUpdateRow(update)).ToList();
            UpdatesGrid.ItemsSource = _updates;
            UpdateCaption.Text = $"Last scanned {scan.ScannedAt.LocalDateTime:g} · {_updates.Count} available update(s).";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            UpdateCaption.Text = exception.Message;
        }
    }

    private async void UpdateScan_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || UpdateDeviceBox.SelectedItem is not DeviceRow device) return;
        try
        {
            UpdateCaption.Text = "Scanning for updates…";
            var queued = await _api.ScanWindowsUpdatesAsync(device.Id);
            var completed = await _api.WaitForOperationAsync(queued.Id, TimeSpan.FromMinutes(6));
            if (completed.State != OperationState.Succeeded) throw new InvalidOperationException(completed.Error ?? $"The scan ended with {completed.State}.");
            await LoadUpdateScanAsync();
            SetStatus("Windows Update scan complete", true);
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException)
        {
            UpdateCaption.Text = exception.Message;
            SetStatus(exception.Message, false, true);
        }
    }

    private async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || UpdateDeviceBox.SelectedItem is not DeviceRow device) return;
        var selected = _updates.Where(update => update.Selected).Select(update => update.Source.UpdateId).ToArray();
        if (selected.Length == 0)
        {
            SetStatus("Select at least one update to install.", false, true);
            return;
        }
        if (MessageBox.Show(this, $"Install {selected.Length} update(s) on {device.Name}? Windows may require a restart.", "Install updates", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            UpdateCaption.Text = "Installing updates…";
            var queued = await _api.InstallWindowsUpdatesAsync(device.Id, selected);
            var completed = await _api.WaitForOperationAsync(queued.Id, TimeSpan.FromMinutes(125));
            if (completed.State != OperationState.Succeeded) throw new InvalidOperationException(completed.Error ?? $"The installation ended with {completed.State}.");
            var result = JsonSerializer.Deserialize<WindowsUpdateInstallResult>(completed.ResultJson ?? "{}", JsonOptions);
            UpdateCaption.Text = result is null
                ? "Update installation finished."
                : $"Installed {result.Installed} of {result.Selected} · {(result.RebootRequired ? "restart required" : "no restart required")}";
            SetStatus("Windows Update installation complete", true);
            await LoadUpdateScanAsync();
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException or JsonException)
        {
            UpdateCaption.Text = exception.Message;
            SetStatus(exception.Message, false, true);
        }
    }

    private async void AdminRefresh_Click(object sender, RoutedEventArgs e)
    {
        await AdminLoadProcessesAsync();
        await AdminLoadServicesAsync();
    }

    private async void AdminLoadProcesses_Click(object sender, RoutedEventArgs e) => await AdminLoadProcessesAsync();

    private async Task AdminLoadProcessesAsync()
    {
        if (_api is null || AdminDeviceBox.SelectedItem is not DeviceRow device)
        {
            SetStatus("Choose an online device first.", false, true);
            return;
        }
        try
        {
            var completed = await RunDiagnosticAsync(device, OperationKind.ListProcesses);
            if (completed is null) return;
            var processes = JsonSerializer.Deserialize<ProcessSnapshot[]>(completed.ResultJson ?? "[]", JsonOptions) ?? [];
            AdminProcessesGrid.ItemsSource = processes.Select(process => new ProcessAdminRow(process)).ToList();
            ProcessCaption.Text = $"{processes.Length} processes · protected system processes cannot be stopped";
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException or JsonException)
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void AdminLoadServices_Click(object sender, RoutedEventArgs e) => await AdminLoadServicesAsync();

    private async Task AdminLoadServicesAsync()
    {
        if (_api is null || AdminDeviceBox.SelectedItem is not DeviceRow device) return;
        try
        {
            var completed = await RunDiagnosticAsync(device, OperationKind.ListServices);
            if (completed is null) return;
            var services = JsonSerializer.Deserialize<ServiceSnapshot[]>(completed.ResultJson ?? "[]", JsonOptions) ?? [];
            AdminServicesGrid.ItemsSource = services.Select(service => new ServiceAdminRow(service)).ToList();
            SetStatus($"Loaded {services.Length} services", true);
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException or JsonException)
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async Task<OperationView?> RunDiagnosticAsync(DeviceRow device, OperationKind kind, string? argument = null)
    {
        if (_api is null) return null;
        var queued = await _api.CreateOperationAsync(device.Id, kind, argument, true);
        var completed = await _api.WaitForOperationAsync(queued.Id, TimeSpan.FromSeconds(30));
        if (completed.State != OperationState.Succeeded)
            throw new InvalidOperationException(completed.Error ?? $"The node ended the request with {completed.State}.");
        return completed;
    }

    private async void AdminStopProcess_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || AdminProcessesGrid.SelectedItem is not ProcessAdminRow row || AdminDeviceBox.SelectedItem is not DeviceRow device) return;
        if (MessageBox.Show(this, $"Stop {row.Name} (PID {row.Id})?", "Stop process", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAdminOperationAsync(device, OperationKind.StopProcess, new ProcessTargetArgument(row.Id, row.Name), refreshProcesses: true);
    }

    private async void AdminPriority_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || AdminProcessesGrid.SelectedItem is not ProcessAdminRow row || AdminDeviceBox.SelectedItem is not DeviceRow device) return;
        var priority = TextPromptWindow.Show(this, "Set process priority", $"Priority for {row.Name} ({string.Join(", ", AdminLimits.ProcessPriorities)}):", "Normal");
        if (priority is null) return;
        if (!AdminLimits.ProcessPriorities.Contains(priority, StringComparer.OrdinalIgnoreCase))
        {
            SetStatus("The priority must be one of: " + string.Join(", ", AdminLimits.ProcessPriorities), false, true);
            return;
        }
        await RunAdminOperationAsync(device, OperationKind.SetProcessPriority, new ProcessPriorityArgument(row.Id, row.Name, priority), refreshProcesses: true);
    }

    private async void AdminStartProcess_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || AdminDeviceBox.SelectedItem is not DeviceRow device) return;
        var fileName = TextPromptWindow.Show(this, "Start program", "Absolute path to an .exe on the node:");
        if (string.IsNullOrWhiteSpace(fileName)) return;
        var arguments = TextPromptWindow.Show(this, "Start program", "Command-line arguments (optional):");
        await RunAdminOperationAsync(device, OperationKind.StartProcess, new StartProcessArgument(fileName.Trim(), string.IsNullOrWhiteSpace(arguments) ? null : arguments, null), refreshProcesses: true);
    }

    private async void AdminServiceStart_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || AdminServicesGrid.SelectedItem is not ServiceAdminRow row || AdminDeviceBox.SelectedItem is not DeviceRow device) return;
        await RunAdminOperationAsync(device, OperationKind.StartService, new ServiceTargetArgument(row.Name), refreshServices: true);
    }

    private async void AdminServiceStop_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || AdminServicesGrid.SelectedItem is not ServiceAdminRow row || AdminDeviceBox.SelectedItem is not DeviceRow device) return;
        if (MessageBox.Show(this, $"Stop service '{row.DisplayName}'?", "Stop service", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAdminOperationAsync(device, OperationKind.StopService, new ServiceTargetArgument(row.Name), refreshServices: true);
    }

    private async void AdminServiceStartup_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || AdminServicesGrid.SelectedItem is not ServiceAdminRow row || AdminDeviceBox.SelectedItem is not DeviceRow device) return;
        var startup = TextPromptWindow.Show(this, "Startup type", $"Startup for {row.Name} ({string.Join(", ", AdminLimits.ServiceStartTypes)}):", row.StartType);
        if (startup is null) return;
        if (!AdminLimits.ServiceStartTypes.Contains(startup, StringComparer.OrdinalIgnoreCase))
        {
            SetStatus("The startup type must be one of: " + string.Join(", ", AdminLimits.ServiceStartTypes), false, true);
            return;
        }
        await RunAdminOperationAsync(device, OperationKind.SetServiceStartType, new ServiceStartTypeArgument(row.Name, startup), refreshServices: true);
    }

    private async Task RunAdminOperationAsync(DeviceRow device, OperationKind kind, object argument, bool refreshProcesses = false, bool refreshServices = false)
    {
        if (_api is null) return;
        try
        {
            SetStatus($"{kind}…");
            var queued = await _api.CreateOperationAsync(device.Id, kind, JsonSerializer.Serialize(argument, JsonOptions), true);
            var completed = await _api.WaitForOperationAsync(queued.Id, TimeSpan.FromSeconds(60));
            if (completed.State != OperationState.Succeeded) throw new InvalidOperationException(completed.Error ?? $"The node ended the request with {completed.State}.");
            SetStatus($"{kind} complete", true);
            if (refreshProcesses) await AdminLoadProcessesAsync();
            if (refreshServices) await AdminLoadServicesAsync();
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException)
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void TerminalStart_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || AdminDeviceBox.SelectedItem is not DeviceRow device) return;
        if (_terminalSessionId is not null)
        {
            SetStatus("Stop the current terminal session first.", false, true);
            return;
        }
        var shell = (TerminalShellBox.SelectedItem as ComboBoxItem)?.Content?.ToString() == "CommandPrompt"
            ? TerminalShell.CommandPrompt
            : TerminalShell.PowerShell;
        try
        {
            TerminalCaption.Text = "Starting session…";
            var response = await _api.StartTerminalAsync(device.Id, new StartTerminalRequest(shell, TerminalWorkingDirectory.Text.Trim()));
            var completed = await _api.WaitForOperationAsync(response.Operation.Id, TimeSpan.FromSeconds(30));
            if (completed.State != OperationState.Succeeded) throw new InvalidOperationException(completed.Error ?? $"The session ended with {completed.State}.");
            var output = JsonSerializer.Deserialize<TerminalOutputResult>(completed.ResultJson ?? "{}", JsonOptions);
            _terminalSessionId = response.Session.Id;
            _terminalSequence = output?.Sequence ?? 0;
            TerminalOutputBox.Text = output?.Output ?? string.Empty;
            TerminalCaption.Text = $"{shell} session active on {device.Name}";
            _terminalTimer.Start();
            TerminalInputBox.Focus();
            SetStatus("Terminal session started", true);
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException or JsonException)
        {
            TerminalCaption.Text = exception.Message;
            SetStatus(exception.Message, false, true);
        }
    }

    private void TerminalSend_Click(object sender, RoutedEventArgs e) => _ = SendTerminalCommandAsync();

    private void TerminalInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = SendTerminalCommandAsync();
        }
    }

    private async Task SendTerminalCommandAsync()
    {
        if (_api is null || _terminalSessionId is null) return;
        var command = TerminalInputBox.Text;
        if (string.IsNullOrWhiteSpace(command)) return;
        TerminalInputBox.Clear();
        try
        {
            var response = await _api.TerminalInputAsync(_terminalSessionId.Value, command + "\r\n");
            var completed = await _api.WaitForOperationAsync(response.Operation.Id, TimeSpan.FromSeconds(30));
            if (completed.State != OperationState.Succeeded) throw new InvalidOperationException(completed.Error ?? $"The command ended with {completed.State}.");
            await PollTerminalAsync();
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException)
        {
            TerminalCaption.Text = exception.Message;
        }
    }

    private async void TerminalStop_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || _terminalSessionId is null) return;
        try
        {
            var response = await _api.StopTerminalAsync(_terminalSessionId.Value);
            await _api.WaitForOperationAsync(response.Operation.Id, TimeSpan.FromSeconds(20));
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
        finally
        {
            _terminalTimer.Stop();
            _terminalSessionId = null;
            TerminalCaption.Text = "Session stopped";
        }
    }

    private void TerminalClear_Click(object sender, RoutedEventArgs e) => TerminalOutputBox.Clear();

    private async void TerminalPollTick(object? sender, EventArgs e)
    {
        if (_terminalPolling) return;
        _terminalPolling = true;
        try
        {
            await PollTerminalAsync();
        }
        finally
        {
            _terminalPolling = false;
        }
    }

    private async Task PollTerminalAsync()
    {
        if (_api is null || _terminalSessionId is null) return;
        try
        {
            var response = await _api.TerminalOutputAsync(_terminalSessionId.Value, _terminalSequence);
            var completed = await _api.WaitForOperationAsync(response.Operation.Id, TimeSpan.FromSeconds(20));
            if (completed.State != OperationState.Succeeded) return;
            var output = JsonSerializer.Deserialize<TerminalOutputResult>(completed.ResultJson ?? "{}", JsonOptions);
            if (output is null) return;
            if (output.Output.Length > 0)
            {
                TerminalOutputBox.AppendText(output.Output);
                TerminalOutputBox.ScrollToEnd();
            }
            _terminalSequence = output.Sequence;
            if (output.Exited)
            {
                _terminalTimer.Stop();
                _terminalSessionId = null;
                TerminalCaption.Text = $"Shell exited with code {output.ExitCode}";
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            TerminalCaption.Text = exception.Message;
        }
    }

    private static string Combine(string folder, string name) =>
        string.IsNullOrWhiteSpace(folder) ? name.Trim() : $"{folder.TrimEnd('/', '\\')}\\{name.Trim()}";

    private static string ParentPath(FileEntry file)
    {
        var index = file.RelativePath.LastIndexOfAny(['\\', '/']);
        return index < 0 ? string.Empty : file.RelativePath[..index];
    }

    private async Task RunFileOperationAsync(OperationKind kind, string argument, bool refresh = true) =>
        await RunFileOperationAsync(kind, (object)argument, refresh);

    private async Task RunFileOperationAsync(OperationKind kind, object argument, bool refresh = true)
    {
        _ = await RunFileOperationResultAsync(kind, argument, refresh);
    }

    private async Task<OperationView?> RunFileOperationResultAsync(OperationKind kind, object argument, bool refresh = true)
    {
        if (_api is null || FilesDeviceBox.SelectedItem is not DeviceRow device) return null;
        try
        {
            SetStatus($"{kind}…");
            var queued = await _api.CreateOperationAsync(device.Id, kind, JsonSerializer.Serialize(argument, JsonOptions), true);
            var completed = await _api.WaitForOperationAsync(queued.Id, TimeSpan.FromSeconds(60));
            if (completed.State != OperationState.Succeeded) throw new InvalidOperationException(completed.Error ?? $"The node ended the request with {completed.State}.");
            SetStatus($"{kind} complete", true);
            if (refresh) await LoadFilesAsync();
            return completed;
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException)
        {
            SetStatus(exception.Message, false, true);
            return null;
        }
    }

    private async void LoadAudit_Click(object sender, RoutedEventArgs e) => await LoadAuditAsync();

    private async Task LoadAuditAsync()
    {
        if (_api is null) return;
        try
        {
            SetStatus("Loading audit history…");
            _auditEvents = [.. await _api.GetAuditAsync()];
            ApplyAuditFilter();
            SetStatus($"Loaded {_auditEvents.Count} audit events", true);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private async void VerifyAudit_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        await RunBusyAsync(sender as Button, async () =>
        {
            var integrity = await _api.VerifyAuditAsync();
            AuditIntegrityText.Text = integrity.Valid ? "Integrity verified" : "Integrity check failed";
            AuditIntegrityText.Foreground = (Brush)FindResource(integrity.Valid ? "SuccessBrush" : "DangerBrush");
            SetStatus(AuditIntegrityText.Text, integrity.Valid, !integrity.Valid);
        });
    }

    private void AuditSearch_Changed(object sender, TextChangedEventArgs e) => ApplyAuditFilter();

    private void ApplyAuditFilter()
    {
        if (AuditGrid is null) return;
        var query = AuditSearchBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(query) ? _auditEvents : _auditEvents.Where(item =>
            item.Actor.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.Action.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.Target.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.Outcome.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (item.Metadata?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        AuditGrid.ItemsSource = filtered.Select(item => new AuditRow(item)).ToList();
    }

    private void ExportAudit_Click(object sender, RoutedEventArgs e)
    {
        if (_auditEvents.Count == 0)
        {
            SetStatus("Load the audit history before exporting.", false, true);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Veltrix-Control audit history",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"veltrix-control-audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;
        var csv = new StringBuilder("Timestamp,Actor,Action,Target,Outcome,Details\r\n");
        foreach (var item in _auditEvents)
            csv.AppendLine(string.Join(',', new[] { item.Timestamp.ToString("O"), item.Actor, item.Action, item.Target, item.Outcome, item.Metadata ?? string.Empty }.Select(Csv)));
        try
        {
            File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
            SetStatus($"Exported {Path.GetFileName(dialog.FileName)}", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not export the audit history: {exception.Message}", false, true);
        }
    }

    private void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null || !HasAdminPermission()) return;
        new EnrollmentWindow(_api) { Owner = this }.ShowDialog();
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        try
        {
            var uri = ControllerApiClient.ValidateControllerUri(SettingsControllerUrl.Text);
            _settings.ControllerUrl = uri.AbsoluteUri;
            _settings.RefreshSeconds = GetRefreshSeconds();
            _settings.AutoRefresh = AutoRefreshCheck.IsChecked == true;
            _settings.Save();
            _api.ChangeController(_settings.ControllerUrl);
            _user = await _api.GetCurrentUserAsync();
            ConfigureTimer();
            await RefreshFleetAsync();
            SetStatus("Settings saved", true);
        }
        catch (ControllerApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            ReturnToLogin("Sign in to the new Controller.");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, false, true);
        }
    }

    private void CopyFingerprint_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(CertificateFingerprintBox.Text);
        SetStatus("Certificate fingerprint copied", true);
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_api is not null) await _api.LogoutAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus($"Signed out locally. {exception.Message}", false, true);
        }
        ReturnToLogin("Signed out.");
    }

    private void ReturnToLogin(string message)
    {
        _refreshTimer.Stop();
        _user = null;
        ShellGrid.Visibility = Visibility.Collapsed;
        LoginGrid.Visibility = Visibility.Visible;
        AuthControllerUrl.Text = _settings.ControllerUrl;
        AuthError.Text = message;
        PasswordBox.Clear();
        UsernameBox.Focus();
    }

    private async void RetryConnection_Click(object sender, RoutedEventArgs e) => await InitializeConnectionAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshFleetAsync();

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string view }) NavigateTo(view);
    }

    private void NavigateTo(string view)
    {
        var views = new FrameworkElement[] { DashboardView, DevicesView, DiagnosticsView, FilesView, AdminView, DeploymentView, OperationsView, IntegrationsView, AuditView, SettingsView };
        foreach (var item in views) item.Visibility = item.Name == view ? Visibility.Visible : Visibility.Collapsed;

        var nav = new[] { DashboardNav, DevicesNav, DiagnosticsNav, FilesNav, AdminNav, DeploymentNav, OperationsNav, IntegrationsNav, AuditNav, SettingsNav };
        foreach (var button in nav)
            button.Background = Equals(button.Tag, view) ? (Brush)FindResource("AccentSoftBrush") : Brushes.Transparent;

        (ViewEyebrow.Text, ViewTitle.Text) = view switch
        {
            "DevicesView" => ("DEVICE DIRECTORY", "Managed computers"),
            "DiagnosticsView" => ("READ-ONLY TOOLS", "Remote diagnostics"),
            "FilesView" => ("MANAGED ROOT", "Remote file browser"),
            "AdminView" => ("CONTROLLED ADMIN", "Processes, services, terminal"),
            "DeploymentView" => ("SOFTWARE LIFECYCLE", "Packages and Windows Update"),
            "OperationsView" => ("PROTECTION AND AUTOMATION", "Backups, alerts, rules"),
            "IntegrationsView" => ("OPTIONAL INTEGRATIONS", "Pterodactyl and Docker"),
            "AuditView" => ("ACCOUNTABILITY", "Audit history"),
            "SettingsView" => ("APPLICATION", "Settings"),
            _ => ("FLEET OVERVIEW", "Command center")
        };
        if (view == "AuditView" && _auditEvents.Count == 0) _ = LoadAuditAsync();
        if (view == "DeploymentView") _ = LoadSoftwareAsync();
        if (view == "OperationsView") { _ = LoadBackupsAsync(); _ = LoadAlertsAsync(); _ = LoadAutomationsAsync(); _ = LoadComputeJobsAsync(); _ = LoadGameServersAsync(); }
        if (view == "IntegrationsView") _ = LoadIntegrationsAsync();
        if (view == "DevicesView") DeviceSearchBox.Focus();
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            await RefreshFleetAsync();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            NavigateTo("DevicesView");
            DeviceSearchBox.Focus();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E && AddDeviceButton.IsEnabled)
        {
            AddDevice_Click(AddDeviceButton, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.OemComma)
        {
            NavigateTo("SettingsView");
            e.Handled = true;
        }
    }

    private async Task RunBusyAsync(Button? button, Func<Task> action)
    {
        if (button is not null) button.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException or JsonException)
        {
            SetStatus(exception.Message, false, true);
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private void ConfigureTimer()
    {
        _refreshTimer.Stop();
        _refreshTimer.Interval = TimeSpan.FromSeconds(GetRefreshSeconds());
        if (AutoRefreshCheck.IsChecked == true) _refreshTimer.Start();
    }

    private int GetRefreshSeconds() =>
        int.TryParse((RefreshIntervalBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var seconds) ? seconds : 5;

    private void SelectRefreshInterval(int seconds)
    {
        var target = Math.Clamp(seconds, 5, 60).ToString(CultureInfo.InvariantCulture);
        RefreshIntervalBox.SelectedItem = RefreshIntervalBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, target)) ?? RefreshIntervalBox.Items[0];
    }

    private bool HasAdminPermission() => _user?.Role is "Owner" or "Administrator";
    private bool HasPowerPermission() => _user?.Role is "Owner" or "Administrator" or "Operator";

    private void SetStatus(string text, bool success = false, bool error = false)
    {
        StatusText.Text = text;
        ConnectionDot.Fill = (Brush)FindResource(error ? "DangerBrush" : success ? "SuccessBrush" : "WarningBrush");
    }

    private static string GetDiagnosticTitle(OperationKind kind) => kind switch
    {
        OperationKind.ListProcesses => "Running processes",
        OperationKind.ListServices => "Windows services",
        OperationKind.ListSoftware => "Installed software",
        OperationKind.ListNetworkAdapters => "Network adapters",
        _ => "Diagnostic results"
    };

    private static DataTable CreateDiagnosticTable(string? json)
    {
        var table = new DataTable();
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return table;
        var rows = document.RootElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray();
        foreach (var property in rows.SelectMany(row => row.EnumerateObject()).Select(property => property.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            table.Columns.Add(property);
        foreach (var item in rows)
        {
            var row = table.NewRow();
            foreach (var property in item.EnumerateObject()) row[property.Name] = FormatJson(property.Value);
            table.Rows.Add(row);
        }
        return table;
    }

    private static string FormatJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(FormatJson)),
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null => string.Empty,
        _ => value.ToString()
    };

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string EscapeDataViewLike(string value) => value
        .Replace("[", "[[]", StringComparison.Ordinal)
        .Replace("%", "[%]", StringComparison.Ordinal)
        .Replace("*", "[*]", StringComparison.Ordinal)
        .Replace("'", "''", StringComparison.Ordinal);

    private static bool IsExpected(Exception exception) =>
        exception is ControllerApiException or HttpRequestException or TaskCanceledException or TimeoutException or IOException or
            UnauthorizedAccessException or ArgumentException;
}
