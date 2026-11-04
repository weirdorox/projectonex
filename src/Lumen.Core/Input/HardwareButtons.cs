using System.Globalization;
using System.Text;

namespace Lumen.Core.Input;

public enum ButtonSourceKind { Keyboard, Hid, Controller }

[Flags]
public enum KeyModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Win = 8 } // same bits as RegisterHotKey MOD_*

/// <summary>
/// One physical button (or key combination) that toggles Quick settings. Serialised into settings.
/// Keyboard: what OEM buttons often send (e.g. Win+Ctrl+F-key). Hid: a bit in a vendor HID report.
/// Controller: an XInput button chord.
/// </summary>
public sealed record ButtonBinding
{
    public required ButtonSourceKind Kind { get; init; }
    // Keyboard
    public KeyModifiers Modifiers { get; init; }
    public int VirtualKey { get; init; }
    // HID
    public int VendorId { get; init; }
    public int ProductId { get; init; }
    public int UsagePage { get; init; }
    public int Usage { get; init; }
    public int ByteIndex { get; init; }
    public int Mask { get; init; }
    public int PressedValue { get; init; }
    // Controller
    public GamepadButtons Buttons { get; init; }

    public string Describe() => Kind switch
    {
        ButtonSourceKind.Keyboard => KeyNames.Chord(Modifiers, VirtualKey),
        ButtonSourceKind.Hid => $"HID button (VID {VendorId:X4} PID {ProductId:X4}, page {UsagePage:X4}/{Usage:X2}, byte {ByteIndex} mask {Mask:X2})",
        _ => $"Controller {ControllerChord.Describe(Buttons)}",
    };

    public string Serialize() => Kind switch
    {
        ButtonSourceKind.Keyboard => FormattableString.Invariant($"kb:{(int)Modifiers}:{VirtualKey}"),
        ButtonSourceKind.Hid => FormattableString.Invariant($"hid:{VendorId}:{ProductId}:{UsagePage}:{Usage}:{ByteIndex}:{Mask}:{PressedValue}"),
        _ => FormattableString.Invariant($"pad:{(int)Buttons}"),
    };

    public static ButtonBinding? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var p = text.Split(':');
        int I(int i) => int.Parse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture);
        try
        {
            return p[0] switch
            {
                "kb" when p.Length == 3 && I(2) is > 0 and < 256 => new ButtonBinding { Kind = ButtonSourceKind.Keyboard, Modifiers = (KeyModifiers)I(1) & (KeyModifiers)15, VirtualKey = I(2) },
                "hid" when p.Length == 8 && I(6) is > 0 and < 256 => new ButtonBinding
                {
                    Kind = ButtonSourceKind.Hid, VendorId = I(1), ProductId = I(2), UsagePage = I(3), Usage = I(4),
                    ByteIndex = I(5), Mask = I(6), PressedValue = I(7) & I(6),
                },
                "pad" when p.Length == 2 && I(1) != 0 => new ButtonBinding { Kind = ButtonSourceKind.Controller, Buttons = (GamepadButtons)I(1) },
                _ => null,
            };
        }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
    }

    /// <summary>Default fallback: Ctrl+Alt+Q.</summary>
    public static ButtonBinding DefaultKeyboard => new() { Kind = ButtonSourceKind.Keyboard, Modifiers = KeyModifiers.Control | KeyModifiers.Alt, VirtualKey = 0x51 };
}

public static class KeyNames
{
    public static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    public static KeyModifiers ModifierFor(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => KeyModifiers.Shift,
        0x11 or 0xA2 or 0xA3 => KeyModifiers.Control,
        0x12 or 0xA4 or 0xA5 => KeyModifiers.Alt,
        0x5B or 0x5C => KeyModifiers.Win,
        _ => KeyModifiers.None,
    };

