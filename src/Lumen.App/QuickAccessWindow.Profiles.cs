using Lumen.Core.Diagnostics;
using Lumen.Core.Profiles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lumen.App;

/// <summary>Per-game profile controls in the overlay's Game section.</summary>
public sealed partial class QuickAccessWindow
{
    partial void RenderProfile() => _ = RenderProfileAsync();

    private async Task RenderProfileAsync()
    {
        ProfilePanel.Children.Clear();
        var game = _targetGame;
        var sessions = _main.GameSessions;
        if (game is null || sessions is null) return;

        var adapter = await sessions.EnsureEngineAsync();
        var profile = await sessions.GetProfileAsync(game.Id);
        if (_targetGame != game) return; // panel re-targeted meanwhile

        ProfilePanel.Children.Add(new TextBlock
        {
            Text = "Performance profile",
            Style = (Style)Application.Current.Resources["LumenBodyText"],
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        ProfilePanel.Children.Add(new TextBlock
        {
            Text = Describe(profile, adapter),
            Style = (Style)Application.Current.Resources["LumenCaptionText"],
        });

        var save = new Button { Content = profile is null ? "Save current settings for this game" : "Update profile with current settings", HorizontalAlignment = HorizontalAlignment.Stretch };
        save.Click += async (_, _) =>
        {
            try
            {
                await sessions.SaveCurrentAsync(game.Id);
                _main.ShowToast($"Profile saved for {game.Title}");
                await RenderProfileAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Saving profile failed.", ex);
            }
        };
        ProfilePanel.Children.Add(save);

        var edit = new Button { Content = "Edit profile…", HorizontalAlignment = HorizontalAlignment.Stretch };
        edit.Click += (_, _) =>
        {
            _main.ReturnToShell();
            _main.ShowGameDetails(game.Id);
        };
        ProfilePanel.Children.Add(edit);

        if (profile is not null)
        {
            var remove = new Button { Content = "Remove profile", HorizontalAlignment = HorizontalAlignment.Stretch };
            remove.Click += async (_, _) =>
            {
                await sessions.DeleteProfileAsync(game.Id);
                _main.ShowToast($"Profile removed for {game.Title}");
                await RenderProfileAsync();
            };
            ProfilePanel.Children.Add(remove);
        }
    }

    internal static string Describe(PerformanceProfile? profile, Services.HardwareProfileAdapter? adapter)
    {
        if (profile is null) return "No profile - this game uses your current settings.";
        var parts = new List<string>();
        if (profile.RefreshHz is { } hz) parts.Add($"{hz} Hz");
        if (profile.PowerPlanId is { } plan)
        {
            var name = adapter?.Services.PowerPlans?.GetPlans().FirstOrDefault(p => p.Id == plan)?.Name;
            parts.Add(name ?? "custom power plan");
        }
        if (profile.BrightnessPercent is { } br) parts.Add($"brightness {br}%");
        if (profile.TdpWatts is { } w && adapter?.Supports(ProfileField.Tdp) != true) parts.Add($"TDP {w} W (not supported here)");
        return "Applied automatically while it runs: " + string.Join(" · ", parts) + ". Your settings come back when the game closes.";
    }
}
