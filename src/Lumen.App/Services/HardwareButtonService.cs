using System.Diagnostics;
using Lumen.App.Input;
using Lumen.Core.Diagnostics;
using Lumen.Core.Input;
using Lumen.Platform.Windows.Input;

namespace Lumen.App.Services;

/// <summary>One input event seen while learning, for the diagnostics list.</summary>
public sealed record ButtonEvent(DateTime At, ButtonSourceKind Kind, string Device, string Details, ButtonBinding? Binding, string Availability);

/// <summary>
/// Global Quick settings toggle, running in the Lumen process while the dashboard is hidden:
/// - the detected hardware button (keyboard combination, HID report bit or controller chord),
/// - a keyboard fallback shortcut (RegisterHotKey - owned by Lumen, Ctrl+Alt+Q by default),
/// - the built-in controller chord View + Menu (handled by the input mapper).
/// Raw Input is read-only: Windows and the game still get the same input. Nothing is hooked.
/// </summary>
public sealed class HardwareButtonService : IDisposable
{
    private const int ShortcutHotkeyId = 0x5101, ButtonHotkeyId = 0x5102;
    private readonly AppHost _host;
    private readonly GamepadNavigator _navigator;
    private readonly Action<string> _toggle;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ToggleDebouncer _debounce = new(TimeSpan.FromMilliseconds(300));
    private readonly KeyChordTracker _chords = new();
    private readonly Dictionary<IntPtr, byte[]> _lastReports = new();
    private RawInputListener? _raw;
    private ButtonBinding? _button;
    private ButtonBinding _shortcut = ButtonBinding.DefaultKeyboard;
    private HidButtonMatcher? _hidMatcher;
    private ControllerChord? _padChord;
    private bool _buttonOwned;   // keyboard button registered with RegisterHotKey (else passive)
    private bool _learning;
    private GamepadButtons _learnPadPrevious;

    public HardwareButtonService(AppHost host, GamepadNavigator navigator, Action<string> toggle)
    {
        _host = host;
        _navigator = navigator;
        _toggle = toggle;
        _navigator.StateRead += OnPadState;
    }

    public string ShortcutStatus { get; private set; } = "not started";
    public string ButtonStatus { get; private set; } = "no hardware button assigned";
    public ButtonBinding? Button => _button;
    public ButtonBinding Shortcut => _shortcut;

    /// <summary>Raised for every event while learning (UI thread).</summary>
    public event Action<ButtonEvent>? Learned;

    public void Start()
    {
        if (_host.IsSafeMode) return;
        try
        {
            _raw = new RawInputListener();
            _raw.Hotkey += OnHotkey;
            _raw.Key += OnKey;
            _raw.Hid += OnHid;
            _raw.DevicesChanged += () => { if (_learning) ListenAllHid(); };
            Apply();
        }
        catch (Exception ex)
        {
            Log.Error("Hardware button listener failed to start.", ex);
            ShortcutStatus = ButtonStatus = "listener unavailable: " + ex.Message;
        }
    }

    /// <summary>Re-applies bindings from settings (after a change, or after sleep/resume).</summary>
    public void Apply()
    {
        if (_raw is null) return;
        _raw.UnregisterHotkey(ShortcutHotkeyId);
        _raw.UnregisterHotkey(ButtonHotkeyId);
        _raw.StopHid(_ => true);
        _hidMatcher = null;
        _padChord = null;
        _buttonOwned = false;

        _shortcut = ButtonBinding.Parse(_host.Settings.QuickSettingsShortcut) is { Kind: ButtonSourceKind.Keyboard } s ? s : ButtonBinding.DefaultKeyboard;
        ShortcutStatus = _raw.RegisterHotkey(ShortcutHotkeyId, (int)_shortcut.Modifiers, _shortcut.VirtualKey)
            ? $"{_shortcut.Describe()} - active"
            : $"{_shortcut.Describe()} - reserved by another app, choose a different one";

        _button = ButtonBinding.Parse(_host.Settings.QuickSettingsButton);
        var auto = false;
        if (_button is null && OneXInterfacePresent())
        {
            // OneXPlayer detected: the OneX button opens Quick settings without any setup.
            _button = new ButtonBinding { Kind = ButtonSourceKind.OneX, ButtonId = OneXVendorHid.OneXButtonOxp3 };
            auto = true;
        }
        switch (_button?.Kind)
        {
            case ButtonSourceKind.OneX:
                var listening = _raw.ListenHid([(OneXVendorHid.UsagePage, OneXVendorHid.Usage)]) > 0;
                ButtonStatus = $"{_button.Describe()}{(auto ? " (set automatically)" : "")} - " + (listening
                    ? "active. If nothing happens, press Detect to see which id your OneX button sends."
                    : "OneXPlayer controller interface not found right now");
                break;
            case ButtonSourceKind.Keyboard:
                _buttonOwned = _raw.RegisterHotkey(ButtonHotkeyId, (int)_button.Modifiers, _button.VirtualKey);
                if (!_buttonOwned) _raw.ListenKeyboard(); // reserved: watch it passively instead
                ButtonStatus = $"{_button.Describe()} - " + (_buttonOwned
                    ? "active (Lumen owns this key combination)"
                    : "active in watch mode (another app also reacts to it)");
                break;
            case ButtonSourceKind.Hid:
                _hidMatcher = new HidButtonMatcher(_button);
                var n = _raw.ListenHid([(_button.UsagePage, _button.Usage)]);
                ButtonStatus = $"{_button.Describe()} - " + (n > 0 ? "active" : "device not found right now");
                break;
            case ButtonSourceKind.Controller:
                _padChord = new ControllerChord(_button.Buttons);
                ButtonStatus = $"{_button.Describe()} - active";
                break;
            default:
                ButtonStatus = "no hardware button assigned - use Detect";
                break;
        }
        if (_learning) StartLearning();
        Log.Info($"Quick settings button: {ButtonStatus}; shortcut: {ShortcutStatus}.");
    }

