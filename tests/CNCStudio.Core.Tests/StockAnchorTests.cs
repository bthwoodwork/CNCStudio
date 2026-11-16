using CNCStudio.Core;
using Xunit;

public class StockAnchorTests
{
    // $23: bit 0 = X homes to the left, bit 1 = Y homes to the front.
    private static MachineProfile Table(HomeCorner home) => new()
    {
        WorkWidthMm = 750, WorkLengthMm = 500,
        HomingDirInvertMask = home switch { HomeCorner.BackRight => 0, HomeCorner.BackLeft => 1, HomeCorner.FrontRight => 2, _ => 3 }
    };

    [Fact]
    public void The_default_anchor_is_the_corner_nearest_home()
    {
        Assert.Equal(new StockAnchor(0, 2), StockAnchor.NearestHome(HomeCorner.FrontLeft));
        Assert.Equal(new StockAnchor(2, 0), StockAnchor.NearestHome(HomeCorner.BackRight));
    }

    [Fact]
    public void Front_left_home_measures_the_front_left_corner_from_the_front_left()
    {
        // Material 300 × 60 with its back-left corner at canvas (50, 100): front edge at canvas y 160 → 340 from the front.
        var (x, y) = StockAnchor.NearestHome(HomeCorner.FrontLeft).ToTable(Table(HomeCorner.FrontLeft), 50, 100, 300, 60);
        Assert.Equal((50.0, 340.0), (x, y));
    }

    [Theory]
    [InlineData(HomeCorner.FrontLeft, 1, 1)]
    [InlineData(HomeCorner.BackRight, 2, 0)]
    [InlineData(HomeCorner.FrontRight, 0, 2)]
    [InlineData(HomeCorner.BackLeft, 2, 1)]
    public void FromTable_undoes_ToTable_for_every_anchor(HomeCorner home, int col, int row)
    {
        var a = new StockAnchor(col, row);
        var m = Table(home);
        var (x, y) = a.ToTable(m, 50, 100, 300, 60);
        Assert.Equal((50.0, 100.0), a.FromTable(m, x, y, 300, 60));
    }

    [Fact]
    public void The_centre_anchor_moves_the_material_so_its_middle_lands_on_the_point()
    {
        var m = Table(HomeCorner.BackLeft);
        var (sx, sy) = new StockAnchor(1, 1).FromTable(m, 375, 250, 300, 60);
        Assert.Equal((225.0, 220.0), (sx, sy));
    }

    [Fact]
    public void Names_read_naturally()
    {
        Assert.Equal("mitten", new StockAnchor(1, 1).Name);
        Assert.Equal("främre vänstra hörnet", new StockAnchor(0, 2).Name);
        Assert.Equal("bakre kanten", new StockAnchor(1, 0).Name);
        Assert.Equal("högra kanten", new StockAnchor(2, 1).Name);
    }
}
