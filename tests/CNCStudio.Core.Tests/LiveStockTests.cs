using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

/// <summary>The material cut away while a program runs (3D view during a run).</summary>
public class LiveStockTests
{
    private static CncDocument Doc(WorkZero zero = WorkZero.FrontLeft) =>
        new() { Name = "x", MaterialWidth = 100, MaterialHeight = 60, MaterialThickness = 10, WorkZero = zero };

    private static readonly ToolSpec Flat6 = new(ToolKind.FlatEndMill, 6, 0);

    /// <summary>A slot along X at Y 30, 2 mm deep, from X 20 to X 80.</summary>
    private static IReadOnlyList<ProgramMove> Slot() => GCodePath.Parse(new[]
    {
        "G21", "G90", "G0 Z5", "G0 X20 Y30", "G1 Z-2 F300", "G1 X80 F1000", "G0 Z5", "M30"
    });

    private static float H(StockHeightMap map, double x, double y) => map[(int)(x / map.Cell), (int)(y / map.Cell)];

    [Fact]
    public void Only_the_part_that_has_run_is_cut()
    {
        var doc = Doc();
        var moves = Slot();
        int cut = moves.ToList().FindIndex(m => !m.Rapid && m.X1 > m.X0);   // the slot along X
        var live = new LiveStockSimulator(StockSimulator.NewMap(doc, 0.5), doc.WorkZero, Flat6);
        Assert.True(live.Advance(moves, cut, 0));                             // the plunge at X 20 has run, the slot not yet
        Assert.Equal(-2, H(live.Map, 20, 30), 3);
        Assert.Equal(0, H(live.Map, 30, 30), 3);
        Assert.False(live.Advance(moves, cut, 0));                            // nothing new
        live.Advance(moves, cut, 0.5);                                         // half the slot: up to X 50
        Assert.Equal(-2, H(live.Map, 30, 30), 3);
        Assert.Equal(0, H(live.Map, 70, 30), 3);
        live.Advance(moves, moves.Count, 0);                                   // the whole program
        Assert.Equal(-2, H(live.Map, 70, 30), 3);
        Assert.Equal(0, H(live.Map, 50, 40), 3);                              // 10 mm beside the slot: untouched
    }

    [Fact]
    public void Advancing_twice_to_the_same_place_cuts_the_same_as_once_and_going_back_starts_over()
    {
        var doc = Doc();
        var moves = Slot();
        var a = new LiveStockSimulator(StockSimulator.NewMap(doc, 0.5), doc.WorkZero, Flat6);
        for (double f = 0; f <= 1.0001; f += 0.1) a.Advance(moves, 5, Math.Min(1, f));
        a.Advance(moves, moves.Count, 0);
        var b = new LiveStockSimulator(StockSimulator.NewMap(doc, 0.5), doc.WorkZero, Flat6);
        b.Advance(moves, moves.Count, 0);
        Assert.Equal(b.Map.RemovedVolume, a.Map.RemovedVolume, 6);
        Assert.True(a.Advance(moves, 0, 0));                                   // a new run: back to uncut
        Assert.Equal(0, a.Map.RemovedVolume, 9);
    }

    [Fact]
    public void A_centred_work_zero_is_moved_to_the_material()
    {
        var doc = Doc(WorkZero.Center);
        var moves = GCodePath.Parse(new[] { "G90", "G0 Z5", "G0 X0 Y0", "G1 Z-3 F300", "G0 Z5" });
        var live = new LiveStockSimulator(StockSimulator.NewMap(doc, 0.5), doc.WorkZero, Flat6);
        live.Advance(moves, moves.Count, 0);
        Assert.Equal(-3, H(live.Map, 50, 30), 3);                              // X0 Y0 = the middle of 100 × 60
        Assert.Equal(-3, live.Map.MinHeight, 3);
    }

    [Fact]
    public void It_starts_from_what_the_earlier_program_left()
    {
        var doc = Doc();
        var start = StockSimulator.NewMap(doc, 0.5);
        StockSimulator.StampSegment(start, 10, 10, -5, 10, 10, -5, Flat6);    // program 1 drilled a hole at X10 Y10
        var live = new LiveStockSimulator(start, doc.WorkZero, Flat6);
        Assert.Equal(-5, H(live.Map, 10, 10), 3);
        live.Advance(Slot(), Slot().Count, 0);
        Assert.Equal(-5, H(live.Map, 10, 10), 3);
        Assert.Equal(-2, H(live.Map, 50, 30), 3);
        Assert.Equal(0, H(start, 50, 30), 3);                                  // the start map itself is not changed
    }

    [Fact]
    public void The_whole_generated_program_cuts_about_the_same_as_the_planned_simulation()
    {
        var doc = new CncDocument { Name = "Ficka", MaterialWidth = 100, MaterialHeight = 80, MaterialThickness = 12 };
        doc.Motion.StockTopMachineZ = -40;
        var s = new DesignShape { Kind = ShapeKind.Rectangle, X = 30, Y = 20, Width = 40, Height = 30 };
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, Kind = CamOperationKind.Pocket, ToolDiameter = 6, TotalDepth = 4, DepthPerPass = 2, UseTabs = false });
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
        var planned = StockSimulator.Simulate(doc, report, 0.5);
        var code = GCodeGenerator.Generate(doc, report);
        Assert.True(code.Success);
        var moves = GCodePath.Parse(code.Lines);
        var live = new LiveStockSimulator(StockSimulator.NewMap(doc, 0.5), doc.WorkZero, ToolSpec.Of(doc.CamOperations[0]));
        live.Advance(moves, moves.Count, 0);
        Assert.InRange(live.Map.RemovedVolume, planned.RemovedVolume * 0.97, planned.RemovedVolume * 1.03);
        Assert.Equal(planned.MinHeight, live.Map.MinHeight, 3);
    }
}
