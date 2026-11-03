using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Lumen.App.Controls;

/// <summary>
/// A controller button legend: a round badge with the button letter plus a label, e.g. (A) Play.
/// Drawn as an original badge (no platform-holder button art). Code-only, so it can be created
/// dynamically by pages that change their hints.
/// </summary>
public sealed class ButtonHint : StackPanel
{
    private readonly TextBlock _glyph;
    private readonly TextBlock _label;

    public ButtonHint()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        VerticalAlignment = VerticalAlignment.Center;

        _glyph = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["LumenTextBrush"],
        };
        var badge = new Border
        {
            MinWidth = 26,
            Height = 26,
            Padding = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(1.5),
            BorderBrush = (Brush)Application.Current.Resources["LumenTextDimBrush"],
            Child = _glyph,
        };
        _label = new TextBlock
        {
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["LumenTextDimBrush"],
        };
        Children.Add(badge);
        Children.Add(_label);
    }

    public string Glyph
    {
        get => _glyph.Text;
        set => _glyph.Text = value;
    }

    public string Label
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    public static ButtonHint Create(string glyph, string label) => new() { Glyph = glyph, Label = label };
}
