using System.Globalization;
using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

public class GCodeGeneratorTests
{
    /// <summary>A ready-to-export document: homed, Z zero known, one 100 × 60 rectangle.</summary>
    private static (CncDocument Doc, CamReport Report) Ready(double depth = 6, bool tabs = false, Action<CncDocument>? tweak = null)
    {
        var doc = new CncDocument { Name = "Test" };
        doc.Motion.StockTopMachineZ = -40;
        doc.Machine.Spindle = SpindleControl.Grbl; // these tests cover M3/M5; manual mode has its own tests
        var s = new DesignShape { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = 100, Height = 60 };
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, TotalDepth = depth, DepthPerPass = 2, UseTabs = tabs,
            FeedRate = 1200, PlungeRate = 300, SpindleRpm = 16000 });
        tweak?.Invoke(doc);
        return (doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
    }

    [Fact]
    public void Blockers_say_what_is_missing_so_the_ui_can_point_there()
    {
        var (doc, report) = Ready(tweak: d => { d.Motion.StockTopMachineZ = null; d.Machine.HomeOnStartup = false; });
        var blockers = GCodeGenerator.Blockers(doc, report);
        Assert.Contains(blockers, b => b.Kind == ExportBlockerKind.StockTopUnknown);
        Assert.Contains(blockers, b => b.Kind == ExportBlockerKind.HomingOff);
        Assert.Equal(blockers.Select(b => b.Message), GCodeGenerator.BlockingReasons(doc, report));

        var (ok, okReport) = Ready();
        Assert.Empty(GCodeGenerator.Blockers(ok, okReport));
    }

    [Fact]
    public void Critical_finding_blocker_points_at_its_shape_and_code()
    {
        var (doc, report) = Ready(tweak: d => d.CamOperations[0].ToolKind = ToolKind.VBit);
        var b = Assert.Single(GCodeGenerator.Blockers(doc, report), x => x.Kind == ExportBlockerKind.CriticalFinding);
        Assert.Equal("VBitNotSupported", b.FindingCode);
        Assert.Equal(doc.Shapes[0].Id, b.ShapeId);
    }

    private static List<string> Code(GCodeResult r) =>
        r.Lines.Where(l => !l.StartsWith("(")).ToList();

    [Fact]
    public void Generates_a_program_that_passes_the_verifier()
    {
        var (doc, report) = Ready();
        var r = GCodeGenerator.Generate(doc, report, new DateTime(2026, 11, 5, 12, 0, 0));
        Assert.True(r.Success, string.Join("\n", r.BlockingReasons.Concat(r.VerifierIssues)));
        Assert.Empty(r.VerifierIssues);
    }

    [Fact]
    public void Program_starts_with_modes_then_lifts_z_alone_then_starts_the_spindle()
    {
        var (doc, report) = Ready();
        var code = Code(GCodeGenerator.Generate(doc, report));
        Assert.Equal(new[] { "G21", "G90", "G94", "G17", "G54" }, code.Take(5));
        Assert.Equal("G0 Z10", code[5]);          // lift first, Z only
        Assert.Equal("M3 S16000", code[6]);       // spindle at safe height
        Assert.Equal("G4 P4", code[7]);           // spin-up dwell in seconds
        Assert.StartsWith("G0 X", code[8]);       // then XY to the first path start
        Assert.DoesNotContain("Z", code[8]);
    }

    [Fact]
    public void Program_ends_lifted_with_spindle_off_and_m30()
    {
        var (doc, report) = Ready();
        var code = Code(GCodeGenerator.Generate(doc, report));
        Assert.Equal("M30", code[^1]);
        Assert.Equal("M5", code[^2]);
        Assert.Equal("G0 X0 Y0", code[^3]);      // default: back to work zero at the end height
        Assert.Equal("G0 Z10", code[^4]);
    }

    [Fact]
    public void Feed_is_set_on_the_first_g1_and_plunge_and_cut_use_their_own_rates()
    {
        var (doc, report) = Ready();
        var code = Code(GCodeGenerator.Generate(doc, report));
        var firstG1 = code.First(l => l.StartsWith("G1 "));
        Assert.Equal("G1 Z-2 F300", firstG1);
        Assert.Contains(code, l => l.StartsWith("G1 X") && l.EndsWith("F1200"));
        Assert.Equal(3, code.Count(l => l.StartsWith("G1 Z-") && !l.Contains('X'))); // three plunges, 2/4/6 mm
    }

    [Fact]
    public void Y_is_mirrored_so_zero_is_the_front_left_corner()
    {
        // Rectangle at design Y 100..160 on 600 mm material: G-code Y 440..500 (± tool radius).
        var (doc, report) = Ready();
        var ys = Code(GCodeGenerator.Generate(doc, report))
            .Where(l => l.StartsWith("G1 X")).Select(l => double.Parse(l.Split(' ')[2][1..], CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(600 - 160 - 1.588, ys.Min(), 2);
        Assert.Equal(600 - 100 + 1.588, ys.Max(), 2);
    }

    [Fact]
    public void Rapids_never_move_sideways_below_the_plunge_clearance()
    {
        var (doc, report) = Ready();
        double z = double.NaN;
        foreach (var l in Code(GCodeGenerator.Generate(doc, report)))
        {
            var parts = l.Split(' ');
            var zw = parts.FirstOrDefault(p => p.StartsWith("Z"));
            if (zw is not null) z = double.Parse(zw[1..], CultureInfo.InvariantCulture);
            if (l.StartsWith("G0 X")) Assert.True(z >= doc.Motion.PlungeClearanceMm, l);
        }
    }

    [Fact]
    public void Tabs_lift_the_tool_only_in_the_deepest_passes()
    {
        var (doc, report) = Ready(depth: 18, tabs: true);
        var r = GCodeGenerator.Generate(doc, report);
        Assert.True(r.Success, string.Join("\n", r.BlockingReasons.Concat(r.VerifierIssues)));
        var code = Code(r);
        // 4 tabs × 2 deep passes (−16, −18) lift to the tab top at −15, at the plunge rate.
        var lifts = code.Where(l => l.StartsWith("G1 Z-15")).ToList();
        Assert.Equal(8, lifts.Count);
        Assert.All(lifts, l => Assert.True(l == "G1 Z-15" || l == "G1 Z-15 F300", l));
        Assert.Contains("G1 Z-15 F300", lifts);
    }

    [Fact]
    public void All_lines_are_short_ascii_and_comments_are_transliterated()
    {
        var (doc, report) = Ready(tweak: d => d.Name = "Skärbräda åäö");
        var r = GCodeGenerator.Generate(doc, report);
        Assert.All(r.Lines, l => Assert.True(l.Length <= GCodeVerifier.MaxLineLength && l.All(c => c >= 0x20 && c <= 0x7E), l));
        Assert.Contains(r.Lines, l => l.Contains("Skarbrada aao"));
        Assert.DoesNotContain(r.Lines, l => l.Contains('%'));
    }

    [Fact]
    public void Numbers_never_have_negative_zero_or_commas()
    {
        Assert.Equal("0", GCodeGenerator.F(-0.0001));
        Assert.Equal("1.5", GCodeGenerator.F(1.5));
        Assert.Equal("-2", GCodeGenerator.F(-2.0));
        Assert.Equal("10.123", GCodeGenerator.F(10.12345));
    }

    [Fact]
    public void Export_is_blocked_by_critical_findings()
    {
        var (doc, report) = Ready(tweak: d => d.Fixtures.Add(new FixtureBlock { X = 140, Y = 140, Width = 30, Length = 30 }));
        var r = GCodeGenerator.Generate(doc, report);
        Assert.False(r.Success);
        Assert.Empty(r.Lines);
        Assert.Contains(r.BlockingReasons, b => b.StartsWith("Kritiskt"));
    }

    [Fact]
    public void Export_is_blocked_without_known_z_limit()
    {
        var (doc, report) = Ready(tweak: d => d.Motion.StockTopMachineZ = null);
        var r = GCodeGenerator.Generate(doc, report);
        Assert.False(r.Success);
        Assert.Contains(r.BlockingReasons, b => b.Contains("MPos Z"));
    }

    [Fact]
    public void Export_is_blocked_when_homing_is_off()
    {
        var (doc, report) = Ready(tweak: d => d.Machine.HomeOnStartup = false);
        Assert.Contains(GCodeGenerator.Generate(doc, report).BlockingReasons, b => b.Contains("$H"));
    }

    [Fact]
    public void Export_is_blocked_for_stale_paths()
    {
        var doc = new CncDocument();
        doc.Motion.StockTopMachineZ = -40;
        var contour = new VectorContour();
        for (int i = 0; i < CamPlanner.AutoRecomputePointLimit + 1; i++)
        {
            double a = 2 * Math.PI * i / (CamPlanner.AutoRecomputePointLimit + 1);
            contour.Points.Add(new VectorPoint(0.5 + 0.5 * Math.Cos(a), 0.5 + 0.5 * Math.Sin(a)));
        }
        var s = new DesignShape { Kind = ShapeKind.VectorPath, X = 100, Y = 100, Width = 100, Height = 100, Contours = { contour } };
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id });
        var planner = new CamPlanner((sh, op, t) => ContourToolpaths.Generate(
            new DesignShape { Kind = ShapeKind.Rectangle, X = sh.X, Y = sh.Y, Width = sh.Width, Height = sh.Height }, op, t));
        planner.Review(doc);
        s.Width = 120;
        var r = GCodeGenerator.Generate(doc, planner.Review(doc));
        Assert.Contains(r.BlockingReasons, b => b.Contains("inaktuell"));
    }

    [Fact]
    public void Manual_router_gets_an_m0_pause_and_no_spindle_codes()
    {
        var (doc, report) = Ready(tweak: d => d.Machine.Spindle = SpindleControl.Manual);
        var r = GCodeGenerator.Generate(doc, report);
        Assert.True(r.Success, string.Join("\n", r.BlockingReasons.Concat(r.VerifierIssues)));
        var code = Code(r);
        Assert.DoesNotContain(code, l => l.Split(' ')[0] is "M3" or "M5" or "G4" || l.Contains(" S"));
        Assert.Equal("G0 Z10", code[5]);   // lift first
        Assert.Equal("M0", code[6]);       // then pause at the safe height
        Assert.Equal("M30", code[^1]);
        int m0 = r.Lines.ToList().IndexOf("M0");
        Assert.Contains("16000 varv/min", r.Lines[m0 - 1]);   // the comment just before tells the speed
        Assert.Contains("fortsatt med ~", r.Lines[m0 - 1]);
        Assert.Contains(r.Lines, l => l.Contains("stang av frasen"));
    }

    [Fact]
    public void Default_spindle_control_is_manual()
    {
        Assert.Equal(SpindleControl.Manual, new MachineProfile().Spindle);
    }

    private static CncDocument TwoParts(SpindleControl spindle)
    {
        var doc = new CncDocument();
        doc.Motion.StockTopMachineZ = -40;
        doc.Machine.Spindle = spindle;
        var a = new DesignShape { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = 50, Height = 50 };
        var b = new DesignShape { Kind = ShapeKind.Rectangle, X = 300, Y = 100, Width = 50, Height = 50 };
        doc.Shapes.Add(a); doc.Shapes.Add(b);
        doc.CamOperations.Add(new CamOperation { ShapeId = a.Id, SpindleRpm = 16000 });
        doc.CamOperations.Add(new CamOperation { ShapeId = b.Id, SpindleRpm = 20000 });
        return doc;
    }

    [Theory]
    [InlineData(SpindleControl.Manual, "M0")]
    [InlineData(SpindleControl.Grbl, "M3 S20000")]
    public void Speed_change_between_operations_happens_at_travel_height(SpindleControl spindle, string change)
    {
        var doc = TwoParts(spindle);
        var r = GCodeGenerator.Generate(doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
        Assert.True(r.Success, string.Join("\n", r.BlockingReasons.Concat(r.VerifierIssues)));
        var code = Code(r);
        int at = spindle == SpindleControl.Manual ? code.LastIndexOf("M0") : code.IndexOf(change);
        Assert.True(at > 0);
        var lastZ = code.Take(at).Last(l => l.Contains('Z'));
        double z = double.Parse(lastZ.Split(' ').First(w => w.StartsWith("Z"))[1..], CultureInfo.InvariantCulture);
        Assert.True(z >= doc.Motion.TravelHeightMm, $"speed changed at Z {z}");
    }

    [Fact]
    public void Feed_above_machine_max_rate_is_a_warning_in_the_header()
    {
        var (doc, report) = Ready(tweak: d => { d.Machine.MaxRateX = 800; d.Machine.MaxRateY = 800; });
        var r = GCodeGenerator.Generate(doc, report);
        Assert.True(r.Success);
        Assert.Contains(r.Warnings, w => w.Contains("$110/$111"));
        Assert.Contains(r.Lines, l => l.StartsWith("(VARNING"));
    }
}

