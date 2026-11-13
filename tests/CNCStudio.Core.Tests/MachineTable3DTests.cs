namespace CNCStudio.Core.Tests;

/// <summary>The machine's work area in the 3D view (material frame: X right, Y back, Z up from the material's top).</summary>
public class MachineTable3DTests
{
    private static CncDocument Doc(double stockX, double stockY, HomeCorner corner = HomeCorner.BackLeft)
    {
        var d = new CncDocument { MaterialWidth = 300, MaterialHeight = 200, MaterialThickness = 18 };
        d.Machine.WorkWidthMm = 750; d.Machine.WorkLengthMm = 500; d.Machine.HomingDirInvertMask = Mask(corner);
        d.Stock.X = stockX; d.Stock.Y = stockY;
        return d;
    }

    /// <summary>$23: bit 0 = X homes to the left, bit 1 = Y homes to the front.</summary>
    private static int Mask(HomeCorner c) => c switch
    {
        HomeCorner.BackRight => 0, HomeCorner.BackLeft => 1, HomeCorner.FrontRight => 2, _ => 3
    };

    [Fact]
    public void The_table_lies_around_the_material_as_in_the_machine_view()
    {
        // Material 50 mm from the left and 100 mm from the back of a 750 × 500 table.
        var a = MachineTable3D.For(Doc(50, 100));
        Assert.Equal((-50.0, 700.0), (a.Left, a.Right));
        Assert.Equal(200 + 100.0, a.Back);                 // the back edge is 100 mm behind the material's back edge (y = 200)
        Assert.Equal(200 + 100.0 - 500, a.Front);          // and the table is 500 mm long
        Assert.Equal(-18, a.TopZ);                         // the material lies on the table
        Assert.Equal((750.0, 500.0), (a.Width, a.Length));
    }

    [Fact]
    public void Material_in_the_back_left_corner_starts_at_the_tables_corner()
    {
        var a = MachineTable3D.For(Doc(0, 0));
        Assert.Equal(0, a.Left);
        Assert.Equal(200, a.Back);                          // material's back edge = table's back edge
    }

    [Theory]
    [InlineData(HomeCorner.BackLeft, -50.0, 300.0)]
    [InlineData(HomeCorner.BackRight, 700.0, 300.0)]
    [InlineData(HomeCorner.FrontLeft, -50.0, -200.0)]
    [InlineData(HomeCorner.FrontRight, 700.0, -200.0)]
    public void Home_is_the_right_corner(HomeCorner corner, double x, double y)
    {
        var a = MachineTable3D.For(Doc(50, 100, corner));
        Assert.Equal((x, y), MachineTable3D.HomePoint(a, corner));
    }

    [Fact]
    public void Grid_lines_count_from_the_home_corner()
    {
        var a = MachineTable3D.For(Doc(50, 100, HomeCorner.BackRight));
        var lines = MachineTable3D.GridLines(a, HomeCorner.BackRight);
        Assert.Equal(100, MachineTable3D.GridStep(a));
        Assert.Contains(lines, l => l.X0 == 600 && l.X1 == 600);        // 100 mm from the right edge (700)
        Assert.Contains(lines, l => l.Y0 == 200 && l.Y1 == 200);        // 100 mm in front of the back edge (300)
        Assert.Equal(7 + 4, lines.Count);                                // 750 → 7 lines across, 500 → 4 along
    }

    [Fact]
    public void Without_material_the_table_starts_at_the_origin()
    {
        var d = Doc(50, 100);
        d.HasMaterial = false;
        var a = MachineTable3D.For(d);
        Assert.Equal((0.0, 750.0, 0.0, 500.0, 0.0), (a.Left, a.Right, a.Front, a.Back, a.TopZ));
    }

    [Fact]
    public void A_small_table_gets_a_finer_grid()
    {
        var d = Doc(0, 0);
        d.Machine.WorkWidthMm = 300; d.Machine.WorkLengthMm = 300;
        Assert.Equal(50, MachineTable3D.GridStep(MachineTable3D.For(d)));
    }

    [Fact]
    public void The_tool_marker_from_the_2D_table_lands_on_the_same_spot_in_3D()
    {
        // Material at (50, 100) from the table's back-left corner: the tool over the material's back-left
        // corner is at the view's (0, material length); over its front-left corner at (0, 0).
        var a = MachineTable3D.For(Doc(50, 100));
        Assert.Equal((0.0, 200.0), MachineTable3D.FromTable(a, 50, 100));
        Assert.Equal((0.0, 0.0), MachineTable3D.FromTable(a, 50, 300));
        Assert.Equal((a.Left, a.Back), MachineTable3D.FromTable(a, 0, 0));
        var empty = Doc(50, 100); empty.HasMaterial = false;
        var e = MachineTable3D.For(empty);
        Assert.Equal((0.0, 500.0), MachineTable3D.FromTable(e, 0, 0));      // back-left corner of a 750 × 500 table
    }
}
