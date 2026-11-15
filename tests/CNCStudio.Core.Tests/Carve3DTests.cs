using System.Numerics;
using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

public class Carve3DTests
{
    /// <summary>A square pyramid 100 × 100 at the base, 20 high, with a bottom.</summary>
    private static byte[] Pyramid()
    {
        var a = new Vector3(0, 0, 0); var b = new Vector3(100, 0, 0); var c = new Vector3(100, 100, 0); var d = new Vector3(0, 100, 0); var top = new Vector3(50, 50, 20);
        return Stl.Write(new[] { (a, b, top), (b, c, top), (c, d, top), (d, a, top), (a, c, b), (a, d, c) });
    }

    private static DesignShape Shape(double size = 60, double depth = 6) => new()
    {
        Kind = ShapeKind.Rectangle, X = 20, Y = 20, Width = size, Height = size, Model = Model3D.FromStl(Pyramid(), "pyramid", depth),
    };

    [Fact]
    public void Stl_reads_binary_and_ascii()
    {
        Assert.Equal(6, Stl.Parse(Pyramid()).Count);
        var ascii = "solid t\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid t\n";
        Assert.Single(Stl.Parse(System.Text.Encoding.ASCII.GetBytes(ascii)));
        Assert.Throws<InvalidDataException>(() => Stl.Parse(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Heightmap_has_the_top_at_the_surface_and_the_base_at_the_depth()
    {
        var s = Shape();
        var map = Heightmap.Build(s);
        Assert.Equal(0, map.At(50, 50), 1);                 // peak in the middle
        Assert.Equal(-6, map.At(20.2, 20.2), 1);             // corner = base
        Assert.Equal(-3, map.At(35, 50), 1);                 // halfway up the side
        Assert.Equal(0, map.At(5, 5));                       // outside the shape: untouched
    }

    [Theory]
    [InlineData(ToolKind.BallNose)]
    [InlineData(ToolKind.FlatEndMill)]
    public void Finishing_never_cuts_below_the_surface(ToolKind kind)
    {
        var s = Shape();
        var op = new CamOperation { ShapeId = s.Id, Kind = CamOperationKind.Carve3D, ToolKind = kind, ToolDiameter = 3.175, TotalDepth = 6, DepthPerPass = 10, StepoverPercent = 20, UseTabs = false };
        var p = Carve3DToolpaths.Generate(s, op, 18);
        Assert.True(p.Is3D);
        var map = Heightmap.Build(s);
        for (int k = 0; k < p.Centerlines.Count; k++)
            for (int i = 0; i < p.Centerlines[k].Count; i++)
            {
                var q = p.Centerlines[k][i];
                Assert.True(p.PathZ[k][i] >= map.At(q.X, q.Y) - 0.02, $"{q} z {p.PathZ[k][i]} below {map.At(q.X, q.Y)}");
                Assert.True(p.PathZ[k][i] <= 1e-9);
            }
        // The base is at the shape's edge, where the tool may not reach down (it would cut outside the shape).
        Assert.InRange(p.PathZ.SelectMany(z => z).Min(), -6.01, -5);
    }

    [Fact]
    public void Roughing_steps_down_and_leaves_an_allowance()
    {
        var s = Shape();
        var op = new CamOperation { ShapeId = s.Id, Kind = CamOperationKind.Carve3D, ToolKind = ToolKind.FlatEndMill, ToolDiameter = 6, TotalDepth = 6, DepthPerPass = 2,
            StepoverPercent = 40, UseTabs = false, SecondToolEnabled = true, SecondToolKind = ToolKind.BallNose, SecondToolDiameter = 3.175, SecondDepthPerPass = 6 };
        var rough = Carve3DToolpaths.Generate(s, op, 18);
        Assert.Equal(new[] { 2.0, 4.0, 6.0 }, rough.PassDepths);
        var map = Heightmap.Build(s);
        for (int k = 0; k < rough.Centerlines.Count; k++)
            for (int i = 0; i < rough.Centerlines[k].Count; i++)
            {
                var q = rough.Centerlines[k][i];
                Assert.True(rough.PathZ[k][i] >= map.At(q.X, q.Y) + Carve3DToolpaths.RoughAllowanceMm - 0.02);
            }
        var finish = CamSecondPass.For(op)!;
        Assert.Equal(CamOperationKind.Carve3DFinish, finish.Kind);
        Assert.Equal(ToolKind.BallNose, finish.ToolKind);
        Assert.Single(Carve3DToolpaths.Generate(s, finish, 18).Centerlines);   // one zig-zag over everything
    }

    [Fact]
    public void Whole_chain_gives_two_programs_of_3d_gcode()
    {
        var s = Shape();
        var doc = new CncDocument { Name = "Pyramid" };
        doc.Motion.StockTopMachineZ = -40;
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, Kind = CamOperationKind.Carve3D, ToolName = "6 mm", ToolKind = ToolKind.FlatEndMill, ToolDiameter = 6,
            TotalDepth = 6, DepthPerPass = 3, StepoverPercent = 40, UseTabs = false,
            SecondToolEnabled = true, SecondToolName = "3 mm kula", SecondToolKind = ToolKind.BallNose, SecondToolDiameter = 3.175, SecondDepthPerPass = 6 });
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
        Assert.Equal(2, report.Operations.Count);
        Assert.All(report.Operations, r => Assert.True(r.Analyzed, string.Join("; ", r.Findings.Select(f => f.Message))));
        Assert.DoesNotContain(report.Operations.SelectMany(r => r.Findings), f => f.Severity == CamSeverity.Critical);
        var (jobs, blocking) = GCodeGenerator.GenerateByTool(doc, report);
        Assert.Empty(blocking);
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, j => Assert.True(j.Result.Success, string.Join("; ", j.Result.BlockingReasons.Concat(j.Result.VerifierIssues))));
        Assert.Contains(jobs[1].Result.Lines, l => l.StartsWith("G1") && l.Contains('X') && l.Contains('Z'));   // X, Y and Z together
        Assert.InRange(report.Motion.Moves.Min(m => Math.Min(m.Z0, m.Z1)), -6.01, -5);
    }

    [Fact]
    public void A_turned_model_turns_its_heightmap()
    {
        // A wedge: high along x = 0, low at x = 100 (seen from above). Turned a quarter counter-clockwise, the high side is at the front.
        var a = new Vector3(0, 0, 10); var b = new Vector3(100, 0, 0); var c = new Vector3(100, 100, 0); var d = new Vector3(0, 100, 10);
        var stl = Stl.Write(new[] { (a, b, c), (a, c, d) });
        var flat = new DesignShape { Kind = ShapeKind.Rectangle, Width = 100, Height = 100, Model = Model3D.FromStl(stl, "wedge", 10) };
        Assert.True(Heightmap.Build(flat).At(5, 50) > Heightmap.Build(flat).At(95, 50));
        var turned = new DesignShape { Kind = ShapeKind.Rectangle, Width = 100, Height = 100, Model = flat.Model!.Turned(1) };
        var map = Heightmap.Build(turned);
        Assert.True(map.At(50, 95) > map.At(50, 5));            // the left side turned to the front (large design Y)
    }
}
