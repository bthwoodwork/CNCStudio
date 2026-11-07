using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

/// <summary>Facing: the whole area flat, rows past the edges, runs first, only when the user adds it.</summary>
public class FaceTests
{
    private static (CncDocument Doc, CamReport Report) Doc(Action<CamOperation>? tweak = null, bool withContour = false)
    {
        var doc = new CncDocument { MaterialWidth = 200, MaterialHeight = 100, MaterialThickness = 20 };
        doc.Motion.StockTopMachineZ = -40;
        if (withContour)
        {
            var part = new DesignShape { Kind = ShapeKind.Rectangle, X = 40, Y = 20, Width = 100, Height = 50 };
            doc.Shapes.Add(part);
            doc.CamOperations.Add(new CamOperation { ShapeId = part.Id, ToolName = "6 mm rak fräs", ToolDiameter = 6, UseTabs = false, TotalDepth = 5, DepthPerPass = 2.5 });
        }
        var face = new DesignShape { Kind = ShapeKind.Rectangle, X = 0, Y = 0, Width = 200, Height = 100 };
        doc.Shapes.Add(face);
        var op = new CamOperation { ShapeId = face.Id, Kind = CamOperationKind.Face, ToolName = "25 mm planfräs", ToolDiameter = 25,
            TotalDepth = 1, DepthPerPass = 0.5, UseTabs = false, StepoverPercent = 60, FaceOverhangMm = 2 };
        tweak?.Invoke(op);
        doc.CamOperations.Add(op);
        return (doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
    }

    [Fact]
    public void Rows_cover_the_area_and_run_out_past_both_edges()
    {
        var (_, report) = Doc();
        var p = report.Operations.Single(o => o.Operation.Kind == CamOperationKind.Face).Preview!;
        Assert.True(p.IsFace);
        Assert.Equal(new[] { 0.5, 1.0 }, p.PassDepths);
        double run = 12.5 + 2;                                         // radius + overhang
        Assert.Equal(-run, p.Centerline.Min(q => q.X), 6);
        Assert.Equal(200 + run, p.Centerline.Max(q => q.X), 6);
        var rows = p.Centerline.Select(q => q.Y).Distinct().OrderBy(y => y).ToList();
        Assert.Equal(0, rows[0], 6);
        Assert.Equal(100, rows[^1], 6);                                // the far edge is always cut
        Assert.All(rows.Zip(rows.Skip(1)), r => Assert.True(r.Second - r.First <= 25 * 0.6 + 1e-6));
    }

    [Fact]
    public void Rows_can_run_along_Y()
    {
        var (_, report) = Doc(op => op.FaceAlongY = true);
        var p = report.Operations.Single().Preview!;
        Assert.Equal(-14.5, p.Centerline.Min(q => q.Y), 6);
        Assert.Equal(0, p.Centerline.Min(q => q.X), 6);
    }

    [Fact]
    public void Facing_runs_first_plunges_beside_the_material_and_exports()
    {
        var (doc, report) = Doc(withContour: true);
        Assert.Equal(1, report.Motion.OperationOrder[0]);              // the face op (index 1) before the contour
        var faceMoves = report.Motion.Moves.Where(m => m.OperationIndex == 1).ToList();
        var plunge = faceMoves.First(m => m.Kind == MoveKind.Feed && !m.MovesXY);
        Assert.True(plunge.X0 < 0);                                    // straight down beside the material
        Assert.DoesNotContain(report.AllFindings, f => f.Severity == CamSeverity.Critical);
        Assert.DoesNotContain(report.Operations[1].Findings, f => f.Code == "CutsOtherPart");  // over the part on purpose
        Assert.DoesNotContain(report.Operations[0].Findings, f => f.Code == "CutsOtherPart");  // the facing area is not a part
        var (jobs, blocking) = GCodeGenerator.GenerateByTool(doc, report);
        Assert.Empty(blocking);
        Assert.Equal("25 mm planfräs", jobs[0].ToolLabel);             // its own program, the first one
        Assert.All(jobs, j => Assert.True(j.Result.Success, string.Join("\n", j.Result.VerifierIssues)));
    }

    [Fact]
    public void Facing_is_not_added_unless_asked_for()
    {
        var doc = new CncDocument();
        var s = new DesignShape { Kind = ShapeKind.Rectangle, X = 10, Y = 10, Width = 50, Height = 30 };
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, UseTabs = false });
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
        Assert.DoesNotContain(report.Operations, o => o.Operation.Kind == CamOperationKind.Face);
    }

    [Fact]
    public void Invalid_facing_settings_are_refused()
    {
        Assert.NotNull(CamValidation.GetError(new CamOperation { Kind = CamOperationKind.Face, UseTabs = true }, 18));
        Assert.NotNull(CamValidation.GetError(new CamOperation { Kind = CamOperationKind.Face, UseTabs = false, StepoverPercent = 100 }, 18));
        Assert.NotNull(CamValidation.GetError(new CamOperation { Kind = CamOperationKind.Face, UseTabs = false, FaceOverhangMm = -1 }, 18));
        Assert.Null(CamValidation.GetError(new CamOperation { Kind = CamOperationKind.Face, UseTabs = false }, 18));
    }
}
