using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using VeltrixControl.Contracts;

namespace VeltrixControl.Desktop;

public partial class FileEditorWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ControllerApiClient _api;
    private readonly Guid _deviceId;
    private readonly string _path;
    private List<FileBackupEntry> _backups = [];
    private string? _hash;
    private bool _dirty;
    private bool _loading;

    public FileEditorWindow(ControllerApiClient api, Guid deviceId, string path, string displayName, bool showHistory = false)
    {
        InitializeComponent();
        _api = api;
        _deviceId = deviceId;
        _path = path;
        Title = $"Edit · {displayName}";
        if (showHistory) HistoryPanel.Visibility = Visibility.Visible;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
        if (e.Key == System.Windows.Input.Key.S)
        {
            e.Handled = true;
            Save_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == System.Windows.Input.Key.F)
        {
            e.Handled = true;
            if (FindPanel.Visibility != Visibility.Visible) ToggleFind_Click(this, new RoutedEventArgs());
            FindBox.Focus();
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            StatusLine.Text = "Loading file…";
            var result = await RunAsync(OperationKind.ReadTextFile, _path);
            var file = JsonSerializer.Deserialize<TextFileResult>(result.ResultJson ?? "{}", JsonOptions);
            if (file is null) throw new InvalidOperationException("The node returned an empty file result.");
            _loading = true;
            EditorBox.Text = file.Content;
            _loading = false;
            _hash = file.Hash;
            _dirty = false;
            DirtyIndicator.Text = string.Empty;
            UpdateLineNumbers();
            StatusLine.Text = $"{file.Encoding} · {DeviceRow.FormatBytes(file.SizeBytes)} · modified {file.LastModified.LocalDateTime:g}";
            await LoadHistoryAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusLine.Text = exception.Message;
        }
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            var result = await RunAsync(OperationKind.ListFileBackups, _path);
            var history = JsonSerializer.Deserialize<FileBackupListResult>(result.ResultJson ?? "{}", JsonOptions);
            _backups = [.. history?.Backups ?? []];
            HistoryList.ItemsSource = _backups.Select(backup => new BackupRow(backup)).ToList();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusLine.Text = exception.Message;
        }
    }

    private async Task<OperationView> RunAsync(OperationKind kind, string argument)
    {
        var queued = await _api.CreateOperationAsync(_deviceId, kind, argument, true);
        var completed = await _api.WaitForOperationAsync(queued.Id, TimeSpan.FromSeconds(30));
        if (completed.State != OperationState.Succeeded)
            throw new InvalidOperationException(completed.Error ?? $"The node ended the request with {completed.State}.");
        return completed;
    }

    private async Task<OperationView> RunAsync(OperationKind kind, object argument) =>
        await RunAsync(kind, JsonSerializer.Serialize(argument, JsonOptions));

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusLine.Text = "Saving…";
            var result = await RunAsync(OperationKind.WriteTextFile, new WriteTextFileArgument(_path, EditorBox.Text, _hash, true));
            var file = JsonSerializer.Deserialize<TextFileResult>(result.ResultJson ?? "{}", JsonOptions);
            _hash = file?.Hash ?? _hash;
            _dirty = false;
            DirtyIndicator.Text = string.Empty;
            StatusLine.Text = $"Saved at {DateTime.Now:t}";
            await LoadHistoryAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusLine.Text = exception.Message;
            if (MessageBox.Show(this, $"{exception.Message}\n\nReload the file from the node?", "Save failed", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                await LoadAsync();
            }
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_dirty && MessageBox.Show(this, "Discard unsaved changes?", "Reload", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await LoadAsync();
    }

    private void ToggleFind_Click(object sender, RoutedEventArgs e)
    {
        FindPanel.Visibility = FindPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (FindPanel.Visibility == Visibility.Visible) FindBox.Focus();
    }

    private void ToggleHistory_Click(object sender, RoutedEventArgs e)
    {
        HistoryPanel.Visibility = HistoryPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        var query = FindBox.Text;
        if (string.IsNullOrEmpty(query)) return;
        var start = Math.Min(EditorBox.SelectionStart + EditorBox.SelectionLength, EditorBox.Text.Length);
        var index = EditorBox.Text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0 && start > 0) index = EditorBox.Text.IndexOf(query, 0, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            StatusLine.Text = "No match found.";
            return;
        }
        EditorBox.Focus();
        EditorBox.Select(index, query.Length);
        EditorBox.ScrollToLine(EditorBox.GetLineIndexFromCharacterIndex(index));
        StatusLine.Text = $"Match at offset {index}";
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        var query = FindBox.Text;
        if (string.IsNullOrEmpty(query)) return;
        var matches = CountOccurrences(EditorBox.Text, query);
        if (matches == 0)
        {
            StatusLine.Text = "No match found.";
            return;
        }
        var caret = EditorBox.SelectionStart;
        EditorBox.Text = EditorBox.Text.Replace(query, ReplaceBox.Text, StringComparison.OrdinalIgnoreCase);
        EditorBox.CaretIndex = Math.Min(caret, EditorBox.Text.Length);
        StatusLine.Text = $"Replaced {matches} occurrence{(matches == 1 ? string.Empty : "s")}";
    }

    private static int CountOccurrences(string text, string query)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(query, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += query.Length;
        }
        return count;
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not BackupRow row) return;
        if (MessageBox.Show(this, $"Restore backup {row.Name}? The current content is backed up first.", "Restore backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await RunAsync(OperationKind.RestoreFileBackup, new RestoreFileBackupArgument(_path, row.Name));
            await LoadAsync();
            StatusLine.Text = $"Restored {row.Name}";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusLine.Text = exception.Message;
        }
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not BackupRow row) return;
        try
        {
            var result = await RunAsync(OperationKind.ReadFileBackup, new ReadFileBackupArgument(_path, row.Name));
            var backup = JsonSerializer.Deserialize<TextFileResult>(result.ResultJson ?? "{}", JsonOptions);
            new DiffWindow($"{_path} · {row.Name}", backup?.Content ?? string.Empty, EditorBox.Text) { Owner = this }.ShowDialog();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusLine.Text = exception.Message;
        }
    }

    private void EditorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateLineNumbers();
        if (_loading) return;
        _dirty = true;
        DirtyIndicator.Text = "Unsaved changes";
    }

    private void EditorBox_ScrollChanged(object sender, ScrollChangedEventArgs e) => SyncLineScroll();

    private void UpdateLineNumbers()
    {
        var lines = EditorBox.LineCount;
        if (lines <= 0) lines = 1;
        var builder = new System.Text.StringBuilder(lines * 4);
        for (var line = 1; line <= lines; line++) builder.Append(line).Append('\n');
        LineNumbers.Text = builder.ToString();
        SyncLineScroll();
    }

    private void SyncLineScroll()
    {
        var firstLine = EditorBox.GetFirstVisibleLineIndex();
        if (firstLine >= 0) LineNumbers.ScrollToLine(firstLine);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!_dirty) return;
        var choice = MessageBox.Show(this, "Save changes before closing?", "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
        }
        else if (choice == MessageBoxResult.Yes)
        {
            e.Cancel = true;
            Save_Click(this, new RoutedEventArgs());
            if (!_dirty) Close();
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is ControllerApiException or HttpRequestException or TaskCanceledException or TimeoutException or
            IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException;

    private sealed class BackupRow(FileBackupEntry source)
    {
        public string Name => source.BackupName;
        public string Display => $"{source.CreatedAt.LocalDateTime:g} · {DeviceRow.FormatBytes(source.SizeBytes)}";
    }
}
