using Lumen.App.Services;
using Lumen.Core.Diagnostics;
using Lumen.Core.Library;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Lumen.App;

/// <summary>
/// Steam's own web pages inside Lumen (Windows' built-in WebView2 - nothing to install):
/// "Sign in through Steam" (Lumen only receives the SteamID) and the Web API key page (Lumen can
/// read the key the user just created, after they confirm). Navigation is limited to Steam's domains.
/// Use touch, the touch keyboard or mouse mode here.
/// </summary>
public sealed class SteamSignInWindow : Window
{
    public enum Mode { SignIn, ApiKey }

    private static readonly string[] AllowedHosts =
        ["steamcommunity.com", "steampowered.com", "steamstatic.com", "akamaihd.net", "steamcontent.com", "recaptcha.net", "google.com", "gstatic.com"];

    private readonly SteamOnlineService _steam;
    private readonly Mode _mode;
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _useKey = new() { Visibility = Visibility.Collapsed, Style = (Style)Application.Current.Resources["LumenAccentButtonStyle"] };
    private string? _foundKey;
    private bool _done;

    public SteamSignInWindow(SteamOnlineService steam, Mode mode, string webDataFolder)
    {
        _steam = steam;
        _mode = mode;
        Title = mode == Mode.SignIn ? "Sign in through Steam" : "Steam Web API key";

        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();
        _useKey.Click += async (_, _) => await UseKeyAsync();

        var bar = new Grid { Padding = new Thickness(16, 10, 16, 10), ColumnSpacing = 12, Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LumenSurfaceBrush"] };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(_status);
        Grid.SetColumn(_useKey, 1);
        Grid.SetColumn(close, 2);
        bar.Children.Add(_useKey);
        bar.Children.Add(close);

        var root = new Grid { RequestedTheme = ElementTheme.Dark, Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LumenBackgroundBrush"] };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_web, 1);
        root.Children.Add(bar);
        root.Children.Add(_web);
        Content = root;

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true; // above fullscreen Lumen
            p.Maximize();
        }

        _status.Text = mode == Mode.SignIn
            ? "Sign in on Steam's page. Lumen never sees your password - it only receives your public Steam ID."
            : "Steam: enter any domain (e.g. \"localhost\"), agree and register. Lumen then offers to use the key shown.";
        _ = InitializeAsync(webDataFolder);
    }

    /// <summary>Error text (null = success). Raised once when the window finishes or closes.</summary>
    public event Action<string?>? Finished;

    private async Task InitializeAsync(string webDataFolder)
    {
        try
        {
            // Installed per machine, the program folder is not writable: keep WebView data with Lumen's data.
            Directory.CreateDirectory(webDataFolder);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webDataFolder);
            await _web.EnsureCoreWebView2Async();
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += async (_, _) => await AfterNavigationAsync();
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (IsAllowed(e.Uri)) core.Navigate(e.Uri);
            };
            Closed += (_, _) => Finish("Closed.");
            _web.Source = new Uri(_mode == Mode.SignIn ? SteamOpenId.LoginUrl() : "https://steamcommunity.com/dev/apikey");
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 could not start.", ex);
            _status.Text = "Windows' web view (WebView2) is not available on this PC, so Steam's page cannot be shown.";
        }
    }

    private async void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (SteamOpenId.IsReturn(e.Uri))
        {
            e.Cancel = true;
            _status.Text = "Confirming with Steam…";
            var error = await _steam.CompleteSignInAsync(e.Uri);
            Finish(error);
            Close();
            return;
        }
        if (!IsAllowed(e.Uri))
        {
            e.Cancel = true;
            Log.Info("Blocked navigation outside Steam in the sign-in window.");
        }
    }

    private async Task AfterNavigationAsync()
    {
        if (_mode != Mode.ApiKey || _done) return;
        try
        {
            var raw = await _web.CoreWebView2.ExecuteScriptAsync(
                "(function(){var m=(document.body&&document.body.innerText||'').match(/Key:\\s*([0-9A-Fa-f]{32})/);return m?m[1]:'';})()");
            var key = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
            if (SteamStoreApi.IsValidApiKey(key))
            {
                _foundKey = key;
                _useKey.Content = $"Use this key (…{key![^4..]})";
                _useKey.Visibility = Visibility.Visible;
                _status.Text = "Your key is shown on the page. Lumen stores it encrypted for your Windows account only.";
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Key detection failed: {ex.Message}");
        }
    }

    private async Task UseKeyAsync()
    {
        if (_foundKey is null) return;
        _useKey.IsEnabled = false;
        var error = await _steam.SetApiKeyAsync(_foundKey);
        Finish(error);
        Close();
    }

    private void Finish(string? error)
    {
        if (_done) return;
        _done = true;
        Finished?.Invoke(error);
    }

    private static bool IsAllowed(string uri) =>
        uri == "about:blank" || Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        AllowedHosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
}
