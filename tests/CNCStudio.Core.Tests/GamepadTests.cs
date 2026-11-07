namespace CNCStudio.Core.Tests;

/// <summary>Gamepad mapping: lockout, Stop always, second action, deadzone and invert, presses not holds.</summary>
public class GamepadTests
{
    private static GamepadState S(GamepadButton b = GamepadButton.None, double lx = 0, double ly = 0, double ry = 0) =>
        new(b, lx, ly, 0, ry, 0, 0);

    [Fact]
    public void Nothing_but_Stop_works_without_the_lockout_button()
    {
        var p = new GamepadProfile();
        var o = GamepadMapper.Map(p, GamepadState.Released, S(GamepadButton.X | GamepadButton.Back, lx: 1));
        Assert.False(o.Unlocked);
        Assert.Equal(new[] { GamepadAction.Stop }, o.Actions);            // Back = Stop gets through, X (zero) does not
        Assert.False(o.Jogging);
    }

    [Fact]
    public void With_the_lockout_held_buttons_act_once_per_press()
    {
        var p = new GamepadProfile();
        var down = S(GamepadButton.LB | GamepadButton.X);
        Assert.Equal(new[] { GamepadAction.ZeroXY }, GamepadMapper.Map(p, S(GamepadButton.LB), down).Actions);
        Assert.Empty(GamepadMapper.Map(p, down, down).Actions);           // still held: not again
    }

    [Fact]
    public void The_second_action_button_switches_to_the_second_map()
    {
        var p = new GamepadProfile();
        var o = GamepadMapper.Map(p, S(GamepadButton.LB | GamepadButton.RB), S(GamepadButton.LB | GamepadButton.RB | GamepadButton.X));
        Assert.Equal(new[] { GamepadAction.GoToXY0 }, o.Actions);
        // A button without a second action keeps its first one.
        var o2 = GamepadMapper.Map(p, S(GamepadButton.LB | GamepadButton.RB), S(GamepadButton.LB | GamepadButton.RB | GamepadButton.DPadUp));
        Assert.Equal(new[] { GamepadAction.JogYPlus }, o2.Actions);
    }

    [Fact]
    public void Sticks_jog_after_the_deadzone_scaled_from_zero_and_can_be_inverted()
    {
        var p = new GamepadProfile { Deadzone = 0.2, InvertZ = true };
        Assert.False(GamepadMapper.Map(p, GamepadState.Released, S(GamepadButton.LB, lx: 0.15)).Jogging);
        var o = GamepadMapper.Map(p, GamepadState.Released, S(GamepadButton.LB, lx: 0.6, ly: -1, ry: 1));
        Assert.Equal(0.5, o.JogX, 6);                                     // (0.6 − 0.2) / 0.8
        Assert.Equal(-1, o.JogY, 6);
        Assert.Equal(-1, o.JogZ, 6);                                      // inverted
    }

    [Fact]
    public void Without_a_lockout_button_everything_works()
    {
        var p = new GamepadProfile { LockoutButton = GamepadButton.None };
        var o = GamepadMapper.Map(p, GamepadState.Released, S(GamepadButton.B, ly: 1));
        Assert.True(o.Unlocked);
        Assert.Equal(new[] { GamepadAction.ZeroZ }, o.Actions);
        Assert.True(o.Jogging);
    }

    [Fact]
    public void Invalid_profiles_are_refused()
    {
        Assert.NotNull(new GamepadProfile { Deadzone = 0 }.Validate());
        Assert.NotNull(new GamepadProfile { SecondActionButton = GamepadButton.LB }.Validate());
        Assert.NotNull(new GamepadProfile { StickZ = GamepadStickAxis.LeftX }.Validate());
        Assert.Null(new GamepadProfile().Validate());
    }

    [Fact]
    public void The_profile_is_saved_and_read_back()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cncstudio-pad-" + Guid.NewGuid().ToString("N"));
        var storage = new AppStorage(Path.Combine(folder, "local"), Path.Combine(folder, "CNC Studio"));
        var p = new GamepadProfile { Deadzone = 0.3, InvertY = true };
        p.Actions[GamepadButton.RightStick] = GamepadAction.Home;
        storage.SaveGamepadProfile(p);
        var back = storage.LoadGamepadProfile();
        Assert.Equal(0.3, back.Deadzone);
        Assert.True(back.InvertY);
        Assert.Equal(GamepadAction.Home, back.Actions[GamepadButton.RightStick]);
        Assert.Equal(GamepadAction.Stop, back.Actions[GamepadButton.Back]);
    }
}
