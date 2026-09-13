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
    private ControllerApiClient? _api;
    private UserIdentity? _user;
    private List<DeviceSummary> _devices = [];
    private List<AuditEventView> _auditEvents = [];
    private DataTable? _diagnosticTable;
    private string _currentPath = string.Empty;
    private bool _setupRequired;
    private bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();
        _refreshTimer.Tick += async (_, _) => await RefreshFleetAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
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
        var onlineRows = rows.Where(row => row.Source.Online).ToList();
        DiagnosticsDeviceBox.ItemsSource = onlineRows;
        FilesDeviceBox.ItemsSource = onlineRows;
        DiagnosticsDeviceBox.SelectedItem = onlineRows.FirstOrDefault(row => row.Id == diagnosticsId) ?? onlineRows.FirstOrDefault();
        FilesDeviceBox.SelectedItem = onlineRows.FirstOrDefault(row => row.Id == filesId) ?? onlineRows.FirstOrDefault();
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

    private async void FilesUp_Click(object sender, RoutedEventArgs e)
    {
        _currentPath = Path.GetDirectoryName(_currentPath) ?? string.Empty;
        await LoadFilesAsync();
    }

    private async void FilesGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is not FileRow { Source.IsDirectory: true } folder) return;
        _currentPath = folder.Source.RelativePath;
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
            var files = JsonSerializer.Deserialize<FileEntry[]>(completed.ResultJson ?? "[]", JsonOptions) ?? [];
            FilesGrid.ItemsSource = files.OrderByDescending(file => file.IsDirectory).ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Select(file => new FileRow(file)).ToList();
            CurrentPathBox.Text = string.IsNullOrWhiteSpace(_currentPath) ? "Managed root" : _currentPath;
            SetStatus($"Loaded {files.Length} entries", true);
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException or JsonException)
        {
            SetStatus(exception.Message, false, true);
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
        var views = new FrameworkElement[] { DashboardView, DevicesView, DiagnosticsView, FilesView, AuditView, SettingsView };
        foreach (var item in views) item.Visibility = item.Name == view ? Visibility.Visible : Visibility.Collapsed;

        var nav = new[] { DashboardNav, DevicesNav, DiagnosticsNav, FilesNav, AuditNav, SettingsNav };
        foreach (var button in nav)
            button.Background = Equals(button.Tag, view) ? (Brush)FindResource("AccentSoftBrush") : Brushes.Transparent;

        (ViewEyebrow.Text, ViewTitle.Text) = view switch
        {
            "DevicesView" => ("DEVICE DIRECTORY", "Managed computers"),
            "DiagnosticsView" => ("READ-ONLY TOOLS", "Remote diagnostics"),
            "FilesView" => ("MANAGED ROOT", "Remote file browser"),
            "AuditView" => ("ACCOUNTABILITY", "Audit history"),
            "SettingsView" => ("APPLICATION", "Settings"),
            _ => ("FLEET OVERVIEW", "Command center")
        };
        if (view == "AuditView" && _auditEvents.Count == 0) _ = LoadAuditAsync();
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
