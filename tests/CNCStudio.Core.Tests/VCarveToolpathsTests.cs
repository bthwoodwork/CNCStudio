using System;
using System.Collections.Generic;
using System.Linq;
using CNCStudio.Core;
using CNCStudio.Core.GCode;
using Xunit;

namespace CNCStudio.Core.Tests;

public class VCarveToolpathsTests
{
    private static CamOperation Op(Guid id, double angle = 90, double depth = 5, double pass = 5, double dia = 12.7) => new()
    {
        ShapeId = id, Kind = CamOperationKind.VCarve, ToolKind = ToolKind.VBit, ToolAngleDeg = angle, ToolDiameter = dia,
        TotalDepth = depth, DepthPerPass = pass, UseTabs = false,
    };

    private static DesignShape Rect(double w, double h) => new() { Kind = ShapeKind.Rectangle, X = 10, Y = 10, Width = w, Height = h };

    [Fact]
    public void Narrow_strip_gets_its_depth_from_its_width()
    {
        // 4 mm wide strip, 90° bit: the centre is 2 mm from the edges → about 2 mm deep, never deeper.
        var s = Rect(40, 4);
        var p = VCarveToolpaths.Generate(s, Op(s.Id), 10);
        Assert.True(p.IsVCarve);
        double deepest = p.LoopDepths.Max();
        Assert.InRange(deepest, 1.7, 2.0 + 1e-9);
        Assert.False(p.VCarveHasFloor);
        // Each loop lies at its depth × tan(45°) from the edge, so the cone just touches the outline.
        for (int i = 0; i < p.Centerlines.Count; i++)
            foreach (var pt in p.Centerlines[i])
            {
                double toEdge = Math.Min(Math.Min(pt.X - 10, 50 - pt.X), Math.Min(pt.Y - 10, 14 - pt.Y));
                Assert.True(toEdge >= p.LoopDepths[i] - 0.02, $"loop {i}: {toEdge} < {p.LoopDepths[i]}");
            }
    }

    [Fact]
    public void Wide_shape_is_limited_by_depth_and_gets_a_floor()
    {
        var s = Rect(30, 30);
        var p = VCarveToolpaths.Generate(s, Op(s.Id, angle: 60, depth: 3, pass: 1.5), 10);
        Assert.True(p.VCarveHasFloor);
        Assert.Equal(3, p.VCarveMaxDepth, 6);
        Assert.Equal(3, p.LoopDepths.Max(), 6);
        Assert.Equal(new[] { 1.5, 3.0 }, p.PassDepths);
        // Never deeper than the level allows on the first pass.
        Assert.All(p.LoopDepths.Zip(p.LoopClearedDepths), x => Assert.True(x.First - x.Second <= 1.5 + 1e-9));
        Assert.Contains(p.LoopFinal, f => !f);
    }

    [Fact]
    public void Depth_is_limited_by_the_bits_width()
    {
        // 6 mm wide 90° bit: full width at 3 mm, so 8 mm asked is cut 3 mm deep.
        var op = Op(Guid.NewGuid(), angle: 90, depth: 8, pass: 8, dia: 6);
        Assert.Equal(3, VCarveToolpaths.MaxDepth(op), 6);
        var s = Rect(30, 30); op.ShapeId = s.Id;
        Assert.Equal(3, VCarveToolpaths.Generate(s, op, 10).LoopDepths.Max(), 6);
    }

    [Fact]
    public void Validation_requires_a_v_bit_and_a_sane_angle()
    {
        var op = Op(Guid.NewGuid());
        Assert.Null(CamValidation.GetError(op, 10));
        op.ToolKind = ToolKind.FlatEndMill;
        Assert.Contains("V-fräs", CamValidation.GetError(op, 10));
        op.ToolKind = ToolKind.VBit; op.ToolAngleDeg = 5;
        Assert.Contains("vinkel", CamValidation.GetError(op, 10));
    }

    [Fact]
    public void Plans_and_generates_gcode_with_the_loop_depths()
    {
        var doc = new CncDocument { Name = "V" };
        doc.Motion.StockTopMachineZ = -40;
        var shape = TextShapes.FromOutlines(new IReadOnlyList<(double, double)>[]
        {
            new List<(double, double)> { (0, 0), (20, 0), (20, 20), (0, 20) },
            new List<(double, double)> { (7, 7), (7, 13), (13, 13), (13, 7) },
        }, 20, 20, 20, new TextSource { Content = "o" });
        doc.Shapes.Add(shape);
        doc.CamOperations.Add(Op(shape.Id, angle: 60, depth: 3, pass: 2));
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
        var result = Assert.Single(report.Operations);
        Assert.True(result.Analyzed, string.Join("; ", result.Findings.Select(f => f.Message)));
        Assert.DoesNotContain(result.Findings, f => f.Severity == CamSeverity.Critical);
        var cuts = report.Motion.Moves.Where(m => m.FollowsPath).ToList();
        Assert.NotEmpty(cuts);
        double minZ = cuts.Min(m => m.Z1);
        Assert.InRange(-minZ, 1.0, 3.0 + 1e-9);
        // No rapid sideways in the material.
        Assert.DoesNotContain(report.Motion.Moves, m => m.Kind == MoveKind.Rapid && m.MovesXY && Math.Min(m.Z0, m.Z1) < 0);
        var g = GCodeGenerator.Generate(doc, report);
        Assert.True(g.Success, string.Join("; ", g.BlockingReasons));
        Assert.Contains(g.Lines, l => l.StartsWith("G1") && l.Contains("Z-"));
    }

    [Fact]
    public void Finishes_one_letter_before_the_next_and_steps_inwards_without_lifting()
    {
        // Two "letters" side by side, 10 mm wide strips 20 mm apart; two depth levels.
        var shape = TextShapes.FromOutlines(new IReadOnlyList<(double, double)>[]
        {
            new List<(double, double)> { (0, 0), (8, 0), (8, 30), (0, 30) },
            new List<(double, double)> { (28, 0), (36, 0), (36, 30), (28, 30) },
        }, 30, 0, 0, new TextSource { Content = "ll" });
        var p = VCarveToolpaths.Generate(shape, Op(shape.Id, angle: 90, depth: 4, pass: 2), 10);
        // Which letter each loop belongs to: left (x < 18) or right.
        var side = p.Centerlines.Select(l => l[0].X < 18).ToList();
        int switches = side.Zip(side.Skip(1)).Count(x => x.First != x.Second);
        Assert.Equal(1, switches);
        // Most steps inwards go on at depth; only the first loop of each letter and level starts from above.
        Assert.True(p.LoopLinkAtDepth.Count(x => x) > p.Centerlines.Count / 2, $"{p.LoopLinkAtDepth.Count(x => x)} of {p.Centerlines.Count}");

        var doc = new CncDocument { Name = "V" };
        doc.Motion.StockTopMachineZ = -40;
        doc.Shapes.Add(shape);
        doc.CamOperations.Add(Op(shape.Id, angle: 90, depth: 4, pass: 2));
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
        var moves = report.Motion.Moves;
        int rapidsXY = moves.Count(m => m.Kind == MoveKind.Rapid && m.MovesXY);
        Assert.True(rapidsXY <= 8, $"{rapidsXY} rapids");
        Assert.DoesNotContain(moves, m => m.Kind == MoveKind.Rapid && m.MovesXY && Math.Min(m.Z0, m.Z1) < 0);
        // A link at depth never goes deeper than the loop it comes from allows: it stays horizontal.
        Assert.All(moves.Where(m => m.Purpose == "Till nästa varv på djupet"), m => Assert.Equal(m.Z0, m.Z1));
    }
}
