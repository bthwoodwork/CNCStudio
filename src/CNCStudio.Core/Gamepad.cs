using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>Buttons of an Xbox-style (XInput) controller; the values are XInput's button bits.</summary>
[Flags]
public enum GamepadButton
{
    None = 0,
    DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008,
    Start = 0x0010, Back = 0x0020, LeftStick = 0x0040, RightStick = 0x0080,
    LB = 0x0100, RB = 0x0200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
    /// <summary>The triggers count as buttons when pressed past half way.</summary>
    LT = 0x10000, RT = 0x20000,
}

/// <summary>What a button does.</summary>
public enum GamepadAction
{
    None,
    JogXMinus, JogXPlus, JogYMinus, JogYPlus, JogZMinus, JogZPlus,
    NextJogSpeed, ZeroXY, ZeroZ, GoToXY0, Home, Probe,
    Pause, Resume, Stop,
}

/// <summary>Which stick axis moves which machine axis.</summary>
public enum GamepadStickAxis { LeftX, LeftY, RightX, RightY }

/// <summary>One reading of the controller: buttons and sticks/triggers scaled to −1…1 and 0…1.</summary>
public sealed record GamepadState(GamepadButton Buttons, double LeftX, double LeftY, double RightX, double RightY,
    double LeftTrigger, double RightTrigger)
{
    public static readonly GamepadState Released = new(GamepadButton.None, 0, 0, 0, 0, 0, 0);
    public bool IsDown(GamepadButton b) => b != GamepadButton.None && (Buttons & b) == b;
    public double Stick(GamepadStickAxis a) => a switch
    {
        GamepadStickAxis.LeftX => LeftX, GamepadStickAxis.LeftY => LeftY, GamepadStickAxis.RightX => RightX, _ => RightY
    };
}

/// <summary>
/// The gamepad setup (Inställningar\gamepad.json). Modelled on how gSender uses a controller: sticks jog
/// continuously with speed following how far they are pushed, buttons run actions, a second-action button
/// works like Shift, and a lockout button must be held for anything to happen. Stop works without it.
/// </summary>
public sealed class GamepadProfile
{
    public bool Enabled { get; set; } = true;
    /// <summary>Must be held for any action or jog (safety if the controller is dropped). None = no lockout.</summary>
    public GamepadButton LockoutButton { get; set; } = GamepadButton.LB;
    /// <summary>Held together with another button: that button's second action.</summary>
    public GamepadButton SecondActionButton { get; set; } = GamepadButton.RB;
    /// <summary>Stick movement below this (0–1) counts as released.</summary>
    public double Deadzone { get; set; } = 0.25;   // worn sticks rest at ±0.2 or more
    /// <summary>Jog speed with a stick pushed all the way (mm/min); limited by the machine's max rates.</summary>
    public double MaxJogFeedXY { get; set; } = 3000;
    public double MaxJogFeedZ { get; set; } = 800;
    public GamepadStickAxis StickX { get; set; } = GamepadStickAxis.LeftX;
    public GamepadStickAxis StickY { get; set; } = GamepadStickAxis.LeftY;
    public GamepadStickAxis StickZ { get; set; } = GamepadStickAxis.RightY;
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }
    public bool InvertZ { get; set; }

    public Dictionary<GamepadButton, GamepadAction> Actions { get; set; } = new()
    {
        [GamepadButton.DPadLeft] = GamepadAction.JogXMinus, [GamepadButton.DPadRight] = GamepadAction.JogXPlus,
        [GamepadButton.DPadDown] = GamepadAction.JogYMinus, [GamepadButton.DPadUp] = GamepadAction.JogYPlus,
        [GamepadButton.A] = GamepadAction.JogZMinus, [GamepadButton.Y] = GamepadAction.JogZPlus,
        [GamepadButton.X] = GamepadAction.ZeroXY, [GamepadButton.B] = GamepadAction.ZeroZ,
        [GamepadButton.LeftStick] = GamepadAction.NextJogSpeed,
        [GamepadButton.Start] = GamepadAction.Resume, [GamepadButton.Back] = GamepadAction.Stop,
    };

    public Dictionary<GamepadButton, GamepadAction> SecondActions { get; set; } = new()
    {
        [GamepadButton.X] = GamepadAction.GoToXY0, [GamepadButton.B] = GamepadAction.Probe,
        [GamepadButton.Y] = GamepadAction.Home, [GamepadButton.Start] = GamepadAction.Pause,
    };

    public static IReadOnlyList<GamepadButton> AllButtons { get; } =
        Enum.GetValues<GamepadButton>().Where(b => b != GamepadButton.None).ToList();

    public string? Validate()
    {
        if (!double.IsFinite(Deadzone) || Deadzone < 0.02 || Deadzone > 0.9) return "Nollgränsen ska vara 0,02–0,9.";
        if (!double.IsFinite(MaxJogFeedXY) || MaxJogFeedXY <= 0 || !double.IsFinite(MaxJogFeedZ) || MaxJogFeedZ <= 0)
            return "Jogghastigheterna måste vara större än 0.";
        if (LockoutButton != GamepadButton.None && LockoutButton == SecondActionButton)
            return "Spärrknappen och knappen för andra funktion kan inte vara samma.";
        if (new[] { StickX, StickY, StickZ }.Distinct().Count() < 3) return "Varje axel behöver en egen spakriktning.";
        return null;
    }
}

