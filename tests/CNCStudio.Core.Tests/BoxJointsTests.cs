using Clipper2Lib;
using CNCStudio.Core;
using Xunit;

public class BoxJointsTests
{
    private static BoxOptions Box() => new() { LengthMm = 200, WidthMm = 120, HeightMm = 80, ThicknessMm = 12, FingerWidthMm = 20, ClearanceMm = 0, ToolDiameterMm = 6 };

    [Fact]
    public void The_finger_count_is_odd_and_near_the_wanted_width()
    {
        Assert.Equal(3, BoxJoints.Fingers(60, 20));
        Assert.Equal(5, BoxJoints.Fingers(80, 20));        // 4 would be even
        Assert.Equal(11, BoxJoints.Fingers(200, 20));       // 10 → 11
        Assert.Equal(3, BoxJoints.Fingers(30, 20));
    }

    [Fact]
    public void Five_panels_of_the_right_size_and_the_bottom_corners_have_one_owner()
    {
        var panels = BoxJoints.Panels(Box());
        Assert.Equal(new[] { "Framsida", "Baksida", "Vänster gavel", "Höger gavel", "Botten" }, panels.Select(p => p.Name));
        Assert.Equal((200.0, 80.0), (panels[0].Width, panels[0].Height));
        Assert.Equal((120.0, 80.0), (panels[2].Width, panels[2].Height));
        Assert.Equal((200.0, 120.0), (panels[4].Width, panels[4].Height));
        // The front keeps its corner squares; the end and the bottom have them cut away.
        Assert.True(Covers(panels[0], 2, 78));
        Assert.False(Covers(panels[2], 2, 78));
        Assert.False(Covers(panels[4], 2, 2));
    }

    [Fact]
    public void A_front_finger_fills_an_end_slot_along_the_corner()
    {
        var p = BoxJoints.Panels(Box());
        double up = 80 - 12;                                // the corner above the bottom
        int n = BoxJoints.Fingers(up, 20);
        double s = up / n;
        for (int i = 0; i < n; i++)
        {
            double y = (i + 0.5) * s;
            // At every height exactly one of the two panels has material in the shared corner strip.
            Assert.NotEqual(Covers(p[0], 6, y), Covers(p[2], 6, y));
        }
    }

    [Fact]
    public void No_finger_is_thinner_than_the_wanted_finger_width_by_much()
    {
        // Every straight piece of every outline is at least ~a third of the finger width or the board thickness:
        // no thin slivers at the corners.
        foreach (var p in BoxJoints.Panels(Box()))
            foreach (var c in p.Outline)
                for (int i = 0; i < c.Count; i++)
                {
                    var a = c[i]; var b = c[(i + 1) % c.Count];
                    double len = Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
                    Assert.True(len > 6, $"{p.Name}: a {len:0.##} mm piece at ({a.X}, {a.Y})");
                }
    }

    [Fact]
    public void Inside_measures_add_the_board_and_a_too_wide_cutter_is_refused()
    {
        var o = Box(); o.InsideMeasures = true;
        Assert.Equal((224.0, 144.0, 92.0), BoxJoints.Outer(o));
        var narrow = Box(); narrow.FingerWidthMm = 6; narrow.ToolDiameterMm = 8;
        Assert.NotNull(BoxJoints.Validate(narrow));
    }

    private static bool Covers(BoxPanel p, double x, double y)
    {
        var paths = new PathsD(p.Outline.Select(c => new PathD(c.Select(v => new PointD(v.X, v.Y)))));
        int inside = 0;
        foreach (var path in paths)
            if (Clipper.PointInPolygon(new PointD(x, y), path) == PointInPolygonResult.IsInside) inside++;
        return inside % 2 == 1;
    }
}