    /// <summary>After sleep/resume Windows may drop registrations - renew them.</summary>
    public void Resume()
    {
        try
        {
            _raw?.Renew();
            Apply();
        }
        catch (Exception ex)
        {
            Log.Warn("Renewing input registrations failed.", ex);
        }
    }

    // ---------------- learning (diagnostics) ----------------

    public void StartLearning()
    {
        if (_raw is null) return;
        _learning = true;
        _lastReports.Clear();
        _chords.Reset();
        _raw.ListenKeyboard();
        ListenAllHid();
        Log.Info("Button detection started.");
    }

    public void StopLearning()
    {
        if (!_learning) return;
        _learning = false;
        Apply(); // drops the extra registrations
        Log.Info("Button detection stopped.");
    }

    private bool OneXInterfacePresent()
    {
        try
        {
            return _raw!.EnumerateHid().Any(d => OneXVendorHid.IsVendorInterface(d.VendorId, d.ProductId, d.UsagePage, d.Usage));
        }
        catch (Exception ex)
        {
            Log.Warn("Enumerating HID devices failed.", ex);
            return false;
        }
    }

    private void ListenAllHid()
    {
        var devices = _raw!.EnumerateHid();
        _raw.ListenHid(devices.Select(d => (d.UsagePage, d.Usage)).Append((OneXVendorHid.UsagePage, OneXVendorHid.Usage)));
    }

    public string Availability(ButtonBinding b) =>
        b.Kind != ButtonSourceKind.Keyboard || _raw is null ? "can be used"
        : (b == _button && _buttonOwned) || b == _shortcut ? "already used by Lumen"
        : _raw.IsReserved((int)b.Modifiers, b.VirtualKey) ? "reserved by another app (Lumen can still watch it)"
        : "free";

    public void UseAsButton(ButtonBinding b)
    {
        _host.Settings.QuickSettingsButton = b.Serialize();
        Apply();
    }

    public void ClearButton()
    {
        _host.Settings.QuickSettingsButton = null;
        Apply();
    }

    public string? UseAsShortcut(ButtonBinding b)
    {
        if (b.Kind != ButtonSourceKind.Keyboard || b.Modifiers == KeyModifiers.None) return "Use a combination with Ctrl, Alt, Shift or Win.";
        _host.Settings.QuickSettingsShortcut = b.Serialize();
        Apply();
        return ShortcutStatus.Contains("reserved", StringComparison.Ordinal) ? ShortcutStatus : null;
    }

    // ---------------- events ----------------

    private void Fire(string source)
    {
        if (_learning) return; // while detecting, buttons are only listed
        if (!_debounce.Accept(_clock.Elapsed))
        {
            Log.Debug($"Quick settings toggle ignored (debounce, {source}).");
            return;
        }
        _toggle(source);
    }

    private void OnHotkey(int id)
    {
        if (id == ShortcutHotkeyId) Fire("keyboard shortcut");
        else if (id == ButtonHotkeyId) Fire("hardware button (key combination)");
    }

    private void OnKey(RawKeyEvent e)
    {
        var chord = _chords.Feed(e.VirtualKey, e.Down);
        if (_learning && e.Down)
        {
            var mods = chord?.Modifiers ?? KeyModifiers.None;
            var binding = chord is null ? null : new ButtonBinding { Kind = ButtonSourceKind.Keyboard, Modifiers = mods, VirtualKey = chord.Value.Vk };
            Learned?.Invoke(new ButtonEvent(DateTime.Now, ButtonSourceKind.Keyboard, DeviceName(e.Device),
                $"{(binding?.Describe() ?? KeyNames.Key(e.VirtualKey) + " (modifier)")} · VK 0x{e.VirtualKey:X2} · scan 0x{e.ScanCode:X2}{(e.Extended ? " E0" : "")}",
                binding, binding is null ? "modifier - press it together with another key" : Availability(binding)));
            return;
        }
        // Watch mode for a reserved key combination (the other app also reacts - can't be prevented without a hook).
        if (chord is { } c && _button is { Kind: ButtonSourceKind.Keyboard } b && !_buttonOwned && c.Modifiers == b.Modifiers && c.Vk == b.VirtualKey)
            Fire("hardware button (watched key combination)");
    }

