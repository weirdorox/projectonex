using System.Runtime.InteropServices;
using Lumen.Core.Input;

namespace Lumen.Platform.Windows.Shell;

/// <summary>
/// Sends mouse input with SendInput (documented). Windows blocks injection into elevated windows
/// (UAC prompts, admin apps) - that is a deliberate Windows protection and is not worked around.
/// </summary>
public static class MouseInjector
{
    private const uint MOVE = 0x1, LEFTDOWN = 0x2, LEFTUP = 0x4, RIGHTDOWN = 0x8, RIGHTUP = 0x10, WHEEL = 0x800;

    public static void Send(MouseOutput o)
    {
        if (o.IsEmpty) return;
        var inputs = new List<GameBarCapture.INPUT>(4);
        if (o.Dx != 0 || o.Dy != 0) inputs.Add(Mouse(MOVE, o.Dx, o.Dy, 0));
        if (o.Left is { } l) inputs.Add(Mouse(l ? LEFTDOWN : LEFTUP, 0, 0, 0));
        if (o.Right is { } r) inputs.Add(Mouse(r ? RIGHTDOWN : RIGHTUP, 0, 0, 0));
        if (o.Wheel != 0) inputs.Add(Mouse(WHEEL, 0, 0, o.Wheel));
        if (inputs.Count > 0)
            GameBarCapture.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<GameBarCapture.INPUT>());
    }

    private static GameBarCapture.INPUT Mouse(uint flags, int dx, int dy, int data) => new()
    {
        Type = 0, // INPUT_MOUSE
        Mi = new GameBarCapture.MOUSEINPUT { Dx = dx, Dy = dy, MouseData = data, Flags = flags },
    };
}
