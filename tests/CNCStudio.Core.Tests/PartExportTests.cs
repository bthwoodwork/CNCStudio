using System;
using System.Collections.Generic;
using System.Linq;
using CNCStudio.Core;
using Xunit;

public class PartExportTests
{
    private static double Volume(List<(double[] A, double[] B, double[] C)> tris) =>
        tris.Sum(t => (t.A[0] * (t.B[1] * t.C[2] - t.B[2] * t.C[1]) - t.A[1] * (t.B[0] * t.C[2] - t.B[2] * t.C[0]) + t.A[2] * (t.B[0] * t.C[1] - t.B[1] * t.C[0])) / 6);

    /// <summary>Every edge is used once in each direction: the solid is closed.</summary>
    private static bool Closed(List<(double[] A, double[] B, double[] C)> tris)
    {
        string K(double[] p) => string.Join(",", p.Select(v => Math.Round(v, 4)));
        var edges = new Dictionary<(string, string), int>();
        foreach (var (a, b, c) in tris)
            foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
            { var k = (K(p), K(q)); edges[k] = edges.GetValueOrDefault(k) + 1; }
        return edges.All(e => e.Value == 1 && edges.GetValueOrDefault((e.Key.Item2, e.Key.Item1)) == 1);
    }

    private static (CncDocument Doc, DesignShape L, DesignShape Hole) Doc()
    {
        var doc = new CncDocument { MaterialWidth = 200, MaterialHeight = 100, MaterialThickness = 12, HasMaterial = true };
        // An L: 60 × 40 with a 30 × 20 notch – one inner corner for a dogbone.
        var l = new DesignShape { Kind = ShapeKind.VectorPath, X = 20, Y = 20, Width = 60, Height = 40,
            Contours = { new VectorContour { Points = { new(0, 0), new(1, 0), new(1, 0.5), new(0.5, 0.5), new(0.5, 1), new(0, 1) } } } };
        var hole = new DesignShape { Kind = ShapeKind.Rectangle, X = 25, Y = 25, Width = 10, Height = 10 };
        doc.Shapes.Add(l); doc.Shapes.Add(hole);
        doc.CamOperations.Add(new CamOperation { ShapeId = l.Id, Kind = CamOperationKind.Outside, ToolDiameter = 6, TotalDepth = 12, DepthPerPass = 4, UseTabs = false, Dogbones = true });
        doc.CamOperations.Add(new CamOperation { ShapeId = hole.Id, Kind = CamOperationKind.Inside, ToolDiameter = 6, TotalDepth = 12, DepthPerPass = 4, UseTabs = false });
        return (doc, l, hole);
    }

    [Fact]
    public void A_part_is_its_drawn_outline_extruded_with_the_through_holes_cut_out()
    {
        var (doc, l, hole) = Doc();
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
        var part = Assert.Single(PartExport.Parts(doc, report, new[] { l, hole }, new PartExport.Options(12)));
        var tris = PartExport.Solid(part.Region, 12);
        Assert.True(Closed(tris));
        Assert.Equal((60 * 40 - 30 * 20 - 10 * 10) * 12, Volume(tris), 1);   // positive: the faces point outwards
        Assert.Equal(84 + 50 * tris.Count, PartExport.Stl(tris, "test").Length);   // 80 + 4 header, 50 bytes per triangle
    }

    [Fact]
    public void As_milled_a_hole_gets_rounded_corners_or_its_dogbones()
    {
        double Area(bool dogbones)
        {
            var doc = new CncDocument { MaterialWidth = 200, MaterialHeight = 100, MaterialThickness = 12, HasMaterial = true };
            var part = new DesignShape { Kind = ShapeKind.Rectangle, X = 20, Y = 20, Width = 60, Height = 40 };
            var hole = new DesignShape { Kind = ShapeKind.Rectangle, X = 30, Y = 30, Width = 20, Height = 20 };
            doc.Shapes.Add(part); doc.Shapes.Add(hole);
            doc.CamOperations.Add(new CamOperation { ShapeId = part.Id, Kind = CamOperationKind.Outside, ToolDiameter = 6, TotalDepth = 12, DepthPerPass = 4, UseTabs = false });
            doc.CamOperations.Add(new CamOperation { ShapeId = hole.Id, Kind = CamOperationKind.Inside, ToolDiameter = 6, TotalDepth = 12, DepthPerPass = 4, UseTabs = false, Dogbones = dogbones });
            var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
            Assert.NotNull(report.For(hole.Id)?.Preview);
            var milled = Assert.Single(PartExport.Parts(doc, report, new[] { part, hole }, new PartExport.Options(12, AsMilled: true)));
            var solid = PartExport.Solid(milled.Region, 12);
            int tops = solid.Count(t => t.A[2] == 12 && t.B[2] == 12 && t.C[2] == 12);
            int pts = milled.Region.Sum(r => r.Count);
            Assert.True(Closed(solid), $"loops {milled.Region.Count}, points {pts}, top triangles {tops} (expected {pts + 2 * (milled.Region.Count - 1) - 2}) {string.Join(" ; ", milled.Region.Select(r => string.Join(" ", r.Select(q => $"{q.x:0.##},{q.y:0.##}"))))}");
            return Clipper2Lib.Clipper.Area(milled.Region);
        }
        double drawn = 60 * 40 - 20 * 20;
        double rounded = Area(false), boned = Area(true);
        Assert.InRange(rounded, drawn + 1, drawn + 4 * 9 * (1 - Math.PI / 4) + 0.5);   // 4 corners of r 3 left in the hole
        Assert.InRange(boned, drawn - 4 * 30, drawn - 1);                              // the bones go past the corners
    }

    [Fact]
    public void The_panels_of_a_box_become_closed_solids_with_the_finger_joints()
    {
        var doc = new CncDocument { MaterialWidth = 600, MaterialHeight = 600, MaterialThickness = 12, HasMaterial = true };
        var o = new BoxOptions { ThicknessMm = 12, ToolDiameterMm = 6 };
        var (placed, _) = BoxJoints.Layout(BoxJoints.Panels(o), 600, 600, 10, 6);
        foreach (var (panel, x, y) in placed)
        {
            var s = BoxJoints.ToShape(panel, x, y);
            doc.Shapes.Add(s);
            doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, Kind = CamOperationKind.Outside, ToolDiameter = 6, TotalDepth = 12, DepthPerPass = 4, Dogbones = true });
        }
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
        var parts = PartExport.Parts(doc, report, doc.Shapes, new PartExport.Options(12));
        Assert.Equal(placed.Count, parts.Count);
        foreach (var p in parts)
        {
            var solid = PartExport.Solid(p.Region, 12);
            Assert.True(Closed(solid), p.Name);
            Assert.Equal(Clipper2Lib.Clipper.Area(p.Region) * 12, Volume(solid), 1);
        }
    }
}
