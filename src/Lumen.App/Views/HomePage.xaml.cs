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

namespace Lumen.App.Views;

/// <summary>Dashboard: hero for the focused game plus shelves (recent, favorites, recently added).</summary>
public sealed partial class HomePage : Page, IShellPage
{
    private readonly Dictionary<ItemsRepeater, string> _shelfKeys = new();
    private IReadOnlyList<ButtonHint>? _hints;
    private bool _hadGames;
    private GameTileViewModel? _pendingHero;
    private GameTileViewModel? _shownHero;
    private readonly DispatcherQueueTimer _heroTimer;

    /// <summary>Focus must rest this long before heavy artwork work starts (fast scrolling stays smooth).</summary>
    private static readonly TimeSpan HeroSettle = TimeSpan.FromMilliseconds(140);

    public HomePage()
    {
        InitializeComponent();
        _heroTimer = DispatcherQueue.CreateTimer();
        _heroTimer.IsRepeating = false;
        _heroTimer.Interval = HeroSettle;
        _heroTimer.Tick += (_, _) => _ = ApplyHeroAsync();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static MainWindow Shell => App.MainShell!;

    public IReadOnlyList<ButtonHint> Hints => _hints ??=
    [
        ButtonHint.Create("A", "Play"),
        ButtonHint.Create("X", "Details"),
        ButtonHint.Create("Y", "Favorite"),
        ButtonHint.Create("LT/RT", "Jump shelf"),
    ];

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Shell.Library.Changed += OnLibraryChanged;
        Refresh();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusDefault);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Shell.Library.Changed -= OnLibraryChanged;

    private void OnLibraryChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var games = Shell.Library.Games;
        var visible = LibraryService.AllVisible(games);
        var recent = LibraryService.RecentlyPlayed(games, 12);
        var favorites = LibraryService.Favorites(games);
        var newest = visible.OrderByDescending(g => g.AddedAt).Take(12).ToList();

        // Dashboard layout (Settings › Personalization). "Recently added" always shows when it is the only row.
        var settings = Shell.Host.Settings;
        if (!settings.HomeShowRecent) recent = [];
        if (!settings.HomeShowFavorites) favorites = [];
        SetShelf(RecentRepeater, RecentSection, recent);
        SetShelf(FavoritesRepeater, FavoritesSection, favorites);
        var showNew = settings.HomeShowNew || (recent.Count == 0 && favorites.Count == 0 && settings.HomePinnedCollection is null);
        SetShelf(NewRepeater, NewSection, !showNew ? [] : recent.Count == 0 && favorites.Count == 0 ? visible.Take(24).ToList() : newest);
        _ = RefreshPinnedAsync(settings.HomePinnedCollection);

