using CNCStudio.Core;
using Xunit;

namespace CNCStudio.Core.Tests;

public class KeepOnScreenTests
{
    [Fact]
    public void WindowOnScreen_StaysWhereItIs()
    {
        Assert.Equal((100.0, 50.0), WindowPlacement.KeepOnScreen(100, 50, 360, 760, 0, 0, 1920, 1080));
    }

    [Fact]
    public void WindowPastTheRightEdge_IsMovedIn()
    {
        Assert.Equal((1560.0, 200.0), WindowPlacement.KeepOnScreen(1880, 200, 360, 600, 0, 0, 1920, 1080));
    }

    [Fact]
    public void WindowTallerThanTheScreen_KeepsItsTitleBarOnScreen()
    {
        Assert.Equal((10.0, 0.0), WindowPlacement.KeepOnScreen(10, -40, 360, 2000, 0, 0, 1920, 1080));
    }

    [Fact]
    public void SecondMonitorToTheLeft_CountsAsScreen()
    {
        Assert.Equal((-1500.0, 100.0), WindowPlacement.KeepOnScreen(-1500, 100, 360, 600, -1920, 0, 3840, 1080));
    }

    [Fact]
    public void UnknownPosition_GoesToTheCorner()
    {
        Assert.Equal((0.0, 0.0), WindowPlacement.KeepOnScreen(double.NaN, double.NaN, 360, 600, 0, 0, 1920, 1080));
    }
}
