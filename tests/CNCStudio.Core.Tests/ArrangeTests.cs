using Clipper2Lib;

namespace CNCStudio.Core.Tests;

public class ArrangeTests
{
    private static DesignShape R(double x, double y, double w, double h, Guid? g = null) => new() { Kind = ShapeKind.Rectangle, X = x, Y = y, Width = w, Height = h, GroupId = g };

    [Fact]
    public void Align_left_and_centre_of_the_selection()
    {
        var a = R(10, 0, 20, 10); var b = R(50, 30, 40, 10);
        Arrange.Align(new[] { a, b }, AlignMode.Left, null);
        Assert.Equal(10, b.X);
        Arrange.Align(new[] { a, b }, AlignMode.CenterX, null);
        Assert.Equal(a.X + a.Width / 2, b.X + b.Width / 2, 9);
        Arrange.Align(new[] { a, b }, AlignMode.Bottom, null);
        Assert.Equal(40, a.Y + a.Height, 9);
    }

    [Fact]
    public void A_single_piece_aligns_to_the_material_and_groups_move_together()
    {
        var g = Guid.NewGuid();
        var a = R(0, 0, 10, 10, g); var b = R(20, 5, 10, 10, g);
        Arrange.Align(new[] { a, b }, AlignMode.CenterX, new Arrange.Bounds(0, 0, 100, 50));
        Arrange.Align(new[] { a, b }, AlignMode.CenterY, new Arrange.Bounds(0, 0, 100, 50));
        Assert.Equal((35.0, 17.5), (a.X, a.Y));
        Assert.Equal((55.0, 22.5), (b.X, b.Y));          // kept its place in the group
    }

    [Fact]
    public void Distribute_gives_equal_gaps()
    {
        var a = R(0, 0, 10, 10); var b = R(15, 0, 20, 10); var c = R(90, 0, 10, 10);
        Arrange.Distribute(new[] { c, a, b }, horizontal: true);
        Assert.Equal(0, a.X); Assert.Equal(90, c.X);
        Assert.Equal(b.X - (a.X + a.Width), c.X - (b.X + b.Width), 9);
        Assert.Throws<ArgumentException>(() => Arrange.Distribute(new[] { a, b }, true));
    }

    [Fact]
    public void Offset_grows_and_shrinks_with_holes_following()
    {
        var ring = new DesignShape { Kind = ShapeKind.VectorPath, X = 0, Y = 0, Width = 40, Height = 40,
            Contours = { new VectorContour { Points = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) } },
                         new VectorContour { Points = { new(0.25, 0.25), new(0.25, 0.75), new(0.75, 0.75), new(0.75, 0.25) } } } };
        var grown = Arrange.Offset(ring, 2, roundCorners: false);
        Assert.Equal((-2.0, -2.0, 44.0, 44.0), (grown.X, grown.Y, grown.Width, grown.Height));
        double area = Math.Abs(Clipper.Area(PocketToolpaths.Region(grown)));
        Assert.Equal(44 * 44 - 16 * 16, area, 1);        // hole 20 → 16
        var round = Arrange.Offset(R(0, 0, 20, 10), 3, roundCorners: true);
        Assert.Equal(26, round.Width, 2);
        Assert.Throws<ArgumentException>(() => Arrange.Offset(R(0, 0, 4, 4), -3, false));
    }

    [Fact]
    public void Grid_copies_edge_to_edge_with_the_gap()
    {
        var a = R(10, 10, 20, 10);
        var copies = Arrange.Grid(new[] { a }, 3, 2, 5, 2);
        Assert.Equal(5, copies.Count);
        Assert.Contains(copies, c => c.Copy.X == 60 && c.Copy.Y == 22);
        Assert.All(copies, c => Assert.Same(a, c.Source));
        Assert.Throws<ArgumentException>(() => Arrange.Grid(new[] { a }, 1, 1, 0, 0));
    }
}