public class GCodeVerifierTests
{
    private static readonly GCodeVerifier.Limits L = new(PlungeClearance: 2, MaxZ: 30, MinZ: -18,
        MinX: -800, MaxX: 800, MinY: -800, MaxY: 800, EndZ: 10);

    private static readonly string[] Good =
    {
        "(test)", "G21", "G90", "G94", "G17", "G54", "G0 Z10", "M3 S16000", "G4 P4", "G0 X0 Y0", "G0 Z2",
        "G1 Z-2 F300", "G1 X10 Y0 F1200", "G0 Z10", "M5", "M30"
    };

    private static IReadOnlyList<string> V(params string[] lines) => GCodeVerifier.Verify(lines, L);
    private static string[] Replace(int index, string line) { var c = (string[])Good.Clone(); c[index] = line; return c; }

    [Fact] public void Good_program_passes() => Assert.Empty(V(Good));

    [Fact]
    public void Rapid_sideways_in_the_material_is_caught() =>
        Assert.Contains(V(Good[..11].Concat(new[] { "G1 Z-2 F300", "G0 X50 Y50", "G0 Z10", "M3 S1", "M5", "M30" }).ToArray()),
            i => i.Contains("snabbförflyttning"));

    [Fact]
    public void G1_without_feed_is_caught() => Assert.Contains(V(Replace(11, "G1 Z-2")), i => i.Contains("matning"));

