using System.Numerics;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Lumen.App.Controls;

/// <summary>
/// Portrait game tile. The inner Button is the focus/invoke target (touch tap, mouse click and
/// controller "A" all raise <see cref="Invoked"/>). Focus scales the tile up slightly.
/// </summary>
public sealed partial class GameTile : UserControl
{
    /// <summary>One shadow instance shared by all tiles (only the focused tile is lifted).</summary>
    private static readonly ThemeShadow LiftShadow = new();

    public GameTile()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            ViewModel = e.NewValue as GameTileViewModel;
            Bindings.Update();
            UpdateCaption(focused: false);
        };
    }

    public GameTileViewModel? ViewModel { get; private set; }

    public event EventHandler<GameTileViewModel>? Invoked;
    public event EventHandler<GameTileViewModel>? TileFocused;

    public bool FocusTile() => TileButton.Focus(FocusState.Keyboard);

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) Invoked?.Invoke(this, ViewModel);
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        TileButton.ScaleTransition = Motion.Vector(160);
        TileButton.TranslationTransition = Motion.Vector(160);
        TileButton.CenterPoint = new Vector3((float)TileButton.ActualWidth / 2f, (float)TileButton.ActualHeight / 2f, 0f);
        TileButton.Scale = new Vector3(1.07f, 1.07f, 1f);
        if (Motion.Enabled)
        {
            TileButton.Shadow = LiftShadow;
            TileButton.Translation = new Vector3(0f, -4f, 28f); // lift towards the viewer
        }
        Canvas.SetZIndex(this, 1);
        UpdateCaption(focused: true);
        if (ViewModel is not null) TileFocused?.Invoke(this, ViewModel);
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        TileButton.Scale = Vector3.One;
        TileButton.Translation = Vector3.Zero;
        TileButton.Shadow = null;
        Canvas.SetZIndex(this, 0);
        UpdateCaption(focused: false);
    }

    private void UpdateCaption(bool focused)
    {
        var hasArt = ViewModel?.CoverVisibility == Visibility.Visible;
        Caption.Visibility = !hasArt || focused ? Visibility.Visible : Visibility.Collapsed;
    }
}
