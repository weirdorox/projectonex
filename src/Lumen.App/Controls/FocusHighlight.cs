using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Lumen.App;

/// <summary>
/// Draws Lumen's own focus ring inside a window that never becomes the active window (Quick
/// settings over a game): Windows only shows the system focus visual in the active window, so
/// controller navigation would otherwise be invisible.
/// </summary>
public static class FocusHighlight
{
    private sealed record Saved(Brush? Brush, Thickness Thickness);
    private static readonly ConditionalWeakTable<Control, Saved> SavedLook = new();
    private static readonly List<WeakReference<FrameworkElement>> Roots = [];

    public static void Attach(FrameworkElement root)
    {
        if (Roots.Count == 0)
        {
            FocusManager.GotFocus += OnGotFocus;
            FocusManager.LostFocus += OnLostFocus;
        }
        Roots.Add(new WeakReference<FrameworkElement>(root));
    }

    private static bool InRoot(DependencyObject? element)
    {
        if (element is not UIElement e || e.XamlRoot is null) return false;
        foreach (var w in Roots)
            if (w.TryGetTarget(out var r) && r.XamlRoot == e.XamlRoot) return true;
        return false;
    }

    private static void OnGotFocus(object? sender, FocusManagerGotFocusEventArgs e)
    {
        if (e.NewFocusedElement is not Control c || !InRoot(c)) return;
        if (!SavedLook.TryGetValue(c, out _)) SavedLook.Add(c, new Saved(c.BorderBrush, c.BorderThickness));
        if (Application.Current.Resources.TryGetValue("LumenAccentBrush", out var accent) && accent is Brush brush)
        {
            c.BorderBrush = brush;
            c.BorderThickness = new Thickness(2);
        }
    }

    private static void OnLostFocus(object? sender, FocusManagerLostFocusEventArgs e)
    {
        if (e.OldFocusedElement is not Control c || !SavedLook.TryGetValue(c, out var saved)) return;
        c.BorderBrush = saved.Brush;
        c.BorderThickness = saved.Thickness;
        SavedLook.Remove(c);
    }
}
