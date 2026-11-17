using CNCStudio.Core;
using Xunit;

public class ToolWearTests
{
    [Fact]
    public void A_run_adds_its_time_to_the_named_tools_and_reports_one_that_becomes_due_once()
    {
        var lib = new ToolLibrary();
        var flat = new ToolDefinition { Name = "Rak 6 mm", ChangeIntervalHours = 1 };
        var v = new ToolDefinition { Name = "V 90°" };
        lib.Tools.Add(flat); lib.Tools.Add(v);

        Assert.Empty(ToolWear.Record(lib, new[] { "rak 6 mm", "V 90°", "Okänd" }, 40));
        Assert.Equal(40, flat.RunMinutes);
        Assert.Equal(40, v.RunMinutesSinceChange);

        var due = ToolWear.Record(lib, new[] { "Rak 6 mm", "Rak 6 mm" }, 25);     // twice in the list counts once
        Assert.Same(flat, Assert.Single(due));
        Assert.Equal(65, flat.RunMinutes);
        Assert.Empty(ToolWear.Record(lib, new[] { "Rak 6 mm" }, 10));            // already due: not reported again
        Assert.False(v.ChangeDue);                                                // no interval set
    }

    [Fact]
    public void The_hours_read_naturally() => Assert.Equal("2 h 5 min", ToolWear.Hours(125));
}
