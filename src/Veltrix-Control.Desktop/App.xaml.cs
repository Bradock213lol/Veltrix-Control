using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace VeltrixControl.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            var choice = MessageBox.Show(
                $"Veltrix-Control could not continue.\n\n{eventArgs.Exception.Message}\n\nOpen the browser fallback?",
                "Veltrix-Control",
                MessageBoxButton.YesNo,
                MessageBoxImage.Error);
            if (choice == MessageBoxResult.Yes) OpenBrowserFallback();
            Shutdown(1);
        };
        ApplySystemTheme();
        base.OnStartup(e);
    }

    private void ApplySystemTheme()
    {
        var themeValue = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme",
            0);
        var usesLightTheme = themeValue is int setting && setting == 1;

        if (!usesLightTheme) return;
        SetColor("WindowBrush", "#F4F6FB");
        SetColor("SurfaceBrush", "#FFFFFF");
        SetColor("SurfaceMutedBrush", "#EDF0F7");
        SetColor("TextBrush", "#171A29");
        SetColor("MutedTextBrush", "#596178");
        SetColor("BorderBrush", "#D4D9E6");
        SetColor("AccentBrush", "#4054D9");
        SetColor("AccentSoftBrush", "#E4E8FF");
        SetColor("CyanBrush", "#007B9E");
        SetColor("SuccessBrush", "#087A55");
        SetColor("WarningBrush", "#8C5A00");
        SetColor("DangerBrush", "#B4233C");
    }

    private void SetColor(string key, string value) => Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));

    public static void OpenBrowserFallback(string url = "http://localhost:5187/")
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
