using Lumen.App.Controls;
using Lumen.App.Input;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.Core.Diagnostics;
using Lumen.Core.Input;
using Lumen.Core.Library;
using Lumen.Core.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Lumen.App.Views;

/// <summary>
/// Favorites plus user collections. Games are added to collections from the game details page (X on a tile);
/// collections are created here with the on-screen keyboard. Delete needs a second press to confirm.
/// </summary>
public sealed partial class CollectionsPage : Page, IShellPage
{
    private const string FavoritesId = "__favorites";
    private readonly List<(string Id, Button Pill)> _pills = [];
    private IReadOnlyList<GameCollection> _collections = [];
    private string _selected = FavoritesId;
    private bool _creating;
    private bool _confirmDelete;
    private IReadOnlyList<ButtonHint>? _hints;
    private IReadOnlyList<GameTileViewModel> _tiles = [];

    public CollectionsPage()
    {
        InitializeComponent();
        Keyboard.Target = NameBox;
        Keyboard.Done += async (_, _) => await CreateAsync();
        Loaded += async (_, _) =>
        {
            Shell.Library.Changed += OnLibraryChanged;
            await ReloadAsync();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusDefault);
        };
        Unloaded += (_, _) => Shell.Library.Changed -= OnLibraryChanged;
    }

    private static MainWindow Shell => App.MainShell!;
    private static ICollectionRepository Repo => App.Host.Collections;

    public IReadOnlyList<ButtonHint> Hints => _hints ??=
    [
        ButtonHint.Create("A", "Play"),
        ButtonHint.Create("X", "Details / add to collection"),
        ButtonHint.Create("LT/RT", "Collection"),
        ButtonHint.Create("B", "Home"),
    ];

    private async void OnLibraryChanged(object? sender, EventArgs e) => await RenderGamesAsync();

    private async Task ReloadAsync()
    {
        try
        {
            _collections = await Repo.GetAllAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Loading collections failed.", ex);
            _collections = [];
        }
        if (_selected != FavoritesId && _collections.All(c => c.Id != _selected)) _selected = FavoritesId;
        RenderPills();
        await RenderGamesAsync();
    }

    private void RenderPills()
    {
        PillPanel.Children.Clear();
        _pills.Clear();
        AddPill(FavoritesId, "★ Favorites");
        foreach (var c in _collections) AddPill(c.Id, c.Name);
        var add = new Button { Content = "+ New collection", Style = (Style)Application.Current.Resources["LumenPillButtonStyle"] };
        add.Click += (_, _) => ShowCreate(true);
        PillPanel.Children.Add(add);
    }

    private void AddPill(string id, string label)
    {
        var selected = id == _selected;
        var pill = new Button
        {
            Content = label,
            Style = (Style)Application.Current.Resources["LumenPillButtonStyle"],
            Foreground = (Brush)Application.Current.Resources[selected ? "LumenAccentBrush" : "LumenTextDimBrush"],
            Background = (Brush)Application.Current.Resources[selected ? "LumenAccentSoftBrush" : "LumenSurfaceBrush"],
        };
        pill.Click += async (_, _) => await SelectAsync(id);
        _pills.Add((id, pill));
        PillPanel.Children.Add(pill);
    }

    private async Task SelectAsync(string id)
    {
        _selected = id;
        _confirmDelete = false;
        ShowCreate(false);
        RenderPills();
        await RenderGamesAsync();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusDefault);
    }

    private async Task RenderGamesAsync()
    {
        IReadOnlyList<GameEntry> games;
        if (_selected == FavoritesId)
        {
            games = LibraryService.AllVisible(Shell.Library.Games).Where(g => g.IsFavorite).ToList();
        }
        else
        {
            HashSet<string> ids;
            try { ids = new HashSet<string>(await Repo.GetGameIdsAsync(_selected), StringComparer.OrdinalIgnoreCase); }
            catch (Exception ex) { Log.Error("Loading collection members failed.", ex); ids = []; }
            games = Shell.Library.Games.Where(g => g.IsInstalled && ids.Contains(g.Id))
                .OrderBy(g => g.SortTitle, StringComparer.Ordinal).ToList();
        }

        _tiles = Shell.Library.Tiles(games);
        GridRepeater.ItemsSource = _tiles;
        CountText.Text = games.Count == 1 ? "1 game" : $"{games.Count} games";
        EmptyText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _selected == FavoritesId
            ? "No favorites yet. Press Y on any game to add it."
            : "This collection is empty. Press X on a game anywhere in Lumen and choose this collection.";

        ActionsPanel.Children.Clear();
        if (_selected != FavoritesId)
        {
            var delete = new Button
            {
                Content = _confirmDelete ? "Press again to delete this collection" : "Delete collection",
                Style = (Style)Application.Current.Resources["LumenPillButtonStyle"],
            };
            if (_confirmDelete) delete.Foreground = (Brush)Application.Current.Resources["LumenBadBrush"];
            delete.Click += async (_, _) => await DeleteAsync();
            ActionsPanel.Children.Add(delete);
        }
        ActionsPanel.Visibility = ActionsPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task DeleteAsync()
    {
        if (!_confirmDelete)
        {
            _confirmDelete = true;
            await RenderGamesAsync();
            if (ActionsPanel.Children.FirstOrDefault() is Button b) FocusNavigation.Focus(b);
            return;
        }
        try
        {
            await Repo.DeleteAsync(_selected);
            Log.Info("Collection deleted.");
        }
        catch (Exception ex)
        {
            Log.Error("Deleting collection failed.", ex);
        }
        _selected = FavoritesId;
        _confirmDelete = false;
        await ReloadAsync();
        FocusDefault();
    }

    private void ShowCreate(bool show)
    {
        _creating = show;
        NewPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        NewError.Text = string.Empty;
        if (show)
        {
            NameBox.Text = string.Empty;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusNavigation.Focus(Keyboard.FirstKey));
        }
    }

    private async Task CreateAsync()
    {
        try
        {
            var collection = GameCollection.Create(NameBox.Text ?? "", _collections.Count + 1);
            await Repo.SaveAsync(collection);
            _selected = collection.Id;
            ShowCreate(false);
            await ReloadAsync();
            FocusDefault();
        }
        catch (ArgumentException ex)
        {
            NewError.Text = ex.Message.Split(" (")[0];
        }
        catch (Exception ex)
        {
            Log.Error("Creating collection failed.", ex);
            NewError.Text = "Could not save the collection.";
        }
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is GameTile tile) tile.Invoked += OnTileInvoked;
    }

    private void OnElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is GameTile tile) tile.Invoked -= OnTileInvoked;
    }

    private void OnTileInvoked(object? sender, GameTileViewModel vm) => GameActions.Launch(vm);

    public void FocusDefault()
    {
        if (_creating)
        {
            FocusNavigation.Focus(Keyboard.FirstKey);
            return;
        }
        if (_tiles.Count > 0)
        {
            var element = GridRepeater.TryGetElement(0) ?? GridRepeater.GetOrCreateElement(0);
            if (element is GameTile tile && tile.FocusTile()) return;
        }
        var pill = _pills.FirstOrDefault(p => p.Id == _selected).Pill;
        if (pill is not null) FocusNavigation.Focus(pill);
    }

    public bool HandleAction(NavAction action)
    {
        var focusedVm = (FocusNavigation.Focused(XamlRoot) as FrameworkElement)?.DataContext as GameTileViewModel;
        if (_creating)
        {
            switch (action)
            {
                case NavAction.Back:
                    ShowCreate(false);
                    FocusDefault();
                    return true;
                case NavAction.Secondary:
                    Keyboard.Backspace();
                    return true;
                case NavAction.Tertiary:
                    Keyboard.Type(" ");
                    return true;
            }
        }

        switch (action)
        {
            case NavAction.NextSection:
            case NavAction.PreviousSection:
            {
                var ids = new List<string> { FavoritesId };
                ids.AddRange(_collections.Select(c => c.Id));
                var i = ids.IndexOf(_selected);
                i = (i + (action == NavAction.NextSection ? 1 : -1) + ids.Count) % ids.Count;
                _ = SelectAsync(ids[i]);
                return true;
            }
            case NavAction.Secondary:
                if (focusedVm is not null) Shell.ShowGameDetails(focusedVm.Id);
                return true;
            case NavAction.Tertiary:
                if (focusedVm is not null) GameActions.ToggleFavorite(focusedVm);
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
