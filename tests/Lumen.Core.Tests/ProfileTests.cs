using Lumen.Core.Profiles;

namespace Lumen.Core.Tests;

internal sealed class FakeProfileHardware : IProfileHardware
{
    public int Hz = 144;
    public Guid Plan = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    public int Brightness = 70;
    public HashSet<ProfileField> Supported = [ProfileField.RefreshRate, ProfileField.PowerPlan, ProfileField.Brightness];
    public HashSet<int> RejectedRates = [];
    public int Writes;

    public bool Supports(ProfileField field) => Supported.Contains(field);
    public HardwareSnapshot Capture() => new(Hz, Plan, Brightness, null);

    public bool SetRefreshRate(int hz)
    {
        if (RejectedRates.Contains(hz)) return false;
        Writes++; Hz = hz; return true;
    }

    public bool SetPowerPlan(Guid id) { Writes++; Plan = id; return true; }
    public bool SetBrightness(int percent) { Writes++; Brightness = percent; return true; }
    public bool SetTdp(int watts) => throw new InvalidOperationException("must never be called without a provider");
}

public class ProfileEngineTests
{
    private static readonly Guid Performance = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    [Fact]
    public void Applies_profile_and_restores_baseline_on_end()
    {
        var hw = new FakeProfileHardware();
        var engine = new ProfileEngine(hw);

        var report = engine.BeginSession("steam:1", new PerformanceProfile { GameId = "steam:1", RefreshHz = 60, PowerPlanId = Performance });
        Assert.Equal(FieldOutcome.Applied, report.Fields[ProfileField.RefreshRate]);
        Assert.Equal(FieldOutcome.Applied, report.Fields[ProfileField.PowerPlan]);
        Assert.Equal(60, hw.Hz);
        Assert.Equal(Performance, hw.Plan);
        Assert.Equal(70, hw.Brightness); // untouched

        engine.EndSession();
        Assert.Equal(144, hw.Hz);
        Assert.NotEqual(Performance, hw.Plan);
        Assert.Null(engine.SessionGameId);
    }

    [Fact]
    public void Unsupported_fields_are_reported_and_never_called()
    {
        var hw = new FakeProfileHardware();
        var engine = new ProfileEngine(hw);
        var report = engine.BeginSession("g", new PerformanceProfile { GameId = "g", TdpWatts = 15, RefreshHz = 90 });
        Assert.Equal(FieldOutcome.Unsupported, report.Fields[ProfileField.Tdp]);
        Assert.Contains(ProfileField.Tdp, report.Unsupported);
        Assert.Equal(90, hw.Hz);
    }

    [Fact]
    public void Values_already_in_place_are_not_rewritten()
    {
        var hw = new FakeProfileHardware();
        var engine = new ProfileEngine(hw);
        var report = engine.BeginSession("g", new PerformanceProfile { GameId = "g", RefreshHz = 144 });
        Assert.Equal(FieldOutcome.Unchanged, report.Fields[ProfileField.RefreshRate]);
        Assert.Equal(0, hw.Writes);
    }

    [Fact]
    public void Rejected_values_report_failure_and_leave_state()
    {
        var hw = new FakeProfileHardware { RejectedRates = [40] };
        var engine = new ProfileEngine(hw);
        var report = engine.BeginSession("g", new PerformanceProfile { GameId = "g", RefreshHz = 40 });
        Assert.Contains(ProfileField.RefreshRate, report.Failed);
        Assert.Equal(144, hw.Hz);
        engine.EndSession();
        Assert.Equal(0, hw.Writes);
    }

    [Fact]
    public void Session_without_profile_restores_nothing_but_keeps_baseline_for_later_edits()
    {
        var hw = new FakeProfileHardware();
        var engine = new ProfileEngine(hw);
        engine.BeginSession("g", null);

        hw.Brightness = 30; // user changed brightness mid-game, then saved a profile
        engine.UpdateProfile(new PerformanceProfile { GameId = "g", BrightnessPercent = 30, RefreshHz = 60 });
        engine.EndSession();

        Assert.Equal(70, hw.Brightness); // back to the value from before the game
        Assert.Equal(144, hw.Hz);
    }

