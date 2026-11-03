using Lumen.Core.Input;

namespace Lumen.Core.Tests;

public class InputMapperTests
{
    private static GamepadState Pad(GamepadButtons b = GamepadButtons.None, float lx = 0, float ly = 0, float ry = 0, float lt = 0, float rt = 0) =>
        new(true, b, lx, ly, 0, ry, lt, rt);

    private static void Is(IReadOnlyList<NavAction> actual, params NavAction[] expected) =>
        Assert.Equal(expected, actual.ToArray());

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>A primed mapper: the first update only records state.</summary>
    private static InputMapper Primed()
    {
        var m = new InputMapper();
        Assert.Empty(m.Update(Pad(), TimeSpan.Zero));
        return m;
    }

    [Fact]
    public void First_update_after_reset_never_fires()
    {
        var m = new InputMapper();
        Assert.Empty(m.Update(Pad(GamepadButtons.A), Ms(0)));
        Assert.Empty(m.Update(Pad(GamepadButtons.A), Ms(16)));
        Assert.Empty(m.Update(Pad(), Ms(32)));
        Is(m.Update(Pad(GamepadButtons.A), Ms(48)), NavAction.Accept);
    }

    [Fact]
    public void Buttons_fire_on_press_edge_only()
    {
        var m = Primed();
        Is(m.Update(Pad(GamepadButtons.A), Ms(10)), NavAction.Accept);
        Assert.Empty(m.Update(Pad(GamepadButtons.A), Ms(20)));
        Assert.Empty(m.Update(Pad(), Ms(30)));
        Is(m.Update(Pad(GamepadButtons.B), Ms(40)), NavAction.Back);
        Is(m.Update(Pad(GamepadButtons.RightShoulder), Ms(50)), NavAction.NextTab);
    }

    [Fact]
    public void Dpad_repeats_after_initial_delay_then_accelerates()
    {
        var m = Primed();
        var fired = new List<int>();
        for (var t = 1; t <= 2000; t += 5)
        {
            if (m.Update(Pad(GamepadButtons.DPadDown), Ms(t)).Contains(NavAction.Down)) fired.Add(t);
        }
        Assert.Equal(1, fired[0]);                    // immediate
        Assert.InRange(fired[1], 400, 410);           // initial delay
        Assert.InRange(fired[2] - fired[1], 110, 115); // normal repeat
        var lastGap = fired[^1] - fired[^2];
        Assert.InRange(lastGap, 55, 60);              // fast repeat
    }

    [Fact]
    public void Stick_uses_dominant_axis_with_hysteresis()
    {
        var m = Primed();
        Is(m.Update(Pad(lx: 0.8f, ly: 0.3f), Ms(10)), NavAction.Right);
        // drops to 0.4: still above release threshold, no new event, still held
        Assert.Empty(m.Update(Pad(lx: 0.4f), Ms(20)));
        // below release -> released; pressing again fires again
        Assert.Empty(m.Update(Pad(lx: 0.2f), Ms(30)));
        Is(m.Update(Pad(lx: 0.9f), Ms(40)), NavAction.Right);
        Is(m.Update(Pad(ly: 0.9f), Ms(50)), NavAction.Up);
    }

    [Fact]
    public void Dpad_overrides_stick()
    {
        var m = Primed();
        Is(m.Update(Pad(GamepadButtons.DPadLeft, lx: 0.9f), Ms(10)), NavAction.Left);
    }

    [Fact]
    public void View_and_menu_fire_on_release_without_chord()
    {
        var m = Primed();
        Assert.Empty(m.Update(Pad(GamepadButtons.Menu), Ms(10)));
        Is(m.Update(Pad(), Ms(20)), NavAction.Menu);
        Assert.Empty(m.Update(Pad(GamepadButtons.View), Ms(30)));
        Is(m.Update(Pad(), Ms(40)), NavAction.View);
    }

    [Fact]
    public void View_plus_menu_opens_quick_access_once_and_suppresses_singles()
    {
        var m = Primed();
        Assert.Empty(m.Update(Pad(GamepadButtons.View), Ms(10)));
        Is(m.Update(Pad(GamepadButtons.View | GamepadButtons.Menu), Ms(20)), NavAction.QuickAccess);
        Assert.Empty(m.Update(Pad(GamepadButtons.View | GamepadButtons.Menu), Ms(30)));
        Assert.Empty(m.Update(Pad(GamepadButtons.Menu), Ms(40)));
        Assert.Empty(m.Update(Pad(), Ms(50)));
        // afterwards singles work again
        Assert.Empty(m.Update(Pad(GamepadButtons.Menu), Ms(60)));
        Is(m.Update(Pad(), Ms(70)), NavAction.Menu);
    }

    [Fact]
    public void Emergency_chord_requires_hold()
    {
        var m = Primed();
        var combo = GamepadButtons.View | GamepadButtons.Menu | GamepadButtons.LeftShoulder | GamepadButtons.RightShoulder;
        var all = new List<NavAction>();
        for (var t = 10; t <= 3500; t += 10) all.AddRange(m.Update(Pad(combo), Ms(t)));
        Assert.Single(all, NavAction.EmergencyDesktop);
        Assert.DoesNotContain(NavAction.PreviousTab, all); // shoulders suppressed while modifiers held
        Assert.DoesNotContain(NavAction.NextTab, all);
    }

    [Fact]
    public void Triggers_use_thresholds()
    {
        var m = Primed();
        Assert.Empty(m.Update(Pad(rt: 0.4f), Ms(10)));
        Is(m.Update(Pad(rt: 0.8f), Ms(20)), NavAction.NextSection);
        Assert.Empty(m.Update(Pad(rt: 0.5f), Ms(30)));  // between thresholds - still down
        Assert.Empty(m.Update(Pad(rt: 0.1f), Ms(40)));
        Is(m.Update(Pad(lt: 1f), Ms(50)), NavAction.PreviousSection);
    }

    [Fact]
    public void Disconnect_resets_and_held_buttons_do_not_fire_on_reconnect()
    {
        var m = Primed();
        Is(m.Update(Pad(GamepadButtons.A), Ms(10)), NavAction.Accept);
        Assert.Empty(m.Update(GamepadState.Disconnected, Ms(20)));
        Assert.Empty(m.Update(Pad(GamepadButtons.A), Ms(30)));  // primes
        Assert.Empty(m.Update(Pad(GamepadButtons.A), Ms(40)));
    }

    [Fact]
    public void Right_stick_scrolls()
    {
        var m = Primed();
        Is(m.Update(Pad(ry: -0.9f), Ms(10)), NavAction.ScrollDown);
    }

    [Fact]
    public void Merge_combines_detachable_halves()
    {
        var left = new GamepadState(true, GamepadButtons.DPadUp, -0.5f, 0.9f, 0, 0, 0.7f, 0);
        var right = new GamepadState(true, GamepadButtons.A, 0, 0, 0.3f, -0.2f, 0, 1f);
        var merged = GamepadState.Merge([left, right, GamepadState.Disconnected]);
        Assert.True(merged.IsConnected);
        Assert.True(merged.IsDown(GamepadButtons.DPadUp | GamepadButtons.A));
        Assert.Equal(0.9f, merged.LeftY);
        Assert.Equal(0.3f, merged.RightX);
        Assert.Equal(0.7f, merged.LeftTrigger);
        Assert.Equal(1f, merged.RightTrigger);
        Assert.False(GamepadState.Merge([]).IsConnected);
    }
}
