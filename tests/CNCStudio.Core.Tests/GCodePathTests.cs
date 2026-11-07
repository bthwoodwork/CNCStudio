using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

/// <summary>The loaded program as moves, for drawing the run.</summary>
public class GCodePathTests
{
    [Fact]
    public void Moves_keep_their_line_and_follow_absolute_relative_and_rapid_modes()
    {
        var lines = new[]
        {
            "(kommentar X99)", "G21", "G90", "G0 Z10", "G0 X10 Y20", "G1 Z-2 F300", "G1 X30 F1000", "G91", "G1 Y5", "G90", "G53 G0 Z-1", "M30"
        };
        var moves = GCodePath.Parse(lines);
        Assert.Equal(5, moves.Count);                              // comment, G53 and M30 are not moves
        Assert.Equal(new ProgramMove(3, 0, 0, 0, 0, 0, 10, true), moves[0]);
        Assert.Equal((4, true), (moves[1].LineIndex, moves[1].Rapid));
        Assert.Equal((5, false, -2.0), (moves[2].LineIndex, moves[2].Rapid, moves[2].Z1));
        Assert.Equal((30.0, 20.0), (moves[3].X1, moves[3].Y1));
        Assert.Equal((30.0, 25.0, false), (moves[4].X1, moves[4].Y1, moves[4].Rapid)); // G91: relative
    }

    [Fact]
    public void A_generated_program_parses_into_its_cuts()
    {
        var doc = new CncDocument();
        doc.Motion.StockTopMachineZ = -40;
        var s = new DesignShape { Kind = ShapeKind.Rectangle, X = 50, Y = 50, Width = 100, Height = 60 };
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, UseTabs = false, TotalDepth = 4, DepthPerPass = 2 });
        var r = GCodeGenerator.Generate(doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
        var moves = GCodePath.Parse(r.Lines);
        Assert.Contains(moves, m => !m.Rapid && m.Z1 == -4);
        Assert.Equal(-4, moves.Min(m => m.Z1), 6);
    }
}
