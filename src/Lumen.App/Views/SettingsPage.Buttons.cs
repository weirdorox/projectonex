using Lumen.App.Services;
using Lumen.Core.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lumen.App.Views;

/// <summary>Settings › Quick settings button: detection (diagnostics) and binding.</summary>
public sealed partial class SettingsPage
{
    private DispatcherQueueTimer? _detectTimer;
    private int _detectSecondsLeft;
    private int _detectEvents;

    private void RenderButtons()
    {
        var svc = Shell.Buttons;
        ButtonStatusText.Text = "Hardware button: " + (svc?.ButtonStatus ?? "starting…");
        ShortcutStatusText.Text = "Keyboard shortcut: " + (svc?.ShortcutStatus ?? "starting…");
        QsFocusToggle.IsOn = Host.Settings.QuickSettingsTakesFocus;
        DetectButton.IsEnabled = svc is not null;
    }

    private void OnDetectClick(object sender, RoutedEventArgs e)
    {
        if (Shell.Buttons is not { } svc) return;
        if (_detectTimer?.IsRunning == true)
        {
            StopDetect();
            return;
        }
        DetectList.Children.Clear();
        _detectEvents = 0;
        _detectSecondsLeft = 15;
        svc.Learned += OnLearned;
        svc.StartLearning();
        DetectButton.Content = "Stop";
        DetectHint.Visibility = Visibility.Visible;
        UpdateDetectHint();
        _detectTimer ??= CreateDetectTimer();
        _detectTimer.Start();
    }

    private DispatcherQueueTimer CreateDetectTimer()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(1);
        t.Tick += (_, _) =>
        {
            if (--_detectSecondsLeft <= 0) StopDetect();
            else UpdateDetectHint();
        };
        return t;
    }

    private void UpdateDetectHint() =>
        DetectHint.Text = $"Press the button now ({_detectSecondsLeft} s). Everything Windows receives is listed below; nothing is triggered while detecting.";

    private void StopDetect()
    {
        _detectTimer?.Stop();
        if (Shell.Buttons is { } svc)
        {
            svc.Learned -= OnLearned;
            svc.StopLearning();
        }
        DetectButton.Content = "Detect a button";
        DetectHint.Text = _detectEvents == 0
            ? "Windows received no input from the button. It is probably handled by the firmware or OneXConsole only. In OneXConsole, map the button to a key combination (e.g. Ctrl+Alt+F12) and detect again - or use the keyboard shortcut / View + Menu."
            : "Detection finished. Choose an entry above.";
        RenderButtons();
    }

    private void OnLearned(ButtonEvent e)
    {
        _detectEvents++;
        if (DetectList.Children.Count >= 14) DetectList.Children.RemoveAt(DetectList.Children.Count - 1);

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = $"{e.At:HH:mm:ss}  [{e.Kind}]  {e.Details}", TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 13 });
        text.Children.Add(new TextBlock { Text = $"{e.Device} · Windows received it: yes · {e.Availability}", Style = (Style)Application.Current.Resources["LumenCaptionText"], TextWrapping = TextWrapping.Wrap });

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(text);
        if (e.Binding is { } binding)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var use = new Button { Content = "Use as Quick settings button" };
            use.Click += (_, _) =>
            {
                StopDetect();
                Shell.Buttons?.UseAsButton(binding);
                RenderButtons();
                Shell.ShowToast($"Quick settings button: {binding.Describe()}");
            };
            actions.Children.Add(use);
            if (binding.Kind == ButtonSourceKind.Keyboard && binding.Modifiers != KeyModifiers.None)
            {
                var shortcut = new Button { Content = "Use as keyboard shortcut" };
                shortcut.Click += (_, _) =>
                {
                    StopDetect();
                    var error = Shell.Buttons?.UseAsShortcut(binding);
                    RenderButtons();
                    Shell.ShowToast(error ?? $"Keyboard shortcut: {binding.Describe()}");
                };
                actions.Children.Add(shortcut);
            }
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
        }
        DetectList.Children.Insert(0, new Border { Style = (Style)Application.Current.Resources["LumenCardStyle"], Padding = new Thickness(12), Child = row });
    }

    private void OnRemoveButtonClick(object sender, RoutedEventArgs e)
    {
        Shell.Buttons?.ClearButton();
        RenderButtons();
    }

    private void OnResetShortcutClick(object sender, RoutedEventArgs e)
    {
        Shell.Buttons?.UseAsShortcut(ButtonBinding.DefaultKeyboard);
        RenderButtons();
    }

    private void OnQsFocusToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Host.Settings.QuickSettingsTakesFocus = QsFocusToggle.IsOn;
    }
}