/// <summary>What one reading means for the machine.</summary>
public sealed record GamepadOutput(IReadOnlyList<GamepadAction> Actions, double JogX, double JogY, double JogZ, bool Unlocked)
{
    public bool Jogging => JogX != 0 || JogY != 0 || JogZ != 0;
}

/// <summary>
/// Turns controller readings into actions (on press, not while held) and a jog vector (−1…1 per axis,
/// after the deadzone, rescaled so the speed starts from zero at the deadzone edge). Without the lockout
/// button held, only Stop gets through and the jog vector is zero.
/// </summary>
public static class GamepadMapper
{
    public static GamepadOutput Map(GamepadProfile p, GamepadState previous, GamepadState now)
    {
        bool unlocked = p.LockoutButton == GamepadButton.None || now.IsDown(p.LockoutButton);
        bool second = p.SecondActionButton != GamepadButton.None && now.IsDown(p.SecondActionButton);
        var actions = new List<GamepadAction>();
        foreach (var b in GamepadProfile.AllButtons)
        {
            if (b == p.LockoutButton || b == p.SecondActionButton) continue;
            if (!now.IsDown(b) || previous.IsDown(b)) continue;          // only the moment it is pressed
            var map = second ? p.SecondActions : p.Actions;
            if (!map.TryGetValue(b, out var a) && second) p.Actions.TryGetValue(b, out a);
            if (a == GamepadAction.None) continue;
            if (unlocked || a == GamepadAction.Stop) actions.Add(a);   // Stop always works
        }
        if (!unlocked) return new GamepadOutput(actions, 0, 0, 0, false);
        double Axis(GamepadStickAxis axis, bool invert)
        {
            double v = now.Stick(axis);
            double m = Math.Abs(v);
            if (m < p.Deadzone) return 0;
            double scaled = (m - p.Deadzone) / (1 - p.Deadzone);
            return Math.Sign(v) * Math.Min(1, scaled) * (invert ? -1 : 1);
        }
        return new GamepadOutput(actions, Axis(p.StickX, p.InvertX), Axis(p.StickY, p.InvertY), Axis(p.StickZ, p.InvertZ), true);
    }

    public static string ButtonName(GamepadButton b) => b switch
    {
        GamepadButton.None => "(ingen)", GamepadButton.DPadUp => "Styrkors upp", GamepadButton.DPadDown => "Styrkors ned",
        GamepadButton.DPadLeft => "Styrkors vänster", GamepadButton.DPadRight => "Styrkors höger",
        GamepadButton.Start => "Start (≡)", GamepadButton.Back => "Back (⧉)", GamepadButton.LeftStick => "Vänster spak (tryck)",
        GamepadButton.RightStick => "Höger spak (tryck)", GamepadButton.LT => "LT (avtryckare)", GamepadButton.RT => "RT (avtryckare)",
        _ => b.ToString()
    };

    public static string ActionName(GamepadAction a) => a switch
    {
        GamepadAction.None => "–", GamepadAction.JogXMinus => "Jogga X− ett steg", GamepadAction.JogXPlus => "Jogga X+ ett steg",
        GamepadAction.JogYMinus => "Jogga Y− ett steg", GamepadAction.JogYPlus => "Jogga Y+ ett steg",
        GamepadAction.JogZMinus => "Jogga Z− ett steg", GamepadAction.JogZPlus => "Jogga Z+ ett steg",
        GamepadAction.NextJogSpeed => "Byt jogghastighet (Precis/Normal/Snabb)", GamepadAction.ZeroXY => "Nolla X + Y",
        GamepadAction.ZeroZ => "Nolla Z", GamepadAction.GoToXY0 => "Gå till X0 Y0", GamepadAction.Home => "Homing ($H)",
        GamepadAction.Probe => "Öppna probning", GamepadAction.Pause => "Paus (!)", GamepadAction.Resume => "Fortsätt (~)",
        GamepadAction.Stop => "STOPP (jogg/program)", _ => a.ToString()
    };
}
