using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace VeltrixControl.Desktop;

public partial class CarbonWindow : Window
{
    private readonly Uri _controller;
    private readonly string? _sessionCookie;

    public CarbonWindow(Uri controller, string? sessionCookie)
    {
        InitializeComponent();
        _controller = controller;
        _sessionCookie = sessionCookie;
        Loaded += Window_Loaded;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Veltrix-Control",
                "webview2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await Browser.EnsureCoreWebView2Async(environment);

            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;

            if (!string.IsNullOrEmpty(_sessionCookie))
            {
                var cookie = Browser.CoreWebView2.CookieManager.CreateCookie(
                    "Veltrix-Control.Session", _sessionCookie, _controller.Host, "/");
                cookie.IsHttpOnly = true;
                cookie.IsSecure = string.Equals(_controller.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
                Browser.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
            }

            Browser.Source = new Uri(_controller, "/carbon/index.html");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this,
                $"The Carbon console could not start.\n\n{exception.Message}",
                "Veltrix-Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }
}
