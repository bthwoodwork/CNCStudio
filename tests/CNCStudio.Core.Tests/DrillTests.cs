using System.Globalization;
using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

/// <summary>Drilling as in Easel: straight pecks at the hole centre, only G0/G1 (GRBL 1.1 has no G81/G83).</summary>
public class DrillTests
{
    private static (CncDocument Doc, CamReport Report) Doc(double holeDiameter = 5, double tool = 5, double depth = 9, double peck = 3,
        Action<CncDocument>? tweak = null)
    {
        var doc = new CncDocument { Name = "Hål" };
        doc.Motion.StockTopMachineZ = -40;
        var s = new DesignShape { Kind = ShapeKind.Ellipse, X = 100 - holeDiameter / 2, Y = 80 - holeDiameter / 2, Width = holeDiameter, Height = holeDiameter };
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, Kind = CamOperationKind.Drill, ToolName = "5 mm borr", ToolDiameter = tool,
            TotalDepth = depth, DepthPerPass = peck, UseTabs = false, PlungeRate = 250 });
        tweak?.Invoke(doc);
        return (doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
    }

    private static double Z(string l) => double.Parse(l[(l.IndexOf('Z') + 1)..].Split(' ')[0], CultureInfo.InvariantCulture);

    [Fact]
    public void A_hole_is_drilled_at_the_centre_in_pecks_with_lifts_between()
    {
        var (doc, report) = Doc();
        var preview = Assert.Single(report.Operations).Preview!;
        Assert.True(preview.IsDrill);
        Assert.Equal(new[] { 3.0, 6.0, 9.0 }, preview.PassDepths);
        var r = GCodeGenerator.Generate(doc, report);
        Assert.True(r.Success, string.Join("\n", r.BlockingReasons.Concat(r.VerifierIssues)));
        var code = r.Lines.Where(l => !l.StartsWith("(")).ToList();
        var (wx, wy) = doc.ToWork(100, 80);
        Assert.Contains($"G0 X{wx.ToString(CultureInfo.InvariantCulture)} Y{wy.ToString(CultureInfo.InvariantCulture)}", code);
        // Feeds only straight down (no XY while below the surface), at the plunge rate.
        var feeds = code.Where(l => l.StartsWith("G1")).ToList();
        Assert.All(feeds, l => Assert.DoesNotContain("X", l));
        Assert.Contains("G1 Z-3 F250", feeds);
        Assert.Contains("G1 Z-6", feeds);
        Assert.Contains("G1 Z-9", feeds);
        // Between pecks: out of the hole (to the plunge clearance), then rapid back down near the last depth.
        int first = code.IndexOf("G1 Z-3 F250");
        Assert.Equal("G0 Z2", code[first + 1]);
        Assert.Equal("G0 Z-2.5", code[first + 2]);
        Assert.DoesNotContain(code, l => l.StartsWith("G8"));            // no canned cycles
    }

    [Fact]
    public void One_peck_when_the_step_covers_the_depth()
    {
        var (_, report) = Doc(depth: 4, peck: 4);
        Assert.Equal(new[] { 4.0 }, report.Operations[0].Preview!.PassDepths);
    }

    [Fact]
    public void A_shape_bigger_than_the_tool_is_warned_about()
    {
        var (_, report) = Doc(holeDiameter: 10, tool: 5);
        Assert.Contains(report.Operations[0].Findings, f => f.Code == "HoleSizeMismatch" && f.Severity == CamSeverity.Warning);
        var (_, ok) = Doc(holeDiameter: 5, tool: 5);
        Assert.DoesNotContain(ok.Operations[0].Findings, f => f.Code == "HoleSizeMismatch");
    }

    [Fact]
    public void Drilling_never_has_tabs()
    {
        Assert.NotNull(CamValidation.GetError(new CamOperation { Kind = CamOperationKind.Drill, UseTabs = true }, 18));
    }

    [Fact]
    public void Holes_are_drilled_before_the_outside_contour_and_with_rapid_re_entry_off_they_feed_from_the_clearance()
    {
        var (doc, report) = Doc(tweak: d =>
        {
            var part = new DesignShape { Kind = ShapeKind.Rectangle, X = 60, Y = 50, Width = 80, Height = 60 };
            d.Shapes.Add(part);
            d.CamOperations.Insert(0, new CamOperation { ShapeId = part.Id, ToolName = "5 mm borr", ToolDiameter = 5, UseTabs = false, TotalDepth = 6, DepthPerPass = 3 });
            d.Motion.RapidIntoCutClearanceMm = null;
        });
        Assert.Equal(1, report.Motion.OperationOrder[0]);            // the drill (index 1) before the outside contour (index 0)
        var code = GCodeGenerator.Generate(doc, report).Lines.Where(l => !l.StartsWith("(")).ToList();
        Assert.DoesNotContain(code, l => l.StartsWith("G0 Z-"));
    }

    [Fact]
    public void The_simulated_stock_has_the_hole()
    {
        var (doc, report) = Doc();
        var map = StockSimulator.Simulate(doc, report);
        Assert.Equal(-9, map.MinHeight, 2);
        Assert.True(map.RemovedVolume > 0);
    }
}
