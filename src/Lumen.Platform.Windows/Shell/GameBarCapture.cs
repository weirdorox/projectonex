using System.Runtime.InteropServices;
using Lumen.Core.Diagnostics;
using Microsoft.Win32;

namespace Lumen.Platform.Windows.Shell;

/// <summary>
/// Screenshots and recordings through Xbox Game Bar - Windows' supported capture feature (works with
/// HDR and DRM rules, saves to Videos\Captures). Lumen only presses Game Bar's documented default
/// shortcuts (Win+Alt+PrtScn, Win+Alt+R) for the window in front; if Game Bar captures are turned off
/// or the shortcuts were changed, Windows simply does nothing and Lumen says so.
/// </summary>
public static class GameBarCapture
{
    private const ushort VK_LWIN = 0x5B, VK_MENU = 0x12, VK_SNAPSHOT = 0x2C, VK_R = 0x52;

    /// <summary>False when the user turned Game Bar captures off (Settings › Gaming › Captures).</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
            return key?.GetValue("AppCaptureEnabled") is not int v || v != 0;
        }
        catch
        {
            return true;
        }
    }

    public static string CapturesFolder
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
                if (key?.GetValue("AppCaptureFolder") is string custom && custom.Length > 0) return custom;
            }
            catch { /* default below */ }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");
        }
    }

    public static bool Screenshot() => Chord(VK_SNAPSHOT, "screenshot");

    public static bool ToggleRecording() => Chord(VK_R, "recording toggle");

    private static bool Chord(ushort key, string what)
    {
        var inputs = new[]
        {
            Key(VK_LWIN, false), Key(VK_MENU, false), Key(key, false),
            Key(key, true), Key(VK_MENU, true), Key(VK_LWIN, true),
        };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        Log.Info($"Game Bar {what}: sent {sent}/{inputs.Length} key events.");
        return sent == inputs.Length;
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        Ki = new KEYBDINPUT { Vk = vk, Flags = (up ? 0x2u : 0u) | (vk is VK_LWIN or VK_SNAPSHOT ? 0x1u : 0u) }, // KEYUP | EXTENDEDKEY
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public int MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUT
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KEYBDINPUT Ki;
        [FieldOffset(8)] public MOUSEINPUT Mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