        EmptyState.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // First games arrived (initial load or scan): move focus from the empty state to the first tile.
        var hadGames = _hadGames;
        _hadGames = visible.Count > 0;
        if (!hadGames && _hadGames && IsLoaded)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusDefault);
        SetupBanner.Visibility = !Shell.Host.Settings.FirstRunCompleted && !Shell.Host.IsSafeMode
            ? Visibility.Visible : Visibility.Collapsed;

        if (visible.Count == 0)
        {
            HeroTitle.Text = Shell.Library.IsScanning ? "Looking for games…" : "Welcome to Lumen";
            HeroMeta.Text = string.Empty;
            HeroLogo.Visibility = Visibility.Collapsed;
            HeroTitle.Visibility = Visibility.Visible;
            Stage.Show(null);
            Stage.SetTint(null);
            _shownHero = null;
        }
    }

    private async Task RefreshPinnedAsync(string? collectionId)
    {
        IReadOnlyList<GameEntry> games = [];
        string? name = null;
        if (collectionId is not null)
        {
            try
            {
                var collection = (await App.Host.Collections.GetAllAsync()).FirstOrDefault(c => c.Id == collectionId);
                if (collection is not null)
                {
                    name = collection.Name;
                    var ids = new HashSet<string>(await App.Host.Collections.GetGameIdsAsync(collectionId), StringComparer.OrdinalIgnoreCase);
                    games = LibraryService.AllVisible(Shell.Library.Games).Where(g => ids.Contains(g.Id)).ToList();
                }
            }
            catch (Exception ex)
            {
                Lumen.Core.Diagnostics.Log.Warn("Loading pinned collection failed.", ex);
            }
        }
        PinnedHeader.Text = name ?? string.Empty;
        SetShelf(PinnedRepeater, PinnedSection, games);
    }

    /// <summary>Only rebuilds a shelf when its contents actually changed, so focus is not lost needlessly.</summary>
    private void SetShelf(ItemsRepeater repeater, FrameworkElement section, IReadOnlyList<GameEntry> games)
    {
        var key = string.Join('|', games.Select(g => g.Id + (g.IsFavorite ? "*" : "")));
        section.Visibility = games.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_shelfKeys.TryGetValue(repeater, out var old) && old == key)
        {
            // Same games in the same order: update view models in place.
            foreach (var g in games) Shell.Library.Tile(g);
            return;
        }
        _shelfKeys[repeater] = key;
        repeater.ItemsSource = Shell.Library.Tiles(games);
    }

    // ---------------- tiles ----------------

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

    private void OnTileFocused(object? sender, GameTileViewModel vm)
    {
        // Text is cheap and updates instantly; artwork waits until focus settles.
        HeroTitle.Text = vm.Title;
        HeroMeta.Text = vm.Subtitle;
        _pendingHero = vm;
        _heroTimer.Stop();
        _heroTimer.Start();
    }

    /// <summary>Cross-fades backdrop, logo and colour wash to the settled game.</summary>
    private async Task ApplyHeroAsync()
    {
        var vm = _pendingHero;
        if (vm is null || vm == _shownHero) return;
        _shownHero = vm;
        Shell.RememberFocusedGame(vm.Id);

        // Logo replaces the title text when the store provides one.
        var logo = GameTileViewModel.CreateBitmap(vm.LogoPath, 880);
        HeroLogo.OpacityTransition = null;
        HeroLogo.Opacity = 0;
        if (logo is not null)
        {
            logo.ImageOpened += (_, _) =>
            {
                if (_shownHero != vm) return;
                HeroLogo.OpacityTransition = Motion.Fade(300);
                HeroLogo.Opacity = 1;
            };
            HeroLogo.Source = logo;
            HeroLogo.Visibility = Visibility.Visible;
            HeroTitle.Visibility = Visibility.Collapsed;
        }
        else
        {
            HeroLogo.Source = null;
            HeroLogo.Visibility = Visibility.Collapsed;
            HeroTitle.Visibility = Visibility.Visible;
        }

        var service = ArtworkService.Instance;
        var key = vm.HeroPath;
        var local = service?.Cache.TryGetLocal(key);
        if (local is null && service is not null && key is not null)
            local = await service.Cache.ResolveAsync(key); // remote art: download once, then cached
        if (_shownHero != vm) return;

        Stage.Show(local);
        var accent = service is null ? null : await service.GetAccentAsync(service.Cache.TryGetLocal(vm.Game.EffectiveCover) ?? local);
        if (_shownHero == vm) Stage.SetTint(accent);
    }

    // ---------------- controller ----------------

    private ItemsRepeater[] VisibleShelves() =>
        new[] { (RecentRepeater, RecentSection), (FavoritesRepeater, FavoritesSection), (PinnedRepeater, PinnedSection), (NewRepeater, NewSection) }
            .Where(s => s.Item2.Visibility == Visibility.Visible)
            .Select(s => s.Item1)
            .ToArray();

    public void FocusDefault()
    {
        // Session restore: put focus back on the game the user was on.
        if (Shell.PendingFocusGameId is { } pending && FocusGame(pending))
        {
            Shell.PendingFocusGameId = null;
            return;
        }
        foreach (var shelf in VisibleShelves())
        {
            if (FocusFirst(shelf)) return;
        }
        if (EmptyState.Visibility == Visibility.Visible) FocusNavigation.Focus(ScanButton);
        else if (Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(this) is { } first) FocusNavigation.Focus(first);
    }

    private bool FocusGame(string gameId)
    {
        foreach (var shelf in VisibleShelves())
        {
            if (shelf.ItemsSource is not IReadOnlyList<GameTileViewModel> tiles) continue;
            for (var i = 0; i < tiles.Count; i++)
            {
                if (!string.Equals(tiles[i].Id, gameId, StringComparison.OrdinalIgnoreCase)) continue;
                if (shelf.GetOrCreateElement(i) is GameTile tile)
                {
                    tile.StartBringIntoView();
                    return tile.FocusTile();
                }
            }
        }
        return false;
    }

    private static bool FocusFirst(ItemsRepeater repeater)
    {
        if (repeater.ItemsSourceView is null || repeater.ItemsSourceView.Count == 0) return false;
        var element = repeater.TryGetElement(0) ?? repeater.GetOrCreateElement(0);
        return element is GameTile tile && tile.FocusTile();
    }

    public bool HandleAction(NavAction action)
    {
        switch (action)
        {
            case NavAction.Tertiary:
                if (FocusedTile() is { } fav) GameActions.ToggleFavorite(fav);
                return true;
            case NavAction.Secondary:
                if (FocusedTile() is { } details) Shell.ShowGameDetails(details.Id);
                return true;
            case NavAction.ScrollDown:
            case NavAction.ScrollUp:
                FocusNavigation.Scroll(Scroller, action == NavAction.ScrollDown);
                return true;
            case NavAction.NextSection:
            case NavAction.PreviousSection:
                JumpShelf(action == NavAction.NextSection ? 1 : -1);
                return true;
            default:
                return false;
        }
    }

    private GameTileViewModel? FocusedTile() =>
        (FocusNavigation.Focused(XamlRoot) as FrameworkElement)?.DataContext as GameTileViewModel;

    private void JumpShelf(int delta)
    {
        var shelves = VisibleShelves();
        if (shelves.Length == 0) return;
        var focused = FocusNavigation.Focused(XamlRoot) as DependencyObject;
        var current = Array.FindIndex(shelves, s => IsDescendant(s, focused));
        var next = Math.Clamp(current < 0 ? 0 : current + delta, 0, shelves.Length - 1);
        FocusFirst(shelves[next]);
    }

    private static bool IsDescendant(DependencyObject ancestor, DependencyObject? node)
    {
        while (node is not null)
        {
            if (node == ancestor) return true;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnSetupClick(object sender, RoutedEventArgs e) => Shell.NavigateTab(MainWindow.SettingsTabIndex);

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        Shell.ShowToast("Scanning for games…");
        var report = await Shell.Library.RescanAsync();
        Shell.ShowToast(report is null ? "A scan is already running." : $"Found {report.Total} games.");
    }
}