    public static string Key(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x1B => "Esc", 0x20 => "Space",
        0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End", 0x24 => "Home",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        0x2C => "PrintScreen", 0x2D => "Insert", 0x2E => "Delete", 0x5D => "Menu key",
        0xAD => "Volume mute", 0xAE => "Volume down", 0xAF => "Volume up",
        0xB0 => "Next track", 0xB1 => "Previous track", 0xB3 => "Play/Pause",
        _ => $"Key 0x{vk:X2}",
    };

    public static string Chord(KeyModifiers m, int vk)
    {
        var sb = new StringBuilder();
        if (m.HasFlag(KeyModifiers.Win)) sb.Append("Win+");
        if (m.HasFlag(KeyModifiers.Control)) sb.Append("Ctrl+");
        if (m.HasFlag(KeyModifiers.Alt)) sb.Append("Alt+");
        if (m.HasFlag(KeyModifiers.Shift)) sb.Append("Shift+");
        return sb.Append(Key(vk)).ToString();
    }
}

/// <summary>Turns raw key down/up events into chords (modifiers held + one key). Pure, tested.</summary>
public sealed class KeyChordTracker
{
    private KeyModifiers _held;
    private readonly HashSet<int> _down = [];

    /// <summary>Returns a chord when a non-modifier key goes down (auto-repeat ignored).</summary>
    public (KeyModifiers Modifiers, int Vk)? Feed(int vk, bool down)
    {
        var mod = KeyNames.ModifierFor(vk);
        if (mod != KeyModifiers.None)
        {
            _held = down ? _held | mod : _held & ~mod;
            return null;
        }
        if (!down)
        {
            _down.Remove(vk);
            return null;
        }
        return _down.Add(vk) ? (_held, vk) : null;
    }

    public void Reset()
    {
        _held = KeyModifiers.None;
        _down.Clear();
    }
}

/// <summary>Learns and matches a button bit inside HID reports. Pure, tested.</summary>
public static class HidButtonLearner
{
    /// <summary>
    /// Compares two consecutive reports of the same device and returns the first changed byte with
    /// its changed bits (the button). Report id (byte 0) changes are ignored.
    /// </summary>
    public static (int ByteIndex, int Mask, int PressedValue)? Diff(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        if (before.Length != after.Length || after.Length < 2 || before[0] != after[0]) return null;
        for (var i = 1; i < after.Length; i++)
        {
            var changed = before[i] ^ after[i];
            if (changed == 0) continue;
            return (i, changed, after[i] & changed);
        }
        return null;
    }
}

/// <summary>Raises once each time the bound HID bit changes into its pressed value.</summary>
public sealed class HidButtonMatcher(ButtonBinding binding)
{
    private int? _last;

    public bool Feed(ReadOnlySpan<byte> report)
    {
        if (binding.ByteIndex >= report.Length) return false;
        var value = report[binding.ByteIndex] & binding.Mask;
        var pressed = value == binding.PressedValue && _last is not null && _last != value;
        if (_last is null && value == binding.PressedValue) pressed = false; // first report: no edge yet
        _last = value;
        return pressed;
    }
}

/// <summary>Controller chord edge detection for a custom XInput binding. Pure, tested.</summary>
public sealed class ControllerChord(GamepadButtons chord)
{
    private bool _wasDown = true; // a chord held while binding must be released first

    public bool Feed(GamepadState s)
    {
        var down = s.IsConnected && (s.Buttons & chord) == chord;
        var edge = down && !_wasDown;
        _wasDown = down;
        return edge;
    }

    public static string Describe(GamepadButtons b) =>
        string.Join('+', Enum.GetValues<GamepadButtons>().Where(f => f != GamepadButtons.None && b.HasFlag(f) && IsSingle(f)).Select(f => f.ToString()));

    private static bool IsSingle(GamepadButtons f) => ((int)f & ((int)f - 1)) == 0;
}

/// <summary>Ignores toggles that arrive faster than a person can mean them (key bounce, two sources).</summary>
public sealed class ToggleDebouncer(TimeSpan window)
{
    private TimeSpan? _last;

    public bool Accept(TimeSpan now)
    {
        if (_last is { } l && now - l < window) return false;
        _last = now;
        return true;
    }
}
