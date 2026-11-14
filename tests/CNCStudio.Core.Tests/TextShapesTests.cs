using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CNCStudio.Core;
using Xunit;

namespace CNCStudio.Core.Tests;

public class TextShapesTests
{
    private static IReadOnlyList<(double, double)> Square(double x, double y, double s, bool clockwise = true)
    {
        var p = new List<(double, double)> { (x, y), (x + s, y), (x + s, y + s), (x, y + s) };
        if (!clockwise) p.Reverse();
        return p;
    }

    [Fact]
    public void Scales_to_the_height_and_keeps_the_aspect()
    {
        // Two "letters" 10 × 20 units with a gap, in font units.
        var shape = TextShapes.FromOutlines(new[] { Square(0, 0, 10), Square(15, 0, 10) }, 40, 5, 7, new TextSource { Content = "II" });
        Assert.Equal(ShapeKind.VectorPath, shape.Kind);
        Assert.Equal(40, shape.Height, 3);
        Assert.Equal(100, shape.Width, 3);           // 25 units wide × 4
        Assert.Equal((5.0, 7.0), (shape.X, shape.Y));
        Assert.Equal(2, shape.Contours.Count);
        Assert.All(shape.Contours.SelectMany(c => c.Points), p => Assert.InRange(p.X, -1e-9, 1 + 1e-9));
        Assert.Equal("II", shape.Text!.Content);
    }

    [Fact]
    public void Overlapping_letters_join_and_holes_stay()
    {
        // Two overlapping squares (a script join) and an "o": outer square with a counter wound the other way.
        var outlines = new[] { Square(0, 0, 10), Square(8, 0, 10), Square(30, 0, 10), Square(33, 3, 4, clockwise: false) };
        var shape = TextShapes.FromOutlines(outlines, 10, 0, 0, new TextSource { Content = "xo" });
        Assert.Equal(3, shape.Contours.Count);       // joined pair, the o, its hole
        var region = PocketToolpaths.Region(shape);
        double area = Math.Abs(Clipper2Lib.Clipper.Area(region));
        Assert.Equal(18 * 10 + 100 - 16, area, 1);   // the overlap counted once, the hole left out
    }

    [Fact]
    public void Pocket_of_text_cuts_around_the_hole()
    {
        var shape = TextShapes.FromOutlines(new[] { Square(0, 0, 20), Square(7, 7, 6, clockwise: false) }, 20, 0, 0, new TextSource { Content = "o" });
        var op = new CamOperation { ShapeId = shape.Id, Kind = CamOperationKind.Pocket, ToolDiameter = 2, TotalDepth = 1, DepthPerPass = 1, UseTabs = false };
        var preview = PocketToolpaths.Generate(shape, op, 10);
        Assert.NotEmpty(preview.Centerlines);
        // Every tool centre stays at least the radius (1 mm) from the hole (7..13).
        static double ToHole(double x, double y)
        {
            double dx = Math.Max(Math.Max(7 - x, 0), x - 13), dy = Math.Max(Math.Max(7 - y, 0), y - 13);
            return Math.Sqrt(dx * dx + dy * dy);
        }
        Assert.All(preview.Centerlines.SelectMany(l => l), p => Assert.True(ToHole(p.X, p.Y) >= 0.98, $"{p.X};{p.Y}"));
    }

    [Fact]
    public void Refuses_empty_text_and_bad_heights()
    {
        Assert.Throws<ArgumentException>(() => TextShapes.FromOutlines(Array.Empty<IReadOnlyList<(double, double)>>(), 10, 0, 0, new TextSource()));
        Assert.Throws<ArgumentException>(() => TextShapes.FromOutlines(new[] { Square(0, 0, 1) }, 0.5, 0, 0, new TextSource()));
        Assert.Equal("Skriv en text.", new TextSource { Content = "  " }.Validate());
        Assert.Null(new TextSource { Content = "Hej" }.Validate());
    }

    [Fact]
    public void Text_info_is_saved_with_the_project()
    {
        var shape = TextShapes.FromOutlines(new[] { Square(0, 0, 10) }, 10, 0, 0, new TextSource { Content = "Å", FontFamily = "Georgia", Bold = true, Align = TextAlign.Center });
        var back = JsonSerializer.Deserialize<DesignShape>(JsonSerializer.Serialize(shape))!;
        Assert.Equal("Å", back.Text!.Content);
        Assert.Equal("Georgia", back.Text.FontFamily);
        Assert.True(back.Text.Bold);
        Assert.Equal(TextAlign.Center, back.Text.Align);
        Assert.Null(JsonSerializer.Deserialize<DesignShape>(JsonSerializer.Serialize(new DesignShape()))!.Text);
    }
}
