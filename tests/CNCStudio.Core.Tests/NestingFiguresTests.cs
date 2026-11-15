using Clipper2Lib;

namespace CNCStudio.Core.Tests;

public class NestingFiguresTests
{
    private static DesignShape R(double w, double h) => new() { Kind = ShapeKind.Rectangle, X = 500, Y = 500, Width = w, Height = h };

    private static void NoOverlap(IReadOnlyList<DesignShape> s, double spacing)
    {
        for (int i = 0; i < s.Count; i++)
            for (int j = i + 1; j < s.Count; j++)
            {
                var a = Clipper.InflatePaths(PocketToolpaths.Region(s[i]), spacing / 2 - 0.05, JoinType.Round, EndType.Polygon);
                var b = Clipper.InflatePaths(PocketToolpaths.Region(s[j]), spacing / 2 - 0.05, JoinType.Round, EndType.Polygon);
                Assert.True(Math.Abs(Clipper.Area(Clipper.Intersect(a, b, FillRule.NonZero))) < 0.01, $"{i} and {j} overlap");
            }
    }

    [Fact]
    public void Packs_parts_inside_the_material_without_overlap()
    {
        var parts = Enumerable.Range(0, 8).Select(i => R(60 + i * 5, 40)).ToList();
        var r = Nesting.Nest(parts, 300, 200, new NestOptions { SpacingMm = 6, MarginMm = 5 });
        Assert.Equal(8, r.Placed);
        Assert.Empty(r.NotPlaced);
        Assert.All(parts, p => { Assert.True(p.X >= 5 - 1e-6 && p.Y >= 5 - 1e-6); Assert.True(p.X + p.Width <= 295 + 1e-6 && p.Y + p.Height <= 195 + 1e-6); });
        NoOverlap(parts, 6);
    }

    [Fact]
    public void Turns_a_long_part_to_fit_and_reports_what_does_not()
    {
        var tall = R(20, 150);                          // only fits lying down on a 200 × 100 board
        var huge = R(500, 500);
        var r = Nesting.Nest(new[] { tall, huge }, 200, 100, new NestOptions());
        Assert.Equal(1, r.Placed);
        Assert.Single(r.NotPlaced);
        Assert.Equal(150, tall.Width, 6);
        Assert.Equal(500, huge.X);                       // left where it was
    }

    [Fact]
    public void Uses_the_real_shape_not_the_box()
    {
        // Two right triangles make a rectangle; their boxes (100 × 60 each) would not both fit on a 110 × 70 board.
        DesignShape T() => new() { Kind = ShapeKind.VectorPath, Width = 100, Height = 60,
            Contours = { new VectorContour { Points = { new(0, 0), new(1, 0), new(0, 1) } } } };
        var a = T(); var b = T();
        var r = Nesting.Nest(new[] { a, b }, 110, 70, new NestOptions { SpacingMm = 2, MarginMm = 2 });
        Assert.Equal(2, r.Placed);
        NoOverlap(new[] { a, b }, 2);
    }

    [Fact]
    public void Figures_have_the_size_asked_and_the_star_its_points()
    {
        foreach (FigureKind k in Enum.GetValues<FigureKind>())
        {
            var s = Figures.Create(new FigureOptions { Kind = k, WidthMm = 60, HeightMm = 40, Count = 6, InnerPercent = 50, CornerMm = 5 }, 10, 20);
            Assert.Equal((10.0, 20.0, 60.0, 40.0), (s.X, s.Y, s.Width, s.Height));
            Assert.True(Math.Abs(Clipper.Area(PocketToolpaths.Region(s))) > 100, k.ToString());
        }
        Assert.Equal(10, Figures.Outline(new FigureOptions { Kind = FigureKind.Star, Count = 5 })[0].Count);
        Assert.Equal(2, Figures.Outline(new FigureOptions { Kind = FigureKind.Ring })[0].Count > 0 ? Figures.Outline(new FigureOptions { Kind = FigureKind.Ring }).Count : 0);
        Assert.NotNull(Figures.Validate(new FigureOptions { Kind = FigureKind.Polygon, Count = 2 }));
        Assert.NotNull(Figures.Validate(new FigureOptions { Kind = FigureKind.RoundedRectangle, WidthMm = 20, HeightMm = 10, CornerMm = 6 }));
    }
}
