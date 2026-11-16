using CNCStudio.Core;
using Xunit;

public class DogbonesTests
{
    private static DesignShape Rect() => new() { Kind = ShapeKind.Rectangle, X = 0, Y = 0, Width = 50, Height = 30 };

    private static CamOperation Op(CamOperationKind kind, bool bones = true) => new()
    {
        Kind = kind, ToolDiameter = 6, TotalDepth = 5, DepthPerPass = 5, UseTabs = false, Dogbones = bones, StepoverPercent = 40
    };

    private static ContourPreview Plan(DesignShape s, CamOperation op) =>
        Dogbones.Apply(ContourToolpaths.Generate(s, op, 18), s, op);

    [Fact]
    public void An_inside_rectangle_gets_a_bone_into_each_corner_so_the_cutter_reaches_it()
    {
        var path = Plan(Rect(), Op(CamOperationKind.Inside)).Centerline;
        // 4 corners × (corner, bone, back) + closing point.
        Assert.Equal(4 + 4 * 2 + 1, path.Count);
        // The bone at the corner (3, 3) goes towards (0, 0): r·(√2 − 1) along the diagonal, so the cutter's edge touches it.
        var bone = path.Single(p => p.X < 3 - 1e-6 && p.Y < 3 - 1e-6);
        double toCorner = Math.Sqrt(bone.X * bone.X + bone.Y * bone.Y);
        Assert.Equal(3, toCorner, 3);
    }

    [Fact]
    public void Without_the_option_or_on_an_outside_rectangle_nothing_changes()
    {
        Assert.Equal(5, Plan(Rect(), Op(CamOperationKind.Inside, bones: false)).Centerline.Count);
        Assert.Equal(5, Plan(Rect(), Op(CamOperationKind.Outside)).Centerline.Count);   // corners turn towards the part
    }

    [Fact]
    public void A_pocket_gets_bones_on_the_wall_loop()
    {
        var p = Plan(Rect(), Op(CamOperationKind.Pocket));
        var wall = p.Centerlines[^1];                                   // the wall-following loop is cut last
        Assert.Contains(wall, q => Math.Abs(Math.Sqrt(q.X * q.X + q.Y * q.Y) - 3) < 0.01);
    }

    [Fact]
    public void An_ellipse_is_a_curve_and_gets_no_bones()
    {
        var e = new DesignShape { Kind = ShapeKind.Ellipse, X = 0, Y = 0, Width = 60, Height = 40 };
        var plain = ContourToolpaths.Generate(e, Op(CamOperationKind.Inside, bones: false), 18).Centerline.Count;
        Assert.Equal(plain, Plan(e, Op(CamOperationKind.Inside)).Centerline.Count);
    }
}
