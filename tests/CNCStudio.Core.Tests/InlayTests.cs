using System;
using System.Collections.Generic;
using System.Linq;
using CNCStudio.Core;
using CNCStudio.Core.GCode;
using Xunit;

namespace CNCStudio.Core.Tests;

public class InlayTests
{
    private static InlayPlan Plan() => new()
    {
        AngleDeg = 60, ToolDiameter = 12.7, PocketDepthMm = 4, PlugStartDepthMm = 3, PlugDepthMm = 5, ClearanceMm = 0,
        PlugBoardThicknessMm = 12, DepthPerPass = 5,
    };

    [Fact]
    public void Plan_explains_the_gaps_and_refuses_impossible_fits()
    {
        var p = Plan();
        Assert.Null(p.Validate(18));
        Assert.Equal(1, p.BottomGapMm, 9);
        Assert.Equal(2, p.ProtrusionMm, 9);
        p.PlugStartDepthMm = 4;
        Assert.Contains("glipa", p.Validate(18));
        p = Plan(); p.PlugDepthMm = 2;
        Assert.Contains("större än startdjupet", p.Validate(18));
        p = Plan(); p.ToolDiameter = 3;   // 60°, Ø3: full width at 2.6 mm
        Assert.Contains("full bredd", p.Validate(18));
    }

    [Fact]
    public void Mirrors_left_to_right()
    {
        // An L: tall bar on the left, foot to the right.
        var l = new DesignShape { Kind = ShapeKind.VectorPath, X = 0, Y = 0, Width = 20, Height = 30,
            Contours = { new VectorContour { Points = { new(0, 0), new(0.3, 0), new(0.3, 0.8), new(1, 0.8), new(1, 1), new(0, 1) } } } };
        var m = Inlay.MirroredUnion(new[] { l }, 5, 7);
        Assert.Equal((5.0, 7.0, 20.0, 30.0), (m.X, m.Y, m.Width, m.Height));
        var region = PocketToolpaths.Region(m);
        // The tall bar is now on the right: a point at the right edge, top, is inside; the left top is not.
        Assert.True(CamGeometryChecks.Inside(new CamPoint(5 + 18, 7 + 2), region.Select(p => (IReadOnlyList<CamPoint>)p.Select(v => new CamPoint(v.x, v.y)).ToList()).ToList()));
        Assert.False(CamGeometryChecks.Inside(new CamPoint(5 + 2, 7 + 2), region.Select(p => (IReadOnlyList<CamPoint>)p.Select(v => new CamPoint(v.x, v.y)).ToList()).ToList()));
    }

    [Fact]
    public void Plug_is_carved_around_the_shape_from_the_start_depth()
    {
        var p = Plan();
        var square = new DesignShape { Kind = ShapeKind.Rectangle, X = 30, Y = 30, Width = 20, Height = 20 };
        var op = Inlay.PlugOperation(square.Id, p);
        var preview = VCarveToolpaths.Generate(square, op, 12);
        double tan = p.Tan;
        static double Outside(CamPoint q)
        {
            double dx = Math.Max(Math.Max(30 - q.X, 0), q.X - 50), dy = Math.Max(Math.Max(30 - q.Y, 0), q.Y - 50);
            return Math.Sqrt(dx * dx + dy * dy);
        }
        for (int i = 0; i < preview.Centerlines.Count; i++)
        {
            if (!preview.LoopFinal[i]) continue;
            foreach (var q in preview.Centerlines[i])
            {
                double d = Outside(q);
                // Never inside the plug; and where the loop is nearest the plug its depth is S + distance / tan.
                Assert.True(q.X <= 30 + 1e-6 || q.X >= 50 - 1e-6 || q.Y <= 30 + 1e-6 || q.Y >= 50 - 1e-6, $"{q} inside the plug");
                Assert.True(preview.LoopDepths[i] <= p.PlugStartDepthMm + d / tan + 0.02, $"loop {i} at {q}: {preview.LoopDepths[i]} > {p.PlugStartDepthMm + d / tan}");
            }
        }
        Assert.Equal(p.PlugStartDepthMm, preview.LoopDepths.Min(), 6);
        Assert.Equal(p.PlugDepthMm, preview.LoopDepths.Max(), 6);
        Assert.True(preview.VCarveHasFloor);
    }

    [Fact]
    public void Plug_project_is_its_own_board_with_one_mirrored_shape_and_runs()
    {
        var baseDoc = new CncDocument { Name = "Skylt" };
        baseDoc.Motion.StockTopMachineZ = -40;
        var a = new DesignShape { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = 20, Height = 10 };
        var b = new DesignShape { Kind = ShapeKind.Ellipse, X = 130, Y = 100, Width = 10, Height = 10 };
        baseDoc.Shapes.AddRange(new[] { a, b });
        var plan = Plan();
        var doc = Inlay.PlugDocument(baseDoc, new[] { a, b }, plan);
        Assert.Equal("Skylt – hane", doc.Name);
        var plug = Assert.Single(doc.Shapes);
        var op = Assert.Single(doc.CamOperations);
        Assert.Equal(CamOperationKind.VCarvePlug, op.Kind);
        Assert.Equal(12, doc.MaterialThickness);
        Assert.Null(doc.Motion.StockTopMachineZ);
        Assert.Equal(40, plug.Width, 6);
        Assert.True(doc.MaterialWidth >= plug.Width + 2 * 10);
        Assert.Equal(-40, baseDoc.Motion.StockTopMachineZ);        // the base project is left alone

        doc.Motion.StockTopMachineZ = -40;
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
        var r = Assert.Single(report.Operations);
        Assert.True(r.Analyzed, string.Join("; ", r.Findings.Select(f => f.Message)));
        Assert.DoesNotContain(r.Findings, f => f.Severity == CamSeverity.Critical);
        var g = GCodeGenerator.Generate(doc, report);
        Assert.True(g.Success, string.Join("; ", g.BlockingReasons));
    }

    [Fact]
    public void Pocket_operation_is_a_v_carve_to_the_pocket_depth()
    {
        var s = new DesignShape { Kind = ShapeKind.Rectangle, Width = 10, Height = 10 };
        var op = Inlay.PocketOperation(s, Plan());
        Assert.Equal(CamOperationKind.VCarve, op.Kind);
        Assert.Equal(4, op.TotalDepth);
        Assert.Null(CamValidation.GetError(op, 18));
    }
}
