using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

public class LimitSwitchTestTests
{
    private static GrblStatus S(string? pins) => new() { State = "Idle", Pins = pins };

    [Fact]
    public void Axis_passes_only_after_press_and_release()
    {
        var t = new LimitSwitchTest();
        t.Update(S(null));
        t.Update(S("X"));
        Assert.Equal(SwitchTestState.Pressed, t.States['X']);
        t.Update(S(null));
        Assert.Equal(SwitchTestState.Passed, t.States['X']);
        Assert.Equal(SwitchTestState.Waiting, t.States['Y']);
        Assert.False(t.AllPassed);
    }

    [Fact]
    public void All_three_axes_pass()
    {
        var t = new LimitSwitchTest();
        t.Update(S(null));
        foreach (var a in new[] { "X", "Y", "Z" }) { t.Update(S(a)); t.Update(S(null)); }
        Assert.True(t.AllPassed);
    }

    [Fact]
    public void Switch_active_at_start_is_reported_and_not_counted_as_a_press()
    {
        var t = new LimitSwitchTest();
        t.Update(S("Z"));
        Assert.Equal(new[] { 'Z' }, t.ActiveAtStart);
        t.Update(S(null));
        Assert.Equal(SwitchTestState.Waiting, t.States['Z']); // never seen pressed by the user
        Assert.Empty(t.ActiveAtStart);                          // released: can now be tested
        t.Update(S("Z")); t.Update(S(null));
        Assert.Equal(SwitchTestState.Passed, t.States['Z']);
    }

    [Fact]
    public void Probe_and_control_pins_are_ignored()
    {
        Assert.Equal(new[] { 'X', 'Z' }, LimitSwitchTest.LimitAxes("PXZDH"));
        Assert.Empty(LimitSwitchTest.LimitAxes("PDRHS"));
        Assert.Empty(LimitSwitchTest.LimitAxes(null));
    }

    [Fact]
    public void Pn_is_parsed_from_a_real_status_line()
    {
        var s = GrblProtocol.ParseStatus("<Idle|MPos:0.000,0.000,-1.000|FS:0,0|Pn:Y>")!;
        Assert.Equal(new[] { 'Y' }, LimitSwitchTest.LimitAxes(s.Pins));
        var none = GrblProtocol.ParseStatus("<Idle|MPos:0.000,0.000,-1.000|FS:0,0>")!;
        Assert.Empty(LimitSwitchTest.LimitAxes(none.Pins));
    }
}
