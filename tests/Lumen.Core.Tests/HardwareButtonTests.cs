using Lumen.Core.Input;

namespace Lumen.Core.Tests;

public class HardwareButtonTests
{
    [Fact]
    public void Bindings_round_trip_and_reject_garbage()
    {
        var kb = new ButtonBinding { Kind = ButtonSourceKind.Keyboard, Modifiers = KeyModifiers.Win | KeyModifiers.Control, VirtualKey = 0x7B };
        Assert.Equal(kb, ButtonBinding.Parse(kb.Serialize()));
        Assert.Equal("Win+Ctrl+F12", kb.Describe());

        var hid = new ButtonBinding { Kind = ButtonSourceKind.Hid, VendorId = 0x1A86, ProductId = 0xFE00, UsagePage = 0xFF00, Usage = 1, ByteIndex = 3, Mask = 0x04, PressedValue = 0x04 };
        Assert.Equal(hid, ButtonBinding.Parse(hid.Serialize()));

        var pad = new ButtonBinding { Kind = ButtonSourceKind.Controller, Buttons = GamepadButtons.View | GamepadButtons.Menu };
        Assert.Equal(pad, ButtonBinding.Parse(pad.Serialize()));
        Assert.Equal("Controller Menu+View", pad.Describe());

        foreach (var bad in new[] { null, "", "kb:2", "kb:2:999", "hid:1:2:3", "pad:0", "x:1", "kb:a:b" })
            Assert.Null(ButtonBinding.Parse(bad));
        Assert.Equal("Ctrl+Alt+Q", ButtonBinding.DefaultKeyboard.Describe());
    }

    [Fact]
    public void Key_chords_from_raw_events()
    {
        var t = new KeyChordTracker();
        Assert.Null(t.Feed(0xA2, true));                  // LCtrl
        Assert.Null(t.Feed(0x5B, true));                  // LWin
        Assert.Equal((KeyModifiers.Control | KeyModifiers.Win, 0x7B), t.Feed(0x7B, true));
        Assert.Null(t.Feed(0x7B, true));                  // auto-repeat
        Assert.Null(t.Feed(0x7B, false));
        Assert.Null(t.Feed(0xA2, false));
        Assert.Equal((KeyModifiers.Win, 0x44), t.Feed(0x44, true));
    }

    [Fact]
    public void Hid_learning_and_matching()
    {
        byte[] idle = [0x07, 0x00, 0x10, 0x00];
        byte[] down = [0x07, 0x00, 0x14, 0x00];
        var diff = HidButtonLearner.Diff(idle, down)!.Value;
        Assert.Equal((2, 0x04, 0x04), diff);
        Assert.Null(HidButtonLearner.Diff(idle, idle));
        Assert.Null(HidButtonLearner.Diff(idle, [0x08, 0x00, 0x14, 0x00])); // other report id
        Assert.Null(HidButtonLearner.Diff(idle, [0x07, 0x00]));

        var m = new HidButtonMatcher(new ButtonBinding { Kind = ButtonSourceKind.Hid, ByteIndex = 2, Mask = 0x04, PressedValue = 0x04 });
        Assert.False(m.Feed(down));   // first report = baseline, no edge
        Assert.False(m.Feed(idle));
        Assert.True(m.Feed(down));
        Assert.False(m.Feed(down));   // held
        Assert.False(m.Feed([0x07, 0x00, 0x15, 0x00])); // other bit changed, still held
        Assert.False(m.Feed(idle));
        Assert.True(m.Feed(down));
        Assert.False(m.Feed([0x07])); // short report ignored
    }

    [Fact]
    public void Controller_chord_needs_release_first_and_debounce()
    {
        var chord = new ControllerChord(GamepadButtons.View | GamepadButtons.Menu);
        GamepadState S(GamepadButtons b) => new(true, b, 0, 0, 0, 0, 0, 0);
        Assert.False(chord.Feed(S(GamepadButtons.View | GamepadButtons.Menu))); // held while binding
        Assert.False(chord.Feed(S(GamepadButtons.None)));
        Assert.False(chord.Feed(S(GamepadButtons.View)));
        Assert.True(chord.Feed(S(GamepadButtons.View | GamepadButtons.Menu | GamepadButtons.A)));
        Assert.False(chord.Feed(S(GamepadButtons.View | GamepadButtons.Menu)));

        var d = new ToggleDebouncer(TimeSpan.FromMilliseconds(250));
        Assert.True(d.Accept(TimeSpan.FromMilliseconds(1000)));
        Assert.False(d.Accept(TimeSpan.FromMilliseconds(1100)));
        Assert.True(d.Accept(TimeSpan.FromMilliseconds(1300)));
    }
}

public class OneXVendorHidTests
{
    private static byte[] Frame(byte id, byte state, bool withReportId)
    {
        var f = new byte[64];
        f[0] = 0xB2; f[1] = 0x3F; f[2] = 0x01; f[6] = id; f[12] = state; f[62] = 0x3F; f[63] = 0xB2;
        return withReportId ? [0x00, .. f] : f;
    }

    [Fact]
    public void Parses_press_and_release_with_and_without_report_id()
    {
        Assert.Equal((0x24, true), OneXVendorHid.TryParse(Frame(0x24, 0x01, true)));
        Assert.Equal((0x24, false), OneXVendorHid.TryParse(Frame(0x24, 0x02, true)));
        Assert.Equal((0x20, true), OneXVendorHid.TryParse(Frame(0x20, 0x01, false)));
        Assert.Null(OneXVendorHid.TryParse(Frame(0x24, 0x07, true)));        // unknown state
        var status = Frame(0x24, 0x01, true); status[1] = 0xB8;                // status frame
        Assert.Null(OneXVendorHid.TryParse(status));
        Assert.Null(OneXVendorHid.TryParse(new byte[5]));
        Assert.True(OneXVendorHid.IsVendorInterface(0x1A86, 0xFE00, 0xFF00, 1));
        Assert.Equal("OneX (home)", OneXVendorHid.Name(0x24));
    }

    [Fact]
    public void OneX_binding_round_trip()
    {
        var b = new ButtonBinding { Kind = ButtonSourceKind.OneX, ButtonId = 0x24 };
        Assert.Equal("onex:36", b.Serialize());
        Assert.Equal(b, ButtonBinding.Parse("onex:36"));
        Assert.Equal("OneXPlayer OneX (home) button", b.Describe());
        Assert.Null(ButtonBinding.Parse("onex:0"));
    }
}
