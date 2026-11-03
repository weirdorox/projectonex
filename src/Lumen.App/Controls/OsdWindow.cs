using System.Runtime.InteropServices;
using Lumen.Core.Diagnostics;
using Lumen.Core.Performance;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;

namespace Lumen.App;

/// <summary>
/// The performance overlay: a small topmost, never-activated, click-through window in the top-left
/// corner. It is a normal desktop window - nothing is injected into games - so it appears over
/// borderless/windowed games and those using independent flip; Windows does not allow any normal
/// window over true exclusive fullscreen.
/// </summary>
public sealed class OsdWindow : Window
{
    private const int WidthDip = 230;
    private const int GraphHeightDip = 46;
    private readonly IntPtr _hwnd;
    private readonly StackPanel _lines = new() { Spacing = 1 };
    private readonly Polyline _graph = new() { StrokeThickness = 1.5, Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 120, 200, 255)) };
    private readonly Line _target = new() { StrokeThickness = 1, Stroke = new SolidColorBrush(ColorHelper.FromArgb(90, 255, 255, 255)), StrokeDashArray = new DoubleCollection { 3, 3 } };
    private readonly Canvas _graphCanvas;
    private bool _visible;
    private bool _closing;
    private int _lastHeight;

    public OsdWindow(bool clickThrough)
    {
        _graphCanvas = new Canvas { Width = WidthDip - 20, Height = GraphHeightDip, Visibility = Visibility.Collapsed };
        _graphCanvas.Children.Add(_target);
        _graphCanvas.Children.Add(_graph);
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(_lines);
        panel.Children.Add(_graphCanvas);
        Content = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 12, 14, 18)),
            Padding = new Thickness(10, 8, 10, 8),
            Child = panel,
            RequestedTheme = ElementTheme.Dark,
        };

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "Lumen performance overlay";
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.SetBorderAndTitleBar(false, false);
        }
        AppWindow.Closing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;
            HideOsd();
        };

        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        if (clickThrough) ex |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
        if (clickThrough) SetLayeredWindowAttributes(_hwnd, 0, 225, LWA_ALPHA);
        Log.Info($"Overlay window created (click-through {clickThrough}).");
    }

    private double Scale
    {
        get
        {
            var dpi = GetDpiForWindow(_hwnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
    }

    public void Render(IReadOnlyList<OverlayLine> lines, IReadOnlyList<double>? frameTimes)
    {
        _lines.Children.Clear();
        foreach (var line in lines)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(line.Label.Length == 0 ? 0 : 74) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dim = new SolidColorBrush(ColorHelper.FromArgb(255, 150, 158, 170));
            row.Children.Add(new TextBlock { Text = line.Label, FontSize = 13, Foreground = dim });
            var value = new TextBlock
            {
                Text = line.Value,
                FontSize = line.Label == "FPS" ? 20 : 13,
                FontWeight = line.Label == "FPS" ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = line.Dim ? dim : new SolidColorBrush(Colors.White),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            _lines.Children.Add(row);
        }

        if (frameTimes is { Count: > 1 })
        {
            _graphCanvas.Visibility = Visibility.Visible;
            var max = Math.Max(33.4, frameTimes.Max() * 1.1);
            var w = _graphCanvas.Width;
            var h = _graphCanvas.Height;
            var points = new PointCollection();
            for (var i = 0; i < frameTimes.Count; i++)
                points.Add(new Point(i * w / (frameTimes.Count - 1), h - Math.Min(h, frameTimes[i] / max * h)));
            _graph.Points = points;
            var y16 = h - 16.7 / max * h; // 60 FPS line
            _target.X1 = 0; _target.X2 = w; _target.Y1 = y16; _target.Y2 = y16;
        }
        else
        {
            _graphCanvas.Visibility = Visibility.Collapsed;
        }

        // Size to content (DIPs -> physical pixels), top-left of the primary display's work area.
        var heightDip = 16 + lines.Count * (lines.Count > 0 && lines[0].Label == "FPS" ? 21 : 19) + 8 + (frameTimes is { Count: > 1 } ? GraphHeightDip + 6 : 0)
                        + lines.Count(l => l.Value.Length > 24) * 18;
        var scale = Scale;
        var height = (int)Math.Ceiling(heightDip * scale);
        if (height != _lastHeight || !_visible)
        {
            var area = DisplayArea.Primary.WorkArea;
            AppWindow.MoveAndResize(new RectInt32(area.X + (int)(16 * scale), area.Y + (int)(16 * scale), (int)(WidthDip * scale), height));
            _lastHeight = height;
        }
        if (!_visible)
        {
            AppWindow.Show(false); // never take focus from the game
            _visible = true;
        }
    }

    public void HideOsd()
    {
        if (!_visible) return;
        AppWindow.Hide();
        _visible = false;
    }

    public void CloseForGood()
    {
        _closing = true;
        Close();
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private const uint LWA_ALPHA = 0x2;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
