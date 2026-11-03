using Lumen.App.Input;
using Lumen.App.Services;
using Lumen.Core.Diagnostics;
using Lumen.Core.Personalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Lumen.App.Views;

/// <summary>Settings › Personalization: accent, OLED black, sounds and the Home dashboard rows.</summary>
public sealed partial class SettingsPage
{
    private void RenderPersonalization()
    {
        var s = Host.Settings;
        AccentPanel.Children.Clear();
        foreach (var accent in Themes.Accents)
        {
            var c = accent.Color;
            var b = new Button
            {
                Style = (Style)Application.Current.Resources["LumenPillButtonStyle"],
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B)) },
                        new TextBlock { Text = accent.Name },
                    },
                },
                Tag = accent.Id,
            };
            if (string.Equals(s.AccentId, accent.Id, StringComparison.OrdinalIgnoreCase))
                b.Background = (Brush)Application.Current.Resources["LumenAccentSoftBrush"];
            var id = accent.Id;
            b.Click += (_, _) =>
            {
                Host.Settings.AccentId = id;
                ThemeService.Apply(Host.Settings);
                RenderPersonalization();
                FocusNavigation.Focus(AccentPanel.Children.OfType<Button>().FirstOrDefault(x => Equals(x.Tag, id)) ?? (DependencyObject)AccentPanel);
            };
            AccentPanel.Children.Add(b);
        }

        OledToggle.IsOn = s.OledTheme;
        SoundsToggle.IsOn = s.UiSounds;
        HomeRecentToggle.IsOn = s.HomeShowRecent;
        HomeFavoritesToggle.IsOn = s.HomeShowFavorites;
        HomeNewToggle.IsOn = s.HomeShowNew;
        _ = RenderPinnedAsync();
    }

    private async Task RenderPinnedAsync()
    {
        try
        {
            var collections = await Host.Collections.GetAllAsync();
            PinnedPanel.Children.Clear();
            AddPinnedOption(null, "None");
            foreach (var c in collections) AddPinnedOption(c.Id, c.Name);
            if (collections.Count == 0)
                PinnedPanel.Children.Add(new TextBlock { Text = "Create collections in the Collections tab.", Style = (Style)Application.Current.Resources["LumenCaptionText"], VerticalAlignment = VerticalAlignment.Center });
        }
        catch (Exception ex)
        {
            Log.Warn("Loading collections for Home failed.", ex);
        }
    }

    private void AddPinnedOption(string? id, string label)
    {
        var b = new Button { Content = label, Style = (Style)Application.Current.Resources["LumenPillButtonStyle"], Tag = id ?? "" };
        if (Host.Settings.HomePinnedCollection == id)
        {
            b.Background = (Brush)Application.Current.Resources["LumenAccentSoftBrush"];
            b.Foreground = (Brush)Application.Current.Resources["LumenAccentBrush"];
        }
        b.Click += async (_, _) =>
        {
            Host.Settings.HomePinnedCollection = id;
            await RenderPinnedAsync();
            FocusNavigation.Focus(PinnedPanel.Children.OfType<Button>().FirstOrDefault(x => Equals(x.Tag, id ?? "")) ?? (DependencyObject)PinnedPanel);
        };
        PinnedPanel.Children.Add(b);
    }

    private void OnOledToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Host.Settings.OledTheme = OledToggle.IsOn;
        ThemeService.Apply(Host.Settings);
    }

    private void OnSoundsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Host.Settings.UiSounds = SoundsToggle.IsOn;
        ThemeService.Apply(Host.Settings);
    }

    private void OnHomeRowToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Host.Settings.HomeShowRecent = HomeRecentToggle.IsOn;
        Host.Settings.HomeShowFavorites = HomeFavoritesToggle.IsOn;
        Host.Settings.HomeShowNew = HomeNewToggle.IsOn;
    }
}
