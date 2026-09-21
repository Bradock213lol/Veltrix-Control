using System.Diagnostics;
using System.IO;
using System.Windows;

namespace VeltrixControl.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var desktopPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Desktop", "Veltrix-Control.Desktop.exe"));
        string? failure = null;
        try
        {
            if (!File.Exists(desktopPath))
            {
                failure = "The native desktop application is missing.";
            }
            else
            {
                using var process = Process.Start(new ProcessStartInfo(desktopPath) { UseShellExecute = true });
                if (process is null)
                {
                    failure = "Windows could not start the native desktop application.";
                }
                else if (process.WaitForExit(2500))
                {
                    failure = $"The native desktop application closed during startup (exit code {process.ExitCode}).";
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            failure = exception.Message;
        }

        if (failure is null) return;
        var choice = MessageBox.Show(
            $"{failure}\n\nOpen the local web fallback instead?",
            "Veltrix-Control could not start",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);
        if (choice == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo("http://localhost:5187/carbon/index.html") { UseShellExecute = true });
    }
}
