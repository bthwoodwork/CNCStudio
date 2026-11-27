using System.Numerics;

namespace CNCStudio.Core.Tests;

public class TaperedBallNoseTests
{
    // R0.8 tip, 11° included (5.5° per side) – a common detail cutter for reliefs.
    private const double R = 0.8, Angle = 11;

    [Fact]
    public void Shape_is_a_ball_at_the_tip_and_a_tangent_cone_above()
    {
        double half = Angle * Math.PI / 360;
        double d0 = R * Math.Cos(half), h0 = R * (1 - Math.Sin(half));
        Assert.Equal(0, ToolShape.Height(ToolKind.TaperedBallNose, R, Angle, 0), 9);
        // On the ball: same as a ball nose of the tip radius.
        Assert.Equal(ToolShape.Height(ToolKind.BallNose, R, 0, 0.5), ToolShape.Height(ToolKind.TaperedBallNose, R, Angle, 0.5), 9);
        // Where they meet, and beyond: the cone rises 1 / tan(5.5°) per mm outwards.
        Assert.Equal(h0, ToolShape.Height(ToolKind.TaperedBallNose, R, Angle, d0), 9);
        Assert.Equal(h0 + 1 / Math.Tan(half), ToolShape.Height(ToolKind.TaperedBallNose, R, Angle, d0 + 1), 9);
        // Unlike a ball nose, it reaches beyond the tip radius.
        Assert.True(double.IsPositiveInfinity(ToolShape.Height(ToolKind.BallNose, R, 0, 1.2)));
        Assert.True(double.IsFinite(ToolShape.Height(ToolKind.TaperedBallNose, R, Angle, 1.2)));
        // RadiusAt is the inverse of Height.
        foreach (double h in new[] { 0.1, 0.5, h0, 2, 8 })
            Assert.Equal(h, ToolShape.Height(ToolKind.TaperedBallNose, R, Angle, ToolShape.RadiusAt(ToolKind.TaperedBallNose, R, Angle, h)), 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(double.NaN)]
    public void Angle_must_be_between_0_and_90(double angle)
    {
        Assert.NotNull(ToolShape.ValidateTaper(angle));
        var tool = new ToolDefinition { Name = "Konisk", Kind = ToolKind.TaperedBallNose, Diameter = 1.6, AngleDeg = angle };
        Assert.NotNull(tool.Validate());
        Assert.Null(new ToolDefinition { Name = "Konisk", Kind = ToolKind.TaperedBallNose, Diameter = 1.6, AngleDeg = Angle }.Validate());
    }

    /// <summary>A block 100 × 100 with a deep square well (8 mm) in the middle: vertical walls.</summary>
    private static DesignShape Well()
    {
        var t = new List<(Vector3, Vector3, Vector3)>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { t.Add((a, b, c)); t.Add((a, c, d)); }
        float top = 8, low = 0;
        // Top surface around a hole from 40 to 60, and the hole's floor.
        Quad(new(0, 0, top), new(100, 0, top), new(100, 40, top), new(0, 40, top));
        Quad(new(0, 60, top), new(100, 60, top), new(100, 100, top), new(0, 100, top));
        Quad(new(0, 40, top), new(40, 40, top), new(40, 60, top), new(0, 60, top));
        Quad(new(60, 40, top), new(100, 40, top), new(100, 60, top), new(60, 60, top));
        Quad(new(40, 40, low), new(60, 40, low), new(60, 60, low), new(40, 60, low));
        return new DesignShape { Kind = ShapeKind.Rectangle, X = 0, Y = 0, Width = 100, Height = 100, Model = Model3D.FromStl(Stl.Write(t), "well", 8) };
    }

    private static CamOperation Finish(ToolKind kind) => new()
    {
        Kind = CamOperationKind.Carve3D, ToolKind = kind, ToolDiameter = 2 * R, ToolAngleDeg = Angle,
        TotalDepth = 8, DepthPerPass = 10, StepoverPercent = 50, UseTabs = false,
    };

    [Fact]
    public void Finishing_never_cuts_below_the_surface_and_the_cone_keeps_off_steep_walls()
    {
        var s = Well();
        var map = Heightmap.Build(s);
        var tapered = Carve3DToolpaths.Generate(s, Finish(ToolKind.TaperedBallNose), 18);
        var ball = Carve3DToolpaths.Generate(s, Finish(ToolKind.BallNose), 18);
        double half = Angle * Math.PI / 360;
        double maxGap = 0;
        for (int k = 0; k < tapered.Centerlines.Count; k++)
            for (int i = 0; i < tapered.Centerlines[k].Count; i++)
            {
                var q = tapered.Centerlines[k][i];
                double z = tapered.PathZ[k][i];
                Assert.True(z >= map.At(q.X, q.Y) - 0.02, $"{q} z {z} below the surface {map.At(q.X, q.Y)}");
                Assert.True(z <= 1e-9);
                // Inside the well, near its wall at x = 40: the cone must clear the wall's top edge (z = 0 at x ≤ 40).
                if (q.Y > 45 && q.Y < 55 && q.X > 40.5 && q.X < 43)
                {
                    double clearance = ToolShape.RadiusAt(ToolKind.TaperedBallNose, R, Angle, -z);
                    Assert.True(q.X - 40 >= clearance - map.Cell - 0.02, $"at x {q.X:0.###} the cone (radius {clearance:0.###} at the top) hits the wall");
                    maxGap = Math.Max(maxGap, z);
                }
            }
        // Near the wall the tapered tool stays higher than a plain ball nose would go (the cone touches the edge first).
        double ballNear = BallZ(ball, 41.2), taperNear = BallZ(tapered, 41.2);
        Assert.True(taperNear > ballNear + 0.5, $"tapered {taperNear:0.###} vs ball {ballNear:0.###}");
        Assert.True(Math.Tan(half) > 0 && maxGap <= 0);
    }

    /// <summary>The path's Z at x on the row nearest y = 50, interpolated along the (simplified) path.</summary>
    private static double BallZ(ContourPreview p, double x)
    {
        double best = double.NaN, dist = double.MaxValue;
        for (int k = 0; k < p.Centerlines.Count; k++)
            for (int i = 1; i < p.Centerlines[k].Count; i++)
            {
                var a = p.Centerlines[k][i - 1]; var b = p.Centerlines[k][i];
                if (Math.Abs(a.Y - b.Y) > 1e-9 || Math.Abs(a.X - b.X) < 1e-9) continue;          // along a row only
                if (x < Math.Min(a.X, b.X) - 1e-9 || x > Math.Max(a.X, b.X) + 1e-9) continue;
                double d = Math.Abs(a.Y - 50);
                if (d >= dist) continue;
                dist = d;
                double t = (x - a.X) / (b.X - a.X);
                best = p.PathZ[k][i - 1] + t * (p.PathZ[k][i] - p.PathZ[k][i - 1]);
            }
        return best;
    }

    [Fact]
    public void Second_tool_carries_its_angle_into_the_finishing_pass()
    {
        var s = Well();
        var op = new CamOperation
        {
            ShapeId = s.Id, Kind = CamOperationKind.Carve3D, ToolKind = ToolKind.FlatEndMill, ToolDiameter = 6, TotalDepth = 8, DepthPerPass = 3,
            StepoverPercent = 40, UseTabs = false, SecondToolEnabled = true, SecondToolKind = ToolKind.TaperedBallNose,
            SecondToolDiameter = 2 * R, SecondToolAngleDeg = Angle, SecondDepthPerPass = 8,
        };
        Assert.Null(CamValidation.GetError(op, 18));
        var finish = CamSecondPass.For(op)!;
        Assert.Equal(ToolKind.TaperedBallNose, finish.ToolKind);
        Assert.Equal(Angle, finish.ToolAngleDeg);
        Assert.Null(CamValidation.GetError(finish, 18));
        // A different angle is a different toolpath.
        var other = op.CopyFor(op.ShapeId); other.SecondToolAngleDeg = 20;
        Assert.NotEqual(CamPlanner.Fingerprint(s, finish, 18), CamPlanner.Fingerprint(s, CamSecondPass.For(other)!, 18));
        // The library tool sets the angle on the operation.
        var lib = new ToolDefinition { Name = "R0.8 11°", Kind = ToolKind.TaperedBallNose, Diameter = 1.6, AngleDeg = Angle };
        var applied = new CamOperation { Kind = CamOperationKind.Carve3D };
        lib.ApplyTo(applied);
        Assert.Equal(Angle, applied.ToolAngleDeg);
        Assert.Equal("Koniska kulfräsar", lib.CategoryOrDefault);
    }

    [Fact]
    public void Tapered_ball_nose_is_only_for_3d_carving()
    {
        var op = new CamOperation { Kind = CamOperationKind.Pocket, ToolKind = ToolKind.TaperedBallNose, ToolDiameter = 1.6, ToolAngleDeg = Angle, TotalDepth = 3, DepthPerPass = 1, UseTabs = false };
        Assert.NotNull(CamValidation.GetError(op, 18));
    }
}
