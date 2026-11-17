using System.Linq;
using CNCStudio.Core;
using Xunit;

public class ShapeRotationTests
{
    private static DesignShape L() => new()
    {
        Kind = ShapeKind.VectorPath, X = 10, Y = 20, Width = 40, Height = 20,
        Contours = { new VectorContour { Points = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) } } }
    };

    [Fact]
    public void A_single_shape_turns_around_its_middle_with_its_tabs()
    {
        var doc = new CncDocument();
        var s = L();
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, ManualTabs = new() { new TabPoint(40, 5) } });   // right edge, near the top
        ShapeRotation.Rotate90(doc, new[] { s }, clockwise: true);
        Assert.Equal((20.0, 10.0, 20.0, 40.0), (s.X, s.Y, s.Width, s.Height));          // middle stays at (30, 30)
        var tab = doc.CamOperations[0].ManualTabs![0];
        Assert.Equal((15.0, 40.0), (tab.X, tab.Y));                                       // the right edge is now the bottom
        Assert.Equal((1.0, 0.0), (s.Contours[0].Points[0].X, s.Contours[0].Points[0].Y)); // the top-left corner went to top-right
        ShapeRotation.Rotate90(doc, new[] { s }, clockwise: false);
        Assert.Equal((10.0, 20.0, 40.0, 20.0), (s.X, s.Y, s.Width, s.Height));
        Assert.Equal((40.0, 5.0), (tab.X, tab.Y));
        Assert.Equal((0.0, 0.0), (s.Contours[0].Points[0].X, s.Contours[0].Points[0].Y));
    }

    [Fact]
    public void Several_shapes_turn_as_one_piece()
    {
        var doc = new CncDocument();
        var a = new DesignShape { Kind = ShapeKind.Rectangle, X = 0, Y = 0, Width = 10, Height = 10 };
        var b = new DesignShape { Kind = ShapeKind.Rectangle, X = 30, Y = 0, Width = 10, Height = 10 };
        ShapeRotation.Rotate90(doc, new[] { a, b }, clockwise: true);
        // Box 40 × 10 around (20, 5) becomes 10 × 40: a at the top, b below it – moved down 15 so it stays on the material.
        Assert.Equal((15.0, 0.0), (a.X, a.Y));
        Assert.Equal((15.0, 30.0), (b.X, b.Y));
    }

    [Fact]
    public void A_3d_model_is_refused() =>
        Assert.NotNull(ShapeRotation.Problem(new[] { new DesignShape { Model = new Model3D() } }));
}