    [Fact]
    public void Cutting_without_spindle_is_caught() => Assert.Contains(V(Replace(7, "M5")), i => i.Contains("spindeln"));

    [Fact]
    public void First_motion_must_lift_z() =>
        Assert.Contains(V(Replace(6, "G0 X5 Y5 Z10")), i => i.Contains("första rörelsen"));

    [Fact]
    public void Too_deep_or_too_high_is_caught()
    {
        Assert.Contains(V(Replace(11, "G1 Z-25 F300")), i => i.Contains("utanför tillåtet område"));
        Assert.Contains(V(Replace(6, "G0 Z40")), i => i.Contains("utanför tillåtet område"));
    }

    [Fact]
    public void Unknown_codes_and_non_ascii_are_caught()
    {
        Assert.Contains(V(Replace(0, "G28")), i => i.Contains("G28"));
        Assert.Contains(V(Replace(0, "M8")), i => i.Contains("M8"));
        Assert.Contains(V(Replace(0, "(skärbräda)")), i => i.Contains("ASCII"));
        Assert.Contains(V(Replace(0, "%")), i => i.Contains("oväntat tecken"));
    }

    [Fact]
    public void Dwell_needs_positive_seconds() => Assert.Contains(V(Replace(8, "G4 P0")), i => i.Contains("G4"));

