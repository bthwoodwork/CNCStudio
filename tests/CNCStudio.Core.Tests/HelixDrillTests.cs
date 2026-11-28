using System.Globalization;
using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

/// <summary>Spiral drilling: a round hole larger than the tool, cut with G1 only (no G2/G3, no canned cycles).</summary>
public class HelixDrillTests
{
    private static (CncDocument Doc, CamReport Report) Doc(double hole = 8, double tool = 3, double depth = 3.3, double perTurn = 1,
        Action<CncDocument>? tweak = null)
    {
        var doc = new CncDocument { Name = "Magnethål" };
        doc.Motion.StockTopMachineZ = -40;
        var s = new DesignShape { Kind = ShapeKind.Ellipse, X = 100 - hole / 2, Y = 80 - hole / 2, Width = hole, Height = hole };
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, Kind = CamOperationKind.Drill, ToolName = "3 mm", ToolDiameter = tool,
            TotalDepth = depth, DepthPerPass = perTurn, UseTabs = false, FeedRate = 700, PlungeRate = 250, RampFeedPercent = 70 });
        tweak?.Invoke(doc);
        return (doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
    }

    private static double Num(string line, char axis)
    {
        int i = line.IndexOf(axis);
        return double.Parse(line[(i + 1)..].Split(' ')[0], CultureInfo.InvariantCulture);
    }

    [Fact]
    public void A_circle_larger_than_the_tool_becomes_one_spiral_with_equal_turns()
    {
        var (_, report) = Doc(hole: 6);
        var p = Assert.Single(report.Operations).Preview!;
        Assert.True(p.IsHelix);
        Assert.False(p.IsDrill);
        var ring = Assert.Single(p.Centerlines);              // 6 mm with 3 mm: ring radius 1.5 = tool radius, one spiral
        Assert.Equal(4, p.PassDepths.Count);                   // 3.3 mm at most 1 mm per turn = 4 turns of 0.825
        Assert.Equal(0.825, p.PassDepths[0], 6);
        Assert.Equal(3.3, p.PassDepths[^1], 9);
        // Every point of the circle is exactly the ring radius from the hole's centre.
        Assert.All(ring, q => Assert.Equal(1.5, Math.Sqrt((q.X - 100) * (q.X - 100) + (q.Y - 80) * (q.Y - 80)), 6));
    }

    [Theory]
    [InlineData(6, 3, 1)]      // up to twice the tool: one spiral reaches the centre
    [InlineData(8, 3, 2)]      // ring radius 2.5 > 1.5: two spirals
    [InlineData(20, 6, 3)]
    public void Spirals_step_at_most_half_the_tool_and_the_innermost_reaches_the_centre(double hole, double tool, int count)
    {
        var radii = DrillToolpaths.HelixRadii(hole, tool);
        Assert.Equal(count, radii.Count);
        Assert.Equal((hole - tool) / 2, radii[^1], 9);         // the outermost cuts the hole's wall
        Assert.True(radii[0] <= tool / 2 + 1e-9);              // no core left in the middle
        for (int i = 1; i < radii.Count; i++) Assert.True(radii[i] - radii[i - 1] <= tool / 2 + 1e-9);
    }

    [Fact]
    public void The_gcode_spirals_down_with_g1_only_then_cuts_flat_goes_to_the_centre_and_lifts()
    {
        var (doc, report) = Doc(hole: 6, tool: 3);
        var r = GCodeGenerator.Generate(doc, report);
        Assert.True(r.Success, string.Join("\n", r.BlockingReasons.Concat(r.VerifierIssues)));
        var code = r.Lines.Where(l => !l.StartsWith("(")).ToList();
        Assert.DoesNotContain(code, l => l.Split(' ')[0] is "G2" or "G3" or "G02" or "G03" || l.StartsWith("G8"));
        var (cx, cy) = doc.ToWork(100, 80);
        // Below the surface every move is a G1, and it stays inside the hole: centre distance at most the ring radius.
        var cuts = code.Where(l => l.StartsWith("G1") && l.Contains('X')).ToList();
        Assert.NotEmpty(cuts);
        Assert.All(cuts, l => Assert.True(Math.Sqrt(Math.Pow(Num(l, 'X') - cx, 2) + Math.Pow(Num(l, 'Y') - cy, 2)) <= 1.5 + 1e-3, l));
        // Z only ever goes down while spiralling, to the full depth, at the ramp feed (70 % of 700).
        var zs = cuts.Where(l => l.Contains('Z')).Select(l => Num(l, 'Z')).ToList();
        Assert.Equal(-3.3, zs.Min(), 6);
        for (int i = 1; i < zs.Count; i++) Assert.True(zs[i] <= zs[i - 1] + 1e-9);
        Assert.Contains(code, l => l.StartsWith("G1") && l.Contains(" F490"));
        // The last cut ends at the centre, then the tool lifts straight up.
        Assert.Equal(cx, Num(cuts[^1], 'X'), 3);
        Assert.Equal(cy, Num(cuts[^1], 'Y'), 3);
        var after = code[code.LastIndexOf(cuts[^1]) + 1];
        Assert.StartsWith("G0 Z", after);
        Assert.True(Num(after, 'Z') > 0);
    }

    [Fact]
    public void Climb_is_counter_clockwise_and_conventional_reverses_it()
    {
        var (_, climb) = Doc(tweak: d => d.CamOperations[0].Direction = MillingDirection.Climb);
        var (_, conv) = Doc(tweak: d => d.CamOperations[0].Direction = MillingDirection.Conventional);
        Assert.True(CamDirection.MachineArea(climb.Operations[0].Preview!.Centerlines[0]) > 0);
        Assert.True(CamDirection.MachineArea(conv.Operations[0].Preview!.Centerlines[0]) < 0);
    }

    [Fact]
    public void A_steep_spiral_is_warned_about()
    {
        var (_, steep) = Doc(hole: 4, tool: 3, depth: 6, perTurn: 3);   // ring radius 0.5: 3 mm per turn is very steep
        Assert.Contains(steep.Operations[0].Findings, f => f.Code == "SteepHelix" && f.Severity == CamSeverity.Warning);
        var (_, gentle) = Doc(hole: 10, tool: 3, depth: 3, perTurn: 0.5);
        Assert.DoesNotContain(gentle.Operations[0].Findings, f => f.Code == "SteepHelix");
    }

    [Fact]
    public void The_simulated_stock_has_a_round_hole_of_the_right_size_and_no_core()
    {
        var (doc, report) = Doc(hole: 12, tool: 3, depth: 4, perTurn: 1);
        var map = StockSimulator.Simulate(doc, report);
        Assert.Equal(-4, map.MinHeight, 2);
        // The removed volume is close to the hole's: π·6²·4 ≈ 452 mm³ (the grid's cells make it a little off).
        Assert.InRange(map.RemovedVolume, 452 * 0.85, 452 * 1.15);
    }

    [Fact]
    public void An_imported_circle_counts_and_an_imported_square_does_not()
    {
        var circle = new DesignShape { Kind = ShapeKind.VectorPath, X = 0, Y = 0, Width = 10, Height = 10,
            Contours = new() { new VectorContour { Closed = true, Points = Enumerable.Range(0, 64).Select(i =>
                new VectorPoint(0.5 + 0.5 * Math.Cos(2 * Math.PI * i / 64), 0.5 + 0.5 * Math.Sin(2 * Math.PI * i / 64))).ToList() } } };
        Assert.True(DrillToolpaths.IsCircle(circle));
        var square = new DesignShape { Kind = ShapeKind.VectorPath, X = 0, Y = 0, Width = 10, Height = 10,
            Contours = new() { new VectorContour { Closed = true, Points = new() { new(0, 0), new(0.5, 0), new(1, 0), new(1, 0.5), new(1, 1), new(0.5, 1), new(0, 1), new(0, 0.5) } } } };
        Assert.False(DrillToolpaths.IsCircle(square));
    }

    [Fact]
    public void The_estimated_run_time_counts_the_spiral_at_the_ramp_feed()
    {
        var (doc, report) = Doc(hole: 6, tool: 3, depth: 3, perTurn: 1);
        var t = RunTimeEstimator.Estimate(doc, report);
        // 3 turns on a 1.5 mm circle, 1 mm down each: about 3 × 9.48 mm at 490 mm/min, plus the flat turn at 700.
        double spiral = 3 * Math.Sqrt(Math.Pow(2 * Math.PI * 1.5, 2) + 1) / 490, flat = 2 * Math.PI * 1.5 / 700;
        Assert.InRange(t.CuttingMinutes, (spiral + flat) * 0.98, (spiral + flat) * 1.3);
    }
}
