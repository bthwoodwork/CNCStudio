using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

/// <summary>Steps/mm: new = old × commanded ÷ measured, rounded like GRBL stores it.</summary>
public class StepsCalibrationTests
{
    [Fact]
    public void An_axis_that_moved_too_short_gets_more_steps()
    {
        var r = StepsCalibration.Compute(0, 40, 100, 99.5);
        Assert.Equal(40.201, r.NewStepsPerMm, 3);
        Assert.Equal(-0.5, r.ErrorPercent, 6);
        Assert.Equal(100, r.SettingNumber);
        Assert.Contains("för kort", StepsCalibration.Describe(r));
    }

    [Fact]
    public void An_axis_that_moved_too_far_gets_fewer_steps()
    {
        var r = StepsCalibration.Compute(2, 200, 20, 20.4);
        Assert.Equal(196.078, r.NewStepsPerMm, 3);
        Assert.Equal(102, r.SettingNumber);
        Assert.Contains("för långt", StepsCalibration.Describe(r));
    }

    [Fact]
    public void A_correct_axis_keeps_its_value()
    {
        Assert.Equal(40, StepsCalibration.Compute(1, 40, 100, 100).NewStepsPerMm);
    }

    [Theory]
    [InlineData(0, 100, 99)]
    [InlineData(40, 0, 99)]
    [InlineData(40, 100, 0)]
    [InlineData(40, 100, double.NaN)]
    public void Impossible_values_are_refused(double old, double commanded, double measured)
    {
        Assert.NotNull(StepsCalibration.Validate(old, commanded, measured));
        Assert.Throws<ArgumentException>(() => StepsCalibration.Compute(0, old, commanded, measured));
    }

    [Fact]
    public void The_move_is_a_jog_on_one_axis()
    {
        Assert.Equal("$J=G91 G21 Y100 F1000", StepsCalibration.MoveLine(1, 100, 1000));
        Assert.Equal("$J=G91 G21 Z-20 F300", StepsCalibration.MoveLine(2, -20, 300));
    }

    [Fact]
    public async Task The_new_value_is_written_and_read_back_on_the_demo_machine()
    {
        var sim = new SimulatedGrbl(speedFactor: 60);
        using var c = new GrblClient(sim);
        await c.ConnectAsync(TimeSpan.FromSeconds(1));
        var r = StepsCalibration.Compute(0, (await c.ReadSettingsAsync())[100], 100, 99.5);
        var w = await c.WriteSettingsAsync(new[] { (r.SettingNumber, r.NewStepsPerMm) });
        Assert.True(w.Success, w.Message);
        Assert.Equal(40.201, (await c.ReadSettingsAsync())[100], 3);
    }
}
