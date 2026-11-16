using CNCStudio.Core.GCode;
using Xunit;

public class GCodeTransformTests
{
    private static string[] Run(XYMap map, params string[] lines)
    {
        var r = GCodeTransform.Apply(lines, map);
        Assert.True(r.Ok, string.Join("; ", r.Problems));
        return r.Lines.ToArray();
    }

    [Fact]
    public void Translate_moves_absolute_xy_and_leaves_z_feed_and_comments()
    {
        var o = Run(XYMap.Translate(10, 5), "G90 G21", "G0 X1 Y2 Z5 (start)", "G1 Z-1 F300", "G1 X3 F1000");
        Assert.Equal(new[] { "G90 G21", "G0 X11 Y7 Z5 (start)", "G1 Z-1 F300", "G1 X13 F1000" }, o);
    }

    [Fact]
    public void Relative_moves_are_not_translated_but_are_rotated()
    {
        Assert.Equal(new[] { "G91", "G1 X5 Y0" }, Run(XYMap.Translate(100, 100), "G91", "G1 X5 Y0"));
        Assert.Equal(new[] { "G91", "G1 X0 Y5" }, Run(XYMap.Rotate(90, 0, 0), "G91", "G1 X5 Y0"));
    }

    [Fact]
    public void Rotation_fills_in_the_axis_a_line_left_out()
    {
        // From (10, 0) a move to X20 (Y stays 0) rotated 90° ccw: (0, 10) → (0, 20).
        var o = Run(XYMap.Rotate(90, 0, 0), "G90", "G0 X10 Y0", "G1 X20");
        Assert.Equal("G0 X0 Y10", o[1]);
        Assert.Equal("G1 X0 Y20", o[2]);
    }

    [Fact]
    public void A_mirror_swaps_arc_direction_and_mirrors_the_centre_offset()
    {
        var o = Run(XYMap.MirrorX(0), "G90", "G0 X10 Y0", "G2 X0 Y-10 I-10 J0");
        Assert.Equal("G3 X0 Y-10 I10 J0", o[2]);
    }

    [Fact]
    public void A_modal_arc_without_G2_on_the_line_gets_the_swapped_direction_written()
    {
        var o = Run(XYMap.MirrorY(0), "G90", "G0 X10 Y0", "G2 X0 Y-10 I-10 J0", "X-10 Y0 I0 J10");
        Assert.StartsWith("G3 ", o[2]);
        Assert.Equal("G3 X-10 Y0 I0 J-10", o[3]);
    }

    [Fact]
    public void Stored_positions_and_inches_are_refused_and_nothing_changes()
    {
        var lines = new[] { "G90", "G28 X0 Y0", "G1 X5" };
        var r = GCodeTransform.Apply(lines, XYMap.Translate(1, 1));
        Assert.False(r.Ok);
        Assert.Same(lines, r.Lines);
        Assert.False(GCodeTransform.Apply(new[] { "G20", "G1 X1" }, XYMap.Identity).Ok);
    }

    [Fact]
    public void Machine_coordinate_lines_are_left_as_written()
    {
        var o = Run(XYMap.Translate(10, 10), "G90", "G53 G0 X-5 Y-5", "G0 X1 Y1");
        Assert.Equal("G53 G0 X-5 Y-5", o[1]);
        Assert.Equal("G0 X11 Y11", o[2]);
    }

    [Fact]
    public void Grid_copies_lift_between_copies_and_end_once()
    {
        var r = GCodeTransform.Grid(new[] { "G90", "G0 Z5", "G0 X0 Y0", "G1 Z-1", "G1 X10", "G0 Z5", "M30" }, 2, 2, 50, 30, 5);
        Assert.True(r.Ok);
        Assert.Equal("M30", r.Lines[^1]);
        Assert.Single(r.Lines, l => l == "M30");
        Assert.Contains("G1 X60", r.Lines);                       // second copy, 50 mm to the right
        Assert.Contains("G0 X50 Y30", r.Lines);                   // row 2 runs back: its first copy is at column 2
        Assert.Equal(3, r.Lines.Count(l => l.StartsWith("(Kopia")));
    }
}
