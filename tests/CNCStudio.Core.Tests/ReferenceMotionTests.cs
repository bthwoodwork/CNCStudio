using System.Globalization;
using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

/// <summary>
/// Ramp feed, end position and rapid re-entry into an already cut slot – modelled on a program that ran
/// well on the user's X-Carve (Kapsylöppnare.nc). Every number is a setting.
/// </summary>
public class ReferenceMotionTests
{
    private static (CncDocument Doc, CamReport Report) Doc(Action<CncDocument>? tweak = null, CamOperationKind kind = CamOperationKind.Outside)
    {
        var doc = new CncDocument { Name = "Test" };
        doc.Motion.StockTopMachineZ = -40;
        doc.Machine.Spindle = SpindleControl.Grbl;
        var s = new DesignShape { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = 100, Height = 60 };
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, Kind = kind, TotalDepth = 6, DepthPerPass = 2, UseTabs = false,
            FeedRate = 1000, PlungeRate = 300, SpindleRpm = 16000, Plunge = PlungeMode.Ramp, RampAngleDeg = 3 });
        tweak?.Invoke(doc);
        return (doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
    }

    private static List<string> Code(CncDocument doc, CamReport report)
    {
        var r = GCodeGenerator.Generate(doc, report);
        Assert.True(r.Success, string.Join("\n", r.BlockingReasons.Concat(r.VerifierIssues)));
        return r.Lines.Where(l => !l.StartsWith("(")).ToList();
    }

    private static double Z(string line) => double.Parse(line[(line.IndexOf('Z') + 1)..].Split(' ')[0], CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(70, "F700")]
    [InlineData(50, "F500")]
    public void Ramps_use_their_own_feed_and_cuts_the_full_feed(double percent, string rampFeed)
    {
        var (doc, report) = Doc(d => d.CamOperations[0].RampFeedPercent = percent);
        var code = Code(doc, report);
        var firstRamp = code.First(l => l.StartsWith("G1 X") && l.Contains(" Z"));
        Assert.EndsWith(rampFeed, firstRamp);
        Assert.Contains(code, l => l.StartsWith("G1 X") && !l.Contains(" Z") && l.EndsWith("F1000"));
    }

    [Fact]
    public void A_ramp_feed_outside_5_to_100_percent_is_refused()
    {
        Assert.NotNull(CamValidation.GetError(new CamOperation { Plunge = PlungeMode.Ramp, RampFeedPercent = 120 }, 18));
        Assert.Null(CamValidation.GetError(new CamOperation { Plunge = PlungeMode.Vertical, RampFeedPercent = 120 }, 18));
    }

    [Fact]
    public void The_program_ends_at_the_chosen_position()
    {
        var (stay, stayReport) = Doc(d => d.Motion.EndPosition = EndPosition.StayAbove);
        var code = Code(stay, stayReport);
        Assert.Equal("G0 Z10", code[^3]);                        // stays above the last cut

        var (zero, zeroReport) = Doc(d => d.WorkZero = WorkZero.Center);
        Assert.Equal("G0 X0 Y0", Code(zero, zeroReport)[^3]);   // work zero, also when it is the stock centre

        var (point, pointReport) = Doc(d => { d.Motion.EndPosition = EndPosition.Point; d.Motion.EndXMm = 20; d.Motion.EndYMm = 300; });
        Assert.Equal("G0 X20 Y300", Code(point, pointReport)[^3]);
    }

    [Fact]
    public void An_end_point_outside_the_machine_is_a_critical_finding()
    {
        var (doc, report) = Doc(d => { d.Motion.EndPosition = EndPosition.Point; d.Motion.EndXMm = 5000; });
        Assert.Contains(report.Operations.SelectMany(o => o.Findings).Concat(report.GlobalFindings),
            f => f.Code == "EndPointOutsideMachine" && f.Severity == CamSeverity.Critical);
        Assert.False(GCodeGenerator.Generate(doc, report).Success);
    }

    [Fact]
    public void A_pocket_level_rapids_down_into_the_cut_and_feeds_only_the_last_part()
    {
        var (doc, report) = Doc(d => { d.CamOperations[0].Plunge = PlungeMode.Vertical; d.Motion.RapidIntoCutClearanceMm = 0.5; },
            CamOperationKind.Pocket);
        var code = Code(doc, report);
        // Second level: G0 down to 0.5 mm above the first level (−2), then feed.
        int i = code.FindIndex(l => l == "G0 Z-1.5");
        Assert.True(i > 0, string.Join("\n", code.Take(60)));
        Assert.StartsWith("G1 Z-4", code[i + 1]);
        Assert.Contains("G0 Z-3.5", code);
        Assert.DoesNotContain(code, l => l.StartsWith("G0 Z-") && Z(l) < -3.5 - 1e-9);   // never rapids lower than cut − 0.5
    }

    [Fact]
    public void Rapid_re_entry_can_be_switched_off()
    {
        var (doc, report) = Doc(d => { d.CamOperations[0].Plunge = PlungeMode.Vertical; d.Motion.RapidIntoCutClearanceMm = null; },
            CamOperationKind.Pocket);
        Assert.DoesNotContain(Code(doc, report), l => l.StartsWith("G0 Z-"));
    }

    [Fact]
    public void The_re_entry_clearance_must_fit_below_the_plunge_height()
    {
        Assert.NotNull(MotionValidation.GetError(new MotionSettings { PlungeClearanceMm = 2, RapidIntoCutClearanceMm = 3 }));
        Assert.NotNull(MotionValidation.GetError(new MotionSettings { RapidIntoCutClearanceMm = 0 }));
        Assert.Null(MotionValidation.GetError(new MotionSettings { RapidIntoCutClearanceMm = 1 }));
    }

    [Fact]
    public void Old_project_files_get_the_reference_defaults()
    {
        var (doc, _) = Doc();
        var path = Path.Combine(Path.GetTempPath(), "cncstudio-ref-" + Guid.NewGuid().ToString("N") + ".cncstudio.json");
        DocumentStorage.Save(path, doc);
        var text = File.ReadAllText(path).Replace("\"RampFeedPercent\"", "\"Gammal1\"").Replace("\"EndPosition\"", "\"Gammal2\"")
            .Replace("\"RapidIntoCutClearanceMm\"", "\"Gammal3\"");
        File.WriteAllText(path, text);
        var loaded = DocumentStorage.Load(path);
        Assert.Equal(70, loaded.CamOperations[0].RampFeedPercent);
        Assert.Equal(EndPosition.WorkZero, loaded.Motion.EndPosition);
        Assert.Equal(0.5, loaded.Motion.RapidIntoCutClearanceMm);
    }
}
