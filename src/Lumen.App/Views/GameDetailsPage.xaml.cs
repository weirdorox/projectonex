using Lumen.App.Controls;
using Lumen.App.Input;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.Core.Activity;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Core.Input;
using Lumen.Core.Models;
using Lumen.Core.Power;
using Lumen.Core.Profiles;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Lumen.App.Views;

/// <summary>Game details (X on a tile): play, favorite, stats and the per-game performance profile editor.</summary>
public sealed partial class GameDetailsPage : Page, IShellPage
{
    private readonly DispatcherQueueTimer _brightnessSave;
    private IReadOnlyList<ButtonHint>? _hints;
    private string? _gameId;
    private PerformanceProfile _profile = new() { GameId = "" };
    private HardwareProfileAdapter? _adapter;
    private bool _loading;

    public GameDetailsPage()
    {
        InitializeComponent();
        _brightnessSave = DispatcherQueue.CreateTimer();
        _brightnessSave.IsRepeating = false;
        _brightnessSave.Interval = TimeSpan.FromMilliseconds(400);
        _brightnessSave.Tick += (_, _) => _ = SaveAsync(_profile with { BrightnessPercent = (int)BrightnessSlider.Value });
    }

    private static MainWindow Shell => App.MainShell!;

    public IReadOnlyList<ButtonHint> Hints => _hints ??=
    [
        ButtonHint.Create("A", "Select"),
        ButtonHint.Create("Y", "Favorite"),
        ButtonHint.Create("R", "Scroll"),
        ButtonHint.Create("B", "Back"),
    ];

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _gameId = e.Parameter as string;
        _ = LoadAsync();
    }

    private GameEntry? Game => Shell.Library.Games.FirstOrDefault(g => string.Equals(g.Id, _gameId, StringComparison.OrdinalIgnoreCase));

    private async Task LoadAsync()
    {
        var game = Game;
        if (game is null)
        {
            TitleText.Text = "Game not found";
            return;
        }

        var tile = Shell.Library.Tile(game);
        TitleText.Text = game.Title;
        MetaText.Text = tile.Subtitle;
        var logo = GameTileViewModel.CreateBitmap(tile.LogoPath, 920);
        Logo.Source = logo;
        Logo.Visibility = logo is null ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Visibility = logo is null ? Visibility.Visible : Visibility.Collapsed;
        RenderFavorite(game);
        PlayText.Text = game.IsInstalled ? "Play" : "Install with Steam";
        _ = RenderStatsAsync(game.Id);
        _ = RenderCollectionsAsync(game.Id);
        DetailsText.Text = string.Join(Environment.NewLine, new[]
        {
            $"Source: {tile.SourceLabel}",
            game.InstallPath is null ? null : $"Installed in: {game.InstallPath}",
            $"Played: {game.PlayCount} time{(game.PlayCount == 1 ? "" : "s")}" + (game.PlaytimeSeconds >= 60 ? $", {TimeSpan.FromSeconds(game.PlaytimeSeconds).TotalHours:0.#} h tracked" : ""),
            game.LastPlayed is { } lp ? $"Last played: {lp.LocalDateTime:g}" : null,
            $"Added to Lumen: {game.AddedAt.LocalDateTime:d}",
        }.Where(l => l is not null));

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusDefault);

        // Artwork (cached / downloaded once) and colour wash.
        var service = ArtworkService.Instance;
        if (service is not null)
        {
            var hero = service.Cache.TryGetLocal(tile.HeroPath) ?? (tile.HeroPath is null ? null : await service.Cache.ResolveAsync(tile.HeroPath));
            Stage.Show(hero);
            Stage.SetTint(await service.GetAccentAsync(service.Cache.TryGetLocal(game.EffectiveCover) ?? hero));
        }

        // Profile editor.
        var sessions = Shell.GameSessions;
        if (sessions is null) return;
        _adapter = await sessions.EnsureEngineAsync();
        _profile = await sessions.GetProfileAsync(game.Id) ?? new PerformanceProfile { GameId = game.Id };
        RenderProfile();
    }

    // ---------------- profile editor ----------------

    private void RenderProfile()
    {
        _loading = true;
        try
        {
            RefreshOptions.Children.Clear();
            PlanOptions.Children.Clear();
            var hw = _adapter?.Services;

            // Refresh rate
            var rates = hw?.Display?.GetSupportedRefreshRates() ?? [];
            if (rates.Count > 1)
            {
                AddOption(RefreshOptions, "Default", _profile.RefreshHz is null, () => _profile with { RefreshHz = null });
                foreach (var hz in rates)
                    AddOption(RefreshOptions, $"{hz} Hz", _profile.RefreshHz == hz, () => _profile with { RefreshHz = hz });
            }
            else
            {
                RefreshOptions.Children.Add(Note("Not adjustable on this display."));
            }

            // TDP (only through the real backend; presets outside the CPU range are disabled)
            TdpOptions.Children.Clear();
            var tdp = Shell.Tdp;
            if (tdp.IsUsable && tdp.Range is { } range)
            {
                AddOption(TdpOptions, "Default", _profile.TdpWatts is null, () => _profile with { TdpWatts = null });
                foreach (var preset in TdpPresets.Resolve(range))
                {
                    var watts = (int)preset.Preset.TargetWatts;
                    AddOption(TdpOptions, $"{preset.Preset.Name} {watts} W", _profile.TdpWatts == watts, () => _profile with { TdpWatts = watts }, preset.Available, preset.Reason);
                }
            }
            else
            {
                TdpOptions.Children.Add(Note(tdp.Status.Locked ? "Locked by the firmware on this device." : tdp.Status.UnavailableReason ?? "Not available."));
            }

            // Power plan
            var plans = hw?.PowerPlans?.GetPlans() ?? [];
            if (plans.Count > 1)
            {
                AddOption(PlanOptions, "Default", _profile.PowerPlanId is null, () => _profile with { PowerPlanId = null });
                foreach (var plan in plans)
                    AddOption(PlanOptions, plan.Name, _profile.PowerPlanId == plan.Id, () => _profile with { PowerPlanId = plan.Id });
            }
            else
            {
                PlanOptions.Children.Add(Note("Windows has a single power plan here; use the power mode in Windows settings."));
            }

            // Brightness
            var brightnessSupported = hw?.Brightness is not null;
            BrightnessToggle.Visibility = brightnessSupported ? Visibility.Visible : Visibility.Collapsed;
            BrightnessSlider.Visibility = brightnessSupported ? Visibility.Visible : Visibility.Collapsed;
            BrightnessToggle.IsOn = _profile.BrightnessPercent is not null;
            BrightnessSlider.IsEnabled = _profile.BrightnessPercent is not null;
            BrightnessSlider.Value = _profile.BrightnessPercent ?? hw?.Brightness?.GetBrightness() ?? 70;

            // Unsupported hardware controls - explained, never faked.
            var profileInfo = hw?.Profile;
            UnsupportedText.Text = profileInfo is null
                ? "Hardware information is not available yet."
                : $"Fan: {profileInfo.ExplainUnsupported(HardwareFeature.FanControl)}";
        }
        finally
        {
            _loading = false;
        }
    }

    private void AddOption(Panel panel, string label, bool selected, Func<PerformanceProfile> change, bool enabled = true, string? reason = null)
    {
        var b = new Button
        {
            Content = label,
            Style = (Style)Application.Current.Resources["LumenPillButtonStyle"],
            IsEnabled = enabled,
        };
        if (reason is not null) ToolTipService.SetToolTip(b, reason);
        if (selected)
        {
            b.Background = (Brush)Application.Current.Resources["LumenAccentSoftBrush"];
            b.Foreground = (Brush)Application.Current.Resources["LumenAccentBrush"];
        }
        b.Click += async (_, _) =>
        {
            await SaveAsync(change());
            RenderProfile();
            FocusNavigation.Focus(panel.Children.OfType<Button>().FirstOrDefault(x => Equals(x.Content, label)) ?? (DependencyObject)panel);
        };
        panel.Children.Add(b);
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["LumenCaptionText"],
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void OnBrightnessToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        BrightnessSlider.IsEnabled = BrightnessToggle.IsOn;
        _ = SaveAsync(_profile with { BrightnessPercent = BrightnessToggle.IsOn ? (int)BrightnessSlider.Value : null });
    }

    private void OnBrightnessChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading || !BrightnessToggle.IsOn) return;
        _brightnessSave.Stop();
        _brightnessSave.Start();
    }

    private async Task SaveAsync(PerformanceProfile profile)
    {
        if (string.IsNullOrEmpty(profile.GameId) || Shell.GameSessions is not { } sessions) return;
        try
        {
            _profile = profile;
            var report = await sessions.SaveProfileAsync(profile);
            StatusText.Text = profile.IsEmpty
                ? "No profile - this game uses your current settings."
                : report.Fields.Count > 0 ? "Saved and applied to the running game." : "Saved. Applied automatically next time the game runs.";
            if (report.Failed.Any())
                StatusText.Text += $" Rejected by the device: {string.Join(", ", report.Failed)}.";
        }
        catch (Exception ex)
        {
            Log.Error("Saving profile failed.", ex);
            StatusText.Text = "Could not save the profile.";
        }
    }

    // ---------------- actions ----------------

    private void RenderFavorite(GameEntry game)
    {
        FavoriteButton.Content = game.IsFavorite ? "★  Favorite" : "☆  Add to favorites";
        HideButton.Content = game.IsHidden ? "Unhide" : "Hide from library";
    }

    private async void OnHideClick(object sender, RoutedEventArgs e)
    {
        if (Game is not { } g) return;
        try
        {
            await Shell.Library.SetHiddenAsync(g, !g.IsHidden);
            if (Game is { } updated)
            {
                RenderFavorite(updated);
                Shell.ShowToast(updated.IsHidden ? $"{updated.Title} is hidden. Find it under Library › Hidden." : $"{updated.Title} is visible again.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Hide/unhide failed.", ex);
        }
    }

    // ---------------- play history ----------------

    private async Task RenderStatsAsync(string gameId)
    {
        IReadOnlyList<PlaySession> sessions;
        try { sessions = await App.Host.History.GetForGameAsync(gameId, 500); }
        catch (Exception ex) { Log.Error("Loading play history failed.", ex); return; }

        var stats = PlayStatsCalculator.Compute(sessions, DateTimeOffset.Now);
        StatsRow.Children.Clear();
        AddStat("Total", Hours(stats.Total));
        AddStat("Sessions", stats.Sessions.ToString());
        AddStat("Average", Hours(stats.Average));
        AddStat("Last 7 days", Hours(stats.Last7Days));
        AddStat("Avg FPS", stats.AvgFps is { } fps ? $"{fps:0}" : "—");
        AddStat("Avg CPU package", stats.AvgPackageWatts is { } w ? $"{w:0.#} W" : "—");
        AddStat("Battery drain", stats.AvgBatteryPercentPerHour is { } p ? $"{p:0.#} %/h" : "—");

        Bars.Children.Clear();
        var max = Math.Max(1, stats.Last14Days.Max(d => d.Played.TotalMinutes));
        foreach (var day in stats.Last14Days)
        {
            var bar = new Border
            {
                Width = 22,
                Height = Math.Max(3, 72 * day.Played.TotalMinutes / max),
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(4),
                Background = (Brush)Application.Current.Resources[day.Played > TimeSpan.Zero ? "LumenAccentBrush" : "LumenSurfaceHighBrush"],
            };
            Bars.Children.Add(bar);
            ToolTipService.SetToolTip(bar, $"{day.Day:ddd d MMM}: {Hours(day.Played)}");
        }
        BarsCaption.Text = "Last 14 days (sessions shorter than a minute are ignored)";

        var recent = sessions.Where(s => s.Duration >= PlayStatsCalculator.MinimumSession).Take(5).Select(s =>
            $"{s.StartedAt.LocalDateTime:g} · {Hours(s.Duration)}" +
            (s.OnBattery ? " · battery" : " · plugged in") +
            (s.AvgFps is { } f && s.FpsMeasured ? $" · {f:0} FPS" : "") +
            (s.AvgPackageWatts is { } pw ? $" · {pw:0.#} W CPU" : ""));
        SessionsText.Text = sessions.Count == 0
            ? "No sessions recorded yet. Lumen records a session whenever this game is in front, however it was started."
            : string.Join(Environment.NewLine, recent) +
              (stats.AvgFps is null ? $"{Environment.NewLine}FPS is shown only when it was really measured (performance overlay telemetry) - never estimated." : "");
    }

    private void AddStat(string label, string value)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = value, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["LumenCaptionText"] });
        StatsRow.Children.Add(panel);
    }

    private static string Hours(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:0.#} h" : $"{Math.Round(t.TotalMinutes):0} min";

    // ---------------- collections ----------------

    private async Task RenderCollectionsAsync(string gameId)
    {
        try
        {
            var all = await App.Host.Collections.GetAllAsync();
            var mine = (await App.Host.Collections.GetCollectionIdsForGameAsync(gameId)).ToHashSet();
            CollectionOptions.Children.Clear();
            if (all.Count == 0)
            {
                CollectionOptions.Children.Add(Note("No collections yet."));
                return;
            }
            foreach (var c in all)
            {
                var member = mine.Contains(c.Id);
                var b = new Button
                {
                    Content = (member ? "✓  " : "") + c.Name,
                    Style = (Style)Application.Current.Resources["LumenPillButtonStyle"],
                };
                if (member)
                {
                    b.Background = (Brush)Application.Current.Resources["LumenAccentSoftBrush"];
                    b.Foreground = (Brush)Application.Current.Resources["LumenAccentBrush"];
                }
                var id = c.Id;
                b.Click += async (_, _) =>
                {
                    await App.Host.Collections.SetMembershipAsync(id, gameId, !member);
                    await RenderCollectionsAsync(gameId);
                    var again = CollectionOptions.Children.OfType<Button>().FirstOrDefault(x => ((string)x.Content).EndsWith(c.Name, StringComparison.Ordinal));
                    if (again is not null) FocusNavigation.Focus(again);
                };
                CollectionOptions.Children.Add(b);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Loading collections failed.", ex);
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (Game is { } g) GameActions.Launch(Shell.Library.Tile(g));
    }

    private async void OnFavoriteClick(object sender, RoutedEventArgs e) => await ToggleFavoriteAsync();

    private async Task ToggleFavoriteAsync()
    {
        if (Game is not { } g) return;
        await Shell.Library.ToggleFavoriteAsync(g);
        if (Game is { } updated) RenderFavorite(updated);
    }

    public void FocusDefault() => FocusNavigation.Focus(PlayButton);

    public bool HandleAction(NavAction action)
    {
        switch (action)
        {
            case NavAction.Back:
                Shell.CloseGameDetails();
                return true;
            case NavAction.Tertiary:
                _ = ToggleFavoriteAsync();
                return true;
            case NavAction.ScrollDown:
            case NavAction.ScrollUp:
                FocusNavigation.Scroll(Scroller, action == NavAction.ScrollDown);
                return true;
            default:
                return false;
        }
    }
}
