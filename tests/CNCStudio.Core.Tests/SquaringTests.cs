using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

/// <summary>XY squaring: three marked points, three measured distances, the angle and what to adjust.</summary>
public class SquaringTests
{
    /// <summary>
    /// What a ruler would measure on a machine whose gantry (X direction) is turned <paramref name="phiDeg"/>
    /// from square to the rails (Y direction, straight back); positive = right end further back.
    /// </summary>
    private static SquaringInput Measure(double phiDeg, int sx, int sy, double dx = 300, double dy = 300, double span = 750)
    {
        double phi = phiDeg * Math.PI / 180;
        double x2 = sx * dx * Math.Cos(phi), y2 = sx * dx * Math.Sin(phi);   // point 2 after the X move
        double x3 = x2, y3 = y2 + sy * dy;                                    // point 3 after the Y move
        return new SquaringInput(sx * dx, sy * dy, dx, dy, Math.Sqrt(x3 * x3 + y3 * y3), span);
    }

    [Theory]
    [InlineData(+1, +1)] [InlineData(+1, -1)] [InlineData(-1, +1)] [InlineData(-1, -1)]
    public void The_angle_comes_out_the_same_whichever_way_the_triangle_is_made(int sx, int sy)
    {
        foreach (var phi in new[] { 0.3, -0.3, 1.0, -0.05 })
        {
            var r = SquaringCheck.Compute(Measure(phi, sx, sy));
            Assert.Equal(phi, r.AngleErrorDeg, 6);
            Assert.Equal(750 * Math.Tan(phi * Math.PI / 180), r.GantryOffsetMm, 6);
        }
    }

    [Fact]
    public void A_square_machine_needs_nothing()
    {
        var r = SquaringCheck.Compute(Measure(0, +1, +1));
        Assert.Equal(SquaringVerdict.Square, r.Verdict);
        Assert.Equal(0, r.DiagonalError, 6);
        Assert.StartsWith("Maskinen är vinkelrät", SquaringCheck.Describe(r));
        Assert.DoesNotContain("Flytta", SquaringCheck.Describe(r));
    }

    [Fact]
    public void Right_end_too_far_back_is_moved_forward()
    {
        var r = SquaringCheck.Compute(Measure(0.3, +1, +1));      // 750 × tan 0.3° ≈ 3.9 mm
        Assert.Equal(SquaringVerdict.NeedsAdjustment, r.Verdict);
        var text = SquaringCheck.Describe(r);
        Assert.Contains("högra ände ligger 3.9 mm för långt bak", text);
        Assert.Contains("Flytta högra änden 3.9 mm framåt, eller vänstra änden 3.9 mm bakåt", text);
    }

    [Fact]
    public void Right_end_too_far_forward_is_moved_back()
    {
        var text = SquaringCheck.Describe(SquaringCheck.Compute(Measure(-0.12, -1, +1)));   // ≈ 1.6 mm
        Assert.Contains("för långt fram", text);
        Assert.Contains("Flytta högra änden 1.6 mm bakåt", text);
        Assert.Equal(SquaringVerdict.Slight, SquaringCheck.Compute(Measure(-0.12, -1, +1)).Verdict);
    }

    [Fact]
    public void The_correction_is_the_gantry_length_times_the_angle_not_the_diagonal_error()
    {
        var r = SquaringCheck.Compute(Measure(0.3, +1, +1));
        // gSender would advise moving the rail by the diagonal's error – here far less than what is needed.
        Assert.True(Math.Abs(r.GantryOffsetMm) > 3 * Math.Abs(r.DiagonalError));
    }

    [Fact]
    public void A_small_error_within_the_measuring_accuracy_is_flagged()
    {
        var r = SquaringCheck.Compute(Measure(0.11, +1, +1));      // just over 0.1°, but a 300 mm triangle measures to about ±0.2°
        Assert.True(r.UncertaintyDeg > 0.1);
        Assert.True(r.WithinMeasuringError);
        Assert.Contains("mindre än mätosäkerheten", SquaringCheck.Describe(r));
        var big = SquaringCheck.Compute(Measure(1, +1, +1, 450, 450));
        Assert.False(big.WithinMeasuringError);
        Assert.True(big.UncertaintyDeg < r.UncertaintyDeg);           // a bigger triangle is surer
    }

    [Fact]
    public void Sides_that_differ_from_the_moves_point_at_steps_per_mm()
    {
        var i = Measure(0, +1, +1) with { A = 301.5 };
        var r = SquaringCheck.Compute(i);
        Assert.Equal(0.5, r.XLengthErrorPercent, 6);
        Assert.True(r.StepsWorthChecking);
        Assert.Contains("Kalibrera steg/mm", SquaringCheck.Describe(r));
        Assert.False(SquaringCheck.Compute(Measure(0, +1, +1)).StepsWorthChecking);
    }

    [Fact]
    public void Wrong_measurements_are_refused()
    {
        var ok = Measure(0.2, +1, +1);
        Assert.Null(SquaringCheck.Validate(ok));
        Assert.NotNull(SquaringCheck.Validate(ok with { C = 700 }));                // not a triangle
        Assert.NotNull(SquaringCheck.Validate(ok with { A = 250 }));                // far from the 300 mm move
        Assert.NotNull(SquaringCheck.Validate(ok with { MoveX = 20 }));             // too small
        Assert.NotNull(SquaringCheck.Validate(ok with { GantrySpan = 0 }));
        Assert.NotNull(SquaringCheck.Validate(ok with { B = double.NaN }));
    }

    [Fact]
    public void Directions_are_chosen_so_both_moves_stay_inside_the_machine()
    {
        // Tool at X 100, Y 600 on a 750 × 750 table: right fits, back does not → right and forward.
        Func<double, double, string?> check = (dx, dy) =>
            100 + dx is < 0 or > 750 ? "X utanför" : 600 + dy is < 0 or > 750 ? "Y utanför" : null;
        Assert.Equal((1, -1, (string?)null), SquaringCheck.ChooseDirections(300, 300, check));
        var none = SquaringCheck.ChooseDirections(800, 300, check);
        Assert.Equal((0, 0), (none.Sx, none.Sy));
        Assert.NotNull(none.Problem);
    }

    [Fact]
    public void Checks_are_kept_per_machine_and_a_repeated_measurement_is_not_saved_twice()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cncstudio-squaring-" + Guid.NewGuid().ToString("N"))).FullName;
        var lib = new MachineLibrary(folder);
        var machine = lib.CreateNew("X-Carve", new MachineProfile());
        var path = lib.SquaringLogPath(machine.Id!);
        var log = SquaringLog.Load(path);
        var r = SquaringCheck.Compute(Measure(0.3, +1, +1));
        Assert.True(log.Add(r, new DateTime(2026, 11, 12), "PC"));
        Assert.False(log.Add(r, new DateTime(2026, 11, 12), "PC"));
        log.Save(path);
        var back = SquaringLog.Load(path);
        Assert.Single(back.Entries);
        Assert.Equal(750, back.GantrySpan);
        Assert.Equal(0.3, back.Entries[0].AngleErrorDeg, 6);
        Assert.Single(lib.All(), m => m.Name == "X-Carve");           // the log folder is not taken for a machine
    }
}
