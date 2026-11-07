using System.Runtime.InteropServices;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Reads an Xbox-compatible controller through XInput (xinput1_4.dll, part of Windows). Controllers that
/// are not XInput devices are not seen; most PC gamepads (and the Xbox 360/One controllers) are.
/// </summary>
public static class XInputGamepad
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepadData
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState { public uint PacketNumber; public XInputGamepadData Gamepad; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint GetState(uint userIndex, out XInputState state);

    private const uint Success = 0;
    private static bool _missing;

    /// <summary>The first connected controller (0–3), or null when none is connected.</summary>
    public static (int Index, GamepadState State)? Read()
    {
        if (_missing) return null;
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                if (GetState(i, out var s) != Success) continue;
                var g = s.Gamepad;
                var buttons = (GamepadButton)g.Buttons;
                if (g.LeftTrigger > 127) buttons |= GamepadButton.LT;
                if (g.RightTrigger > 127) buttons |= GamepadButton.RT;
                static double Axis(short v) => Math.Clamp(v / 32767.0, -1, 1);
                return ((int)i, new GamepadState(buttons, Axis(g.ThumbLX), Axis(g.ThumbLY), Axis(g.ThumbRX), Axis(g.ThumbRY),
                    g.LeftTrigger / 255.0, g.RightTrigger / 255.0));
            }
        }
        catch (DllNotFoundException) { _missing = true; }
        catch (EntryPointNotFoundException) { _missing = true; }
        return null;
    }
}
