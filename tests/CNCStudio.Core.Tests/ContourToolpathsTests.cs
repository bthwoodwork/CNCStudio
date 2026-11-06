using CNCStudio.Core;

namespace CNCStudio.Core.Tests;

public class ContourToolpathsTests
{
    private static DesignShape Rect(double x = 100, double y = 100, double w = 200, double h = 150) =>
        new() { Kind = ShapeKind.Rectangle, X = x, Y = y, Width = w, Height = h };

    [Fact]
    public void Outside_rectangle_is_offset_outward_by_tool_radius()
    {
        var p = ContourToolpaths.Generate(Rect(), new CamOperation { Kind = CamOperationKind.Outside, ToolDiameter = 4 }, 18);
        Assert.Equal(new CamPoint(98, 98), p.Centerline[0]);
        Assert.Equal(new CamPoint(302, 252), p.Centerline[2]);
        Assert.Equal(p.Centerline[0], p.Centerline[^1]); // closed
    }

    [Fact]
    public void Inside_rectangle_is_offset_inward_by_tool_radius()
    {
        var p = ContourToolpaths.Generate(Rect(), new CamOperation { Kind = CamOperationKind.Inside, ToolDiameter = 4 }, 18);
        Assert.Equal(new CamPoint(102, 102), p.Centerline[0]);
        Assert.Equal(new CamPoint(298, 248), p.Centerline[2]);
    }

    [Fact]
    public void Inside_contour_with_too_large_tool_throws()
    {
        Assert.Throws<ArgumentException>(() => ContourToolpaths.Generate(Rect(w: 3, h: 3),
            new CamOperation { Kind = CamOperationKind.Inside, ToolDiameter = 6, TotalDepth = 2, DepthPerPass = 1 }, 18));
    }

    [Theory]
    [InlineData(6, 2, new[] { 2.0, 4.0, 6.0 })]
    [InlineData(5, 2, new[] { 2.0, 4.0, 5.0 })]
    [InlineData(3, 3, new[] { 3.0 })]
    public void Pass_depths_end_exactly_at_total_depth(double total, double perPass, double[] expected)
    {
        var p = ContourToolpaths.Generate(Rect(), new CamOperation { TotalDepth = total, DepthPerPass = perPass }, 18);
        Assert.Equal(expected, p.PassDepths);
    }

    [Fact]
    public void Ellipse_outside_points_are_at_least_tool_radius_from_ellipse()
    {
        var shape = new DesignShape { Kind = ShapeKind.Ellipse, X = 0, Y = 0, Width = 100, Height = 60 };
        var p = ContourToolpaths.Generate(shape, new CamOperation { ToolDiameter = 6 }, 18);
        // Every centerline point lies outside the ellipse.
        Assert.All(p.Centerline, pt =>
        {
            double nx = (pt.X - 50) / 50, ny = (pt.Y - 30) / 30;
            Assert.True(nx * nx + ny * ny > 1);
        });
    }

    [Fact]
    public void Pocket_operations_are_delegated_to_the_pocket_generator()
    {
        var p = ContourToolpaths.Generate(Rect(), new CamOperation { Kind = CamOperationKind.Pocket, UseTabs = false }, 18);
        Assert.True(p.IsPocket);
    }
}
