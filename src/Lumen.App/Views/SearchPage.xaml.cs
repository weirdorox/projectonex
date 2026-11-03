using Lumen.App.Controls;
using Lumen.App.Input;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.Core.Input;
using Lumen.Core.Library;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lumen.App.Views;

/// <summary>Universal search across every store: on-screen keyboard on the left, instant results on the right.</summary>
public sealed partial class SearchPage : Page, IShellPage
{
    private IReadOnlyList<ButtonHint>? _hints;
    private IReadOnlyList<GameTileViewModel> _results = [];

    public SearchPage()
    {
        InitializeComponent();
        Keyboard.Target = QueryBox;
        Keyboard.Done += (_, _) => FocusResults();
        Loaded += (_, _) =>
        {
            Shell.Library.Changed += OnLibraryChanged;
            Refresh();
        };
        Unloaded += (_, _) => Shell.Library.Changed -= OnLibraryChanged;
    }

    private static MainWindow Shell => App.MainShell!;

    public IReadOnlyList<ButtonHint> Hints => _hints ??=
    [
        ButtonHint.Create("A", "Select"),
        ButtonHint.Create("X", "Delete / Details"),
        ButtonHint.Create("Y", "Space / Favorite"),
        ButtonHint.Create("LT/RT", "Keys / Results"),
        ButtonHint.Create("B", "Home"),
    ];

    private void OnLibraryChanged(object? sender, EventArgs e) => Refresh();

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var query = QueryBox.Text ?? string.Empty;
        var games = GameSearch.Search(Shell.Library.Games, query, includeHidden: false, max: 60);
        _results = Shell.Library.Tiles(games);
        ResultsRepeater.ItemsSource = _results;
        CountText.Text = string.IsNullOrWhiteSpace(query)
            ? "Type to search every store"
            : games.Count == 1 ? "1 match" : $"{games.Count} matches";
        EmptyText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = "No games match. Hidden games are not searched - see Library › Hidden.";
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

    private void FocusResults()
    {
        if (_results.Count == 0) return;
        var element = ResultsRepeater.TryGetElement(0) ?? ResultsRepeater.GetOrCreateElement(0);
        if (element is GameTile tile) tile.FocusTile();
    }

    public void FocusDefault() => FocusNavigation.Focus(Keyboard.FirstKey);

    public bool HandleAction(NavAction action)
    {
        var focusedVm = (FocusNavigation.Focused(XamlRoot) as FrameworkElement)?.DataContext as GameTileViewModel;
        switch (action)
        {
            case NavAction.NextSection:
                FocusResults();
                return true;
            case NavAction.PreviousSection:
                FocusDefault();
                return true;
            case NavAction.Secondary:
                if (focusedVm is not null) Shell.ShowGameDetails(focusedVm.Id);
                else Keyboard.Backspace();
                return true;
            case NavAction.Tertiary:
                if (focusedVm is not null) GameActions.ToggleFavorite(focusedVm);
                else Keyboard.Type(" ");
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