    private void OnHid(RawHidEvent e)
    {
        // OneXPlayer OEM buttons (OneX, Turbo, G-Key, M1/M2) on the vendor interface.
        if (OneXVendorHid.IsVendorInterface(e.Device.VendorId, e.Device.ProductId, e.Device.UsagePage, e.Device.Usage)
            && OneXVendorHid.TryParse(e.Report) is { } onex)
        {
            if (_learning)
            {
                if (!onex.Pressed) return;
                var binding = new ButtonBinding { Kind = ButtonSourceKind.OneX, ButtonId = onex.ButtonId };
                Learned?.Invoke(new ButtonEvent(DateTime.Now, ButtonSourceKind.OneX, DeviceName(e.Device),
                    $"{binding.Describe()} · vendor HID button id 0x{onex.ButtonId:X2} · report {Convert.ToHexString(e.Report.AsSpan(0, Math.Min(16, e.Report.Length)))}",
                    binding, "can be used"));
            }
            else if (onex.Pressed && _button is { Kind: ButtonSourceKind.OneX } onexBinding && onexBinding.ButtonId == onex.ButtonId)
            {
                Fire($"OneXPlayer {OneXVendorHid.Name(onex.ButtonId)} button");
            }
            return;
        }

        if (_learning)
        {
            if (_lastReports.TryGetValue(e.Device.Handle, out var before) && HidButtonLearner.Diff(before, e.Report) is { } diff)
            {
                var binding = new ButtonBinding
                {
                    Kind = ButtonSourceKind.Hid, VendorId = e.Device.VendorId, ProductId = e.Device.ProductId,
                    UsagePage = e.Device.UsagePage, Usage = e.Device.Usage, ByteIndex = diff.ByteIndex, Mask = diff.Mask, PressedValue = diff.PressedValue,
                };
                // Analog axes change all the time; only list single-bit changes (buttons).
                if ((diff.Mask & (diff.Mask - 1)) == 0)
                    Learned?.Invoke(new ButtonEvent(DateTime.Now, ButtonSourceKind.Hid, DeviceName(e.Device),
                        $"page {e.Device.UsagePage:X4}/{e.Device.Usage:X2} · byte {diff.ByteIndex} bit 0x{diff.Mask:X2} {(diff.PressedValue != 0 ? "on" : "off")} · report {Convert.ToHexString(e.Report.AsSpan(0, Math.Min(16, e.Report.Length)))}",
                        diff.PressedValue != 0 ? binding : null, diff.PressedValue != 0 ? "can be used" : "release - ignored"));
            }
            _lastReports[e.Device.Handle] = e.Report;
            return;
        }
        if (_hidMatcher is not null && _button is { } b && e.Device.VendorId == b.VendorId && e.Device.ProductId == b.ProductId
            && e.Device.UsagePage == b.UsagePage && e.Device.Usage == b.Usage && _hidMatcher.Feed(e.Report))
            Fire("hardware button (HID)");
    }

    private void OnPadState(GamepadState s)
    {
        if (_learning)
        {
            var pressed = s.Buttons & ~_learnPadPrevious;
            _learnPadPrevious = s.Buttons;
            if (pressed != GamepadButtons.None && s.Buttons != GamepadButtons.None)
            {
                var binding = new ButtonBinding { Kind = ButtonSourceKind.Controller, Buttons = s.Buttons };
                Learned?.Invoke(new ButtonEvent(DateTime.Now, ButtonSourceKind.Controller, "XInput controller",
                    $"{ControllerChord.Describe(s.Buttons)} (hold all buttons of the combination)",
                    BitCount((int)s.Buttons) >= 2 ? binding : null,
                    BitCount((int)s.Buttons) >= 2 ? "combination - games also see these buttons" : "single buttons are needed by games - hold a combination"));
            }
            return;
        }
        if (_padChord?.Feed(s) == true) Fire("controller combination");
    }

    private static int BitCount(int v) => System.Numerics.BitOperations.PopCount((uint)v);

    private static string DeviceName(RawDevice? d) => d is null
        ? "injected / virtual input"
        : d.VendorId != 0 ? $"VID {d.VendorId:X4} PID {d.ProductId:X4}{(d.IsKeyboard ? " keyboard" : " HID")}" : (d.IsKeyboard ? "keyboard" : "HID device");

    public void Dispose()
    {
        _navigator.StateRead -= OnPadState;
        _raw?.Dispose();
    }
}
