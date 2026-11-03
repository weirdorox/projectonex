using Lumen.App.Controls;
using Lumen.App.Input;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.Core.Input;
using Lumen.Core.Library;
using Lumen.Core.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Lumen.App.Views;

/// <summary>All games as a grid, with source filters (LT/RT or tap a pill).</summary>
public sealed partial class LibraryPage : Page, IShellPage
{
    private sealed record Filter(string Label, Func<GameEntry, bool> Predicate);

    private static readonly Filter[] Filters =
    [
        new("All", _ => true),
        new("Favorites", g => g.IsFavorite),
        new("Steam", g => g.Source == GameSource.Steam),
        new("Epic", g => g.Source == GameSource.Epic),
        new("GOG", g => g.Source == GameSource.Gog),
        new("Xbox", g => g.Source == GameSource.Xbox),
        new("Ubisoft", g => g.Source == GameSource.Ubisoft),
        new("EA", g => g.Source == GameSource.EA),
        new("Battle.net", g => g.Source == GameSource.BattleNet),
        new("Folders", g => g.Source is GameSource.Folder or GameSource.Manual),
        new("Hidden", g => g.IsHidden),
        new("Not installed", g => !g.IsInstalled),
    ];

    private bool ShowingHidden => Filters[_filter].Label == "Hidden";
    private bool ShowingNotInstalled => Filters[_filter].Label == "Not installed";

    private readonly List<Button> _pills = [];
    private int _filter;
    private string? _key;
    private IReadOnlyList<ButtonHint>? _hints;

    public LibraryPage()
    {
        InitializeComponent();
        for (var i = 0; i < Filters.Length; i++)
        {
            var index = i;
            var pill = new Button
            {
                Content = Filters[i].Label,
                Style = (Style)Application.Current.Resources["LumenPillButtonStyle"],
            };
            pill.Click += (_, _) => SetFilter(index);
            _pills.Add(pill);
            FilterPanel.Children.Add(pill);
        }
        Loaded += OnLoaded;
        Unloaded += (_, _) => Shell.Library.Changed -= OnLibraryChanged;
    }

    private static MainWindow Shell => App.MainShell!;

    public IReadOnlyList<ButtonHint> Hints => _hints ??=
    [
        ButtonHint.Create("A", "Play"),
        ButtonHint.Create("X", "Details"),
        ButtonHint.Create("Y", "Favorite"),
        ButtonHint.Create("LT/RT", "Filter"),
        ButtonHint.Create("B", "Home"),
    ];

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Shell.Library.Changed += OnLibraryChanged;
        Refresh();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusDefault);
    }

    private void OnLibraryChanged(object? sender, EventArgs e) => Refresh();

    private void SetFilter(int index)
    {
        _filter = (index + Filters.Length) % Filters.Length;
        Refresh();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusDefault);
    }

    private void Refresh()
    {
        var all = ShowingHidden
            ? Shell.Library.Games.Where(g => g.IsInstalled && g.IsHidden).OrderBy(g => g.SortTitle, StringComparer.Ordinal).ToList()
            : ShowingNotInstalled
                ? Shell.Library.Games.Where(g => Shell.Library.Steam.IsSignedIn && Shell.Host.Settings.SteamShowOwned
                        && !g.IsInstalled && !g.IsHidden && g.Launch.Target.StartsWith("steam://install/", StringComparison.Ordinal))
                    .OrderBy(g => g.SortTitle, StringComparer.Ordinal).ToList()
                : LibraryService.AllVisible(Shell.Library.Games);
        var games = all.Where(Filters[_filter].Predicate).ToList();

        var accent = (Brush)Application.Current.Resources["LumenAccentBrush"];
        var dim = (Brush)Application.Current.Resources["LumenTextDimBrush"];
        var soft = (Brush)Application.Current.Resources["LumenAccentSoftBrush"];
        var surface = (Brush)Application.Current.Resources["LumenSurfaceBrush"];
        for (var i = 0; i < _pills.Count; i++)
        {
            _pills[i].Foreground = i == _filter ? accent : dim;
            _pills[i].Background = i == _filter ? soft : surface;
        }

        CountText.Text = games.Count == 1 ? "1 game" : $"{games.Count} games";
        EmptyText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = Shell.Library.IsScanning ? "Looking for games…"
            : ShowingNotInstalled ? "Sign in to Steam in Settings to see games you own but haven't installed."
            : "Nothing here yet.";

        var key = _filter + ":" + string.Join('|', games.Select(g => g.Id + (g.IsFavorite ? "*" : "")));
        if (key == _key)
        {
            foreach (var g in games) Shell.Library.Tile(g);
            return;
        }
        _key = key;
        GridRepeater.ItemsSource = Shell.Library.Tiles(games);
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is GameTile tile)
        {
            tile.Invoked += OnTileInvoked;
            tile.TileFocused += OnTileFocused;
        }
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is GameTile tile)
        {
            tile.Invoked -= OnTileInvoked;
            tile.TileFocused -= OnTileFocused;
        }
    }

    private void OnTileInvoked(object? sender, GameTileViewModel vm) => GameActions.Launch(vm);

    private string? _focusedId;
    private DispatcherQueueTimer? _rememberTimer;

    /// <summary>Remember the focused game (debounced so scrolling doesn't write to disk on every step).</summary>
    private void OnTileFocused(object? sender, GameTileViewModel vm)
    {
        _focusedId = vm.Id;
        if (_rememberTimer is null)
        {
            _rememberTimer = DispatcherQueue.CreateTimer();
            _rememberTimer.IsRepeating = false;
            _rememberTimer.Interval = TimeSpan.FromMilliseconds(800);
            _rememberTimer.Tick += (_, _) => { if (_focusedId is { } id) Shell.RememberFocusedGame(id); };
        }
        _rememberTimer.Stop();
        _rememberTimer.Start();
    }

    public void FocusDefault()
    {
        if (Shell.PendingFocusGameId is { } pending && GridRepeater.ItemsSource is IReadOnlyList<GameTileViewModel> tiles)
        {
            var index = tiles.ToList().FindIndex(t => string.Equals(t.Id, pending, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && GridRepeater.GetOrCreateElement(index) is GameTile restored)
            {
                Shell.PendingFocusGameId = null;
                restored.StartBringIntoView();
                if (restored.FocusTile()) return;
            }
        }
        if (GridRepeater.ItemsSourceView is { Count: > 0 })
        {
            var element = GridRepeater.TryGetElement(0) ?? GridRepeater.GetOrCreateElement(0);
            if (element is GameTile tile && tile.FocusTile()) return;
        }
        FocusNavigation.Focus(_pills[_filter]);
    }

    public bool HandleAction(NavAction action)
    {
        switch (action)
        {
            case NavAction.NextSection:
                SetFilter(_filter + 1);
                return true;
            case NavAction.PreviousSection:
                SetFilter(_filter - 1);
                return true;
            case NavAction.Secondary:
                if ((FocusNavigation.Focused(XamlRoot) as FrameworkElement)?.DataContext is GameTileViewModel detailsVm)
                    Shell.ShowGameDetails(detailsVm.Id);
                return true;
            case NavAction.Tertiary:
                if ((FocusNavigation.Focused(XamlRoot) as FrameworkElement)?.DataContext is GameTileViewModel vm)
                    GameActions.ToggleFavorite(vm);
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
