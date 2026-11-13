using CNCStudio.Core.GCode;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

/// <summary>The X/Y zero that is really set must keep the program inside GRBL's soft limits (−$13x…0).</summary>
public class JobEnvelopeTests
{
    private static readonly Dictionary<int, double> Settings = new() { [130] = 500, [131] = 500, [132] = 100 };

    // A 409 × 409 facing area from X0 Y0, with run-out past the edges (as in the surfacing program).
    private static IReadOnlyList<ProgramMove> Program() => GCodePath.Parse(new[]
    {
        "G21 G90", "G0 Z10", "G0 X-14.5 Y0", "G1 Z-0.5 F300", "G1 X423.5 F2000", "G1 Y20", "G1 X-14.5", "G0 Z10", "G0 X0 Y409", "G0 X0 Y0"
    });

    [Fact]
    public void A_zero_left_at_machine_zero_is_caught_before_GRBL_alarms()
    {
        // What happened: X/Y never zeroed, so G54 = 0 and X0 Y0 is machine 0,0 (the corner opposite home).
        var problem = JobEnvelope.Check(Program(), new double[] { 0, 0, -45 }, Settings, "ytans främre vänstra hörn");
        Assert.NotNull(problem);
        Assert.Contains("X går till MPos 423.5", problem);
        Assert.Contains("Y går till MPos 409", problem);
        Assert.Contains("Nolla X + Y", problem);
    }

    [Fact]
    public void The_zero_in_the_areas_corner_passes()
    {
        // Home front-left at MPos −500, −500: the area's front-left corner 45.5 mm in from it.
        Assert.Null(JobEnvelope.Check(Program(), new double[] { -500 + 45.5, -500 + 45.5, -45 }, Settings, "x"));
    }

    [Fact]
    public void A_zero_too_close_to_the_home_corner_is_caught_on_the_negative_side()
    {
        var problem = JobEnvelope.Check(Program(), new double[] { -495, -450, -45 }, Settings, "x");
        Assert.Contains("X går till MPos -509.5", problem);
        Assert.Contains("förbi maskinens gräns (−500)", problem);
        Assert.DoesNotContain("Y går", problem);
    }

    [Fact]
    public void Without_travel_settings_nothing_is_claimed()
    {
        Assert.Null(JobEnvelope.Check(Program(), new double[] { 0, 0, 0 }, new Dictionary<int, double>(), "x"));
        Assert.Null(JobEnvelope.Check(Array.Empty<ProgramMove>(), new double[] { 0, 0, 0 }, Settings, "x"));
    }
}