    [Fact]
    public void Removing_a_field_mid_session_restores_it_immediately()
    {
        var hw = new FakeProfileHardware();
        var engine = new ProfileEngine(hw);
        engine.BeginSession("g", new PerformanceProfile { GameId = "g", RefreshHz = 60, BrightnessPercent = 40 });
        engine.UpdateProfile(new PerformanceProfile { GameId = "g", BrightnessPercent = 40 });
        Assert.Equal(144, hw.Hz);
        Assert.Equal(40, hw.Brightness);
    }

    [Fact]
    public void Reapply_restores_profile_after_driver_reset()
    {
        var hw = new FakeProfileHardware();
        var engine = new ProfileEngine(hw);
        engine.BeginSession("g", new PerformanceProfile { GameId = "g", RefreshHz = 90 });
        hw.Hz = 144; // e.g. reset by the driver during sleep
        var report = engine.Reapply();
        Assert.Equal(FieldOutcome.Applied, report.Fields[ProfileField.RefreshRate]);
        Assert.Equal(90, hw.Hz);
    }

    [Fact]
    public void Switching_games_restores_first_then_applies_second()
    {
        var hw = new FakeProfileHardware();
        var engine = new ProfileEngine(hw);
        engine.BeginSession("a", new PerformanceProfile { GameId = "a", BrightnessPercent = 20 });
        engine.BeginSession("b", new PerformanceProfile { GameId = "b", RefreshHz = 60 });
        Assert.Equal(70, hw.Brightness);
        Assert.Equal(60, hw.Hz);
        engine.EndSession();
        Assert.Equal(144, hw.Hz);
    }

    [Fact]
    public void Known_baseline_is_used_after_a_restart()
    {
        var hw = new FakeProfileHardware { Hz = 60 }; // Lumen restarted while a 60 Hz profile was active
        var engine = new ProfileEngine(hw);
        engine.BeginSession("g", new PerformanceProfile { GameId = "g", RefreshHz = 60 }, new HardwareSnapshot(144, null, null, null));
        engine.EndSession();
        Assert.Equal(144, hw.Hz);
    }

    [Fact]
    public void Profile_helpers()
    {
        Assert.True(new PerformanceProfile { GameId = "x" }.IsEmpty);
        Assert.Equal(new[] { ProfileField.RefreshRate, ProfileField.Brightness },
            new PerformanceProfile { GameId = "x", RefreshHz = 60, BrightnessPercent = 5 }.DefinedFields().ToArray());
    }
}

public class GameSessionPolicyTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Starts_after_activation_delay()
    {
        var p = new GameSessionPolicy();
        Assert.Empty(p.Observe("g", false, S(0)));
        Assert.Empty(p.Observe("g", false, S(1)));
        var e = Assert.Single(p.Observe("g", false, S(2.1)));
        Assert.Equal(new SessionEvent(SessionEventKind.Started, "g"), e);
        Assert.Equal("g", p.ActiveGameId);
    }

    [Fact]
    public void Brief_flash_does_not_start_a_session()
    {
        var p = new GameSessionPolicy();
        p.Observe("g", false, S(0));
        p.Observe(null, false, S(1));
        Assert.Empty(p.Observe("g", false, S(2.5))); // timer restarted
        Assert.Null(p.ActiveGameId);
    }

    [Fact]
    public void Leaving_the_foreground_keeps_the_session_until_the_process_exits()
    {
        var p = new GameSessionPolicy();
        p.Observe("g", false, S(0));
        p.Observe("g", false, S(3));
        Assert.Empty(p.Observe(null, true, S(10)));   // overlay / Lumen / Alt+Tab
        Assert.Equal("g", p.ActiveGameId);
        var e = Assert.Single(p.Observe(null, false, S(20)));
        Assert.Equal(SessionEventKind.Ended, e.Kind);
        Assert.Null(p.ActiveGameId);
    }

    [Fact]
    public void Another_game_held_in_front_switches_sessions()
    {
        var p = new GameSessionPolicy();
        p.Observe("a", false, S(0));
        p.Observe("a", false, S(3));
        p.Observe("b", true, S(4));
        var events = p.Observe("b", true, S(6.5));
        Assert.Equal(new[] { SessionEventKind.Ended, SessionEventKind.Started }, events.Select(x => x.Kind).ToArray());
        Assert.Equal("b", p.ActiveGameId);
    }
}