    [Fact]
    public void Missing_end_is_caught()
    {
        Assert.Contains(V(Good[..^1]), i => i.Contains("M30"));
        Assert.Contains(V(Good[..^2].Append("M30").ToArray()), i => i.Contains("M5"));
    }

    [Fact]
    public void Motion_before_modes_is_caught() => Assert.Contains(V(Replace(1, "(no units)")), i => i.Contains("G21"));

    private static readonly GCodeVerifier.Limits Manual = L with { ManualSpindle = true };
    private static readonly string[] GoodManual =
    {
        "G21", "G90", "G94", "G17", "G54", "G0 Z10", "(PAUS: starta frasen)", "M0", "G0 X0 Y0", "G0 Z2",
        "G1 Z-2 F300", "G1 X10 Y0 F1200", "G0 Z10", "M30"
    };

    [Fact] public void Good_manual_program_passes() => Assert.Empty(GCodeVerifier.Verify(GoodManual, Manual));

    [Fact]
    public void Manual_mode_rejects_m3_and_m5() =>
        Assert.Contains(GCodeVerifier.Verify(GoodManual.Select(l => l == "M0" ? "M3 S1000" : l).ToArray(), Manual),
            i => i.Contains("manuell fräs"));

    [Fact]
    public void Manual_mode_requires_a_pause_before_cutting() =>
        Assert.Contains(GCodeVerifier.Verify(GoodManual.Where(l => l != "M0").ToArray(), Manual),
            i => i.Contains("M0"));

    [Fact]
    public void Grbl_mode_rejects_m0() =>
        Assert.Contains(V(Replace(8, "M0")), i => i.Contains("M0"));

    [Fact]
    public void Comments_are_stripped_like_grbl()
    {
        Assert.Equal("G0Z10", GCodeVerifier.StripComments("g0 z10 (lift) ; end", out var e));
        Assert.False(e);
        GCodeVerifier.StripComments("G0 (open", out var e2);
        Assert.True(e2);
    }
}
