using Lumen.Core.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Lumen.App.Input;

/// <summary>
/// Controller-driven focus. WinUI 3 desktop apps do not get built-in gamepad navigation, so the
/// XY-focus engine is driven explicitly (FocusManager.TryMoveFocus with a search root) and "A" is
/// mapped to the focused control's UI Automation Invoke/Toggle pattern - the same thing a click does.
/// </summary>
public static class FocusNavigation
{
    public static DependencyObject? Focused(XamlRoot? root) =>
        root is null ? null : FocusManager.GetFocusedElement(root) as DependencyObject;

    public static bool Move(XamlRoot? root, NavAction action)
    {
        if (root?.Content is null) return false;

        var focused = Focused(root);

        // Sliders consume left/right to change their value.
        if (focused is Slider slider && action is NavAction.Left or NavAction.Right)
        {
            var step = slider.StepFrequency > 0 ? slider.StepFrequency : Math.Max(1, (slider.Maximum - slider.Minimum) / 20);
            slider.Value = Math.Clamp(slider.Value + (action == NavAction.Right ? step : -step), slider.Minimum, slider.Maximum);
            return true;
        }

        var direction = action switch
        {
            NavAction.Up => FocusNavigationDirection.Up,
            NavAction.Down => FocusNavigationDirection.Down,
            NavAction.Left => FocusNavigationDirection.Left,
            NavAction.Right => FocusNavigationDirection.Right,
            _ => FocusNavigationDirection.None,
        };
        if (direction == FocusNavigationDirection.None) return false;

        if (focused is null)
        {
            // Nothing focused yet (e.g. after a touch tap): start from the first focusable element.
            var first = FocusManager.FindFirstFocusableElement(root.Content);
            return first is not null && Focus(first);
        }

        var options = new FindNextElementOptions
        {
            SearchRoot = root.Content,
            XYFocusNavigationStrategyOverride = XYFocusNavigationStrategyOverride.Projection,
        };
        return FocusManager.TryMoveFocus(direction, options);
    }

    /// <summary>Activates the focused control. Returns false if nothing actionable is focused.</summary>
    public static bool InvokeFocused(XamlRoot? root)
    {
        var focused = Focused(root);
        switch (focused)
        {
            case ToggleSwitch ts:
                ts.IsOn = !ts.IsOn;
                return true;
            case ComboBox cb:
                cb.IsDropDownOpen = !cb.IsDropDownOpen;
                return true;
            case UIElement element:
                var peer = FrameworkElementAutomationPeer.FromElement(element)
                           ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
                if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
                {
                    invoke.Invoke();
                    return true;
                }
                if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
                {
                    toggle.Toggle();
                    return true;
                }
                if (peer?.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider select)
                {
                    select.Select();
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>Focus with keyboard semantics so the focus ring is visible.</summary>
    public static bool Focus(DependencyObject element) =>
        element is Control c ? c.Focus(FocusState.Keyboard) :
        element is UIElement u && u.Focus(FocusState.Keyboard);

    /// <summary>Scrolls the nearest ScrollViewer by roughly one screen.</summary>
    public static void Scroll(ScrollViewer? viewer, bool down)
    {
        if (viewer is null) return;
        var delta = viewer.ViewportHeight * 0.6 * (down ? 1 : -1);
        viewer.ChangeView(null, Math.Max(0, viewer.VerticalOffset + delta), null);
    }
}
