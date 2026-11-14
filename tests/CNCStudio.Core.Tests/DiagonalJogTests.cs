using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

/// <summary>Jog buttons: straight and diagonal directions, and how far one jog goes.</summary>
public class DiagonalJogTests
{
    [Theory]
    [InlineData("X+", 1, 0, 0)] [InlineData("Y-", 0, -1, 0)] [InlineData("Z+", 0, 0, 1)]
    [InlineData("X-Y+", -1, 1, 0)] [InlineData("X+Y+", 1, 1, 0)] [InlineData("X-Y-", -1, -1, 0)] [InlineData("X+Y-", 1, -1, 0)]
    public void Tags_give_the_direction(string tag, int x, int y, int z) =>
        Assert.Equal((x, y, z), GrblMachineCommands.JogDirection(tag)!.Value);

    [Theory]
    [InlineData("")] [InlineData("X")] [InlineData("X*")] [InlineData("X+X-")] [InlineData("X+Z+")] [InlineData("A+")]
    public void Anything_else_is_not_a_direction(string tag) => Assert.Null(GrblMachineCommands.JogDirection(tag));

    [Fact]
    public void A_diagonal_step_goes_the_step_in_both_X_and_Y()
    {
        var (dx, dy, dz) = GrblMachineCommands.JogMove((1, -1, 0), stepXY: 5, stepZ: 2, feed: 3000, step: true);
        Assert.Equal((5.0, -5.0, 0.0), (dx, dy, dz));
        Assert.Equal("$J=G91 G21 X5 Y-5 F3000", GrblMachineCommands.Jog(dx, dy, dz, 3000));
    }

    [Fact]
    public void A_continuous_diagonal_segment_is_as_long_as_a_straight_one()
    {
        double straight = GrblMachineCommands.JogMove((1, 0, 0), 5, 2, 3000, step: false).Dx;
        var (dx, dy, _) = GrblMachineCommands.JogMove((-1, 1, 0), 5, 2, 3000, step: false);
        Assert.Equal(straight, Math.Sqrt(dx * dx + dy * dy), 9);
        Assert.True(dx < 0 && dy > 0);
    }

    [Fact]
    public void Z_steps_use_the_Z_step()
    {
        Assert.Equal((0.0, 0.0, -2.0), GrblMachineCommands.JogMove((0, 0, -1), 5, 2, 3000, step: true));
    }
}
