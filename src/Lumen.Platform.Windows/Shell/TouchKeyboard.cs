using System.Diagnostics;
using System.Runtime.InteropServices;
using Lumen.Core.Diagnostics;

namespace Lumen.Platform.Windows.Shell;

/// <summary>
/// Shows/hides the Windows touch keyboard for typing in games, launchers and the desktop. Uses the
/// touch keyboard's toggle interface (the same one the taskbar keyboard button uses) and falls back
/// to the classic On-Screen Keyboard (osk.exe), which is a documented accessibility app.
/// </summary>
public static class TouchKeyboard
{
    private static readonly string TabTipPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "microsoft shared", "ink", "TabTip.exe");

    public static bool Toggle()
    {
        if (TryToggleTip()) return true;
        try
        {
            // TabTip must be running for the toggle interface to exist.
            if (File.Exists(TabTipPath))
            {
                using var _ = Process.Start(new ProcessStartInfo(TabTipPath) { UseShellExecute = true });
                Thread.Sleep(400);
                if (TryToggleTip()) return true;
            }
            using var osk = Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true });
            Log.Info("Opened the On-Screen Keyboard (touch keyboard toggle unavailable).");
            return osk is not null;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open a keyboard.", ex);
            return false;
        }
    }

    private static bool TryToggleTip()
    {
        try
        {
            var tip = (ITipInvocation)new UIHostNoLaunch();
            try
            {
                tip.Toggle(GetDesktopWindow());
                return true;
            }
            finally { Marshal.ReleaseComObject(tip); }
        }
        catch (Exception ex)
        {
            Log.Debug($"Touch keyboard toggle unavailable: {ex.Message}");
            return false;
        }
    }

    [ComImport, Guid("4ce576fa-83dc-4F88-951c-9d0782b4e376")]
    private class UIHostNoLaunch { }

    [ComImport, Guid("37c994e7-432b-4834-a2f7-dce1f13b834b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation
    {
        void Toggle(IntPtr hwnd);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
