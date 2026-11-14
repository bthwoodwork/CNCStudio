using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

/// <summary>Macros: the library, and what is sent and refused.</summary>
public class MacroTests
{
    [Fact]
    public void Comments_and_blank_lines_go_and_the_rest_is_sent_in_order()
    {
        var p = MacroCode.Prepare("(lyft först)\nG53 G0 Z-5 ; maskinkoordinater\n\n  g90 g0 x0 y0\n$#");
        Assert.True(p.Ok);
        Assert.Equal(new[] { "G53G0Z-5", "G90G0X0Y0", "$#" }, p.Lines);
    }

    [Theory]
    [InlineData("$100=80", "Maskininställningar")]
    [InlineData("$RST=*", "$RST")]
    [InlineData("$N0=G54", "startblock")]
    [InlineData("$SLP", "$SLP")]
    [InlineData("!", "realtidstecken")]
    [InlineData("G0 X1 ; åäö är ok i kommentar\nG0 Xå", "ASCII")]
    public void What_does_not_belong_in_a_macro_is_refused_with_the_reason(string code, string reason)
    {
        var p = MacroCode.Prepare(code);
        Assert.False(p.Ok);
        Assert.Contains(p.Problems, x => x.Contains(reason));
        Assert.StartsWith("Rad ", p.Problems[0]);
    }

    [Fact]
    public void Long_lines_and_empty_macros_are_refused()
    {
        Assert.Contains("högst 70", MacroCode.Prepare("G1 X" + new string('1', 80)).Problems.Single());
        Assert.Contains("ingen kod", MacroCode.Prepare("; bara en kommentar").Problems.Single());
    }

    [Fact]
    public void Homing_and_unlock_and_reading_settings_are_allowed()
    {
        Assert.True(MacroCode.Prepare("$H\n$X\n$$\n$G").Ok);
    }

    [Fact]
    public void The_example_macros_pass_and_the_library_is_saved_and_read_back()
    {
        var lib = MacroLibrary.Defaults();
        Assert.All(lib.Macros, m => Assert.True(MacroCode.Prepare(m.Code).Ok, m.Name));
        var path = Path.Combine(Path.GetTempPath(), "cncstudio-macros-" + Guid.NewGuid().ToString("N"), "makron.json");
        lib.Macros.Add(new Macro { Name = "Mitt", Code = "G0 X10" });
        lib.Save(path);
        var back = MacroLibrary.Load(path);
        Assert.Equal(lib.Macros.Select(m => m.Name), back.Macros.Select(m => m.Name));
        Assert.Equal("G0 X10", back.Macros.Last().Code);
    }

    [Fact]
    public void All_snippets_pass_the_check()
    {
        Assert.All(MacroCode.Snippets, s => Assert.True(MacroCode.Prepare(s.Code).Ok, s.Code));
    }

    [Fact(Timeout = 60000)]
    public async Task A_macro_runs_on_the_simulated_machine()
    {
        var sim = new SimulatedGrbl(speedFactor: 60);
        using var client = new GrblClient(sim);
        await client.ConnectAsync(TimeSpan.FromSeconds(1));
        Assert.True((await client.SendLineAsync("$H", timeout: TimeSpan.FromSeconds(30))).IsOk);
        var p = MacroCode.Prepare("G53 G0 Z-5\nG53 G0 X-200 Y-200\nG10 L20 P0 X0 Y0\nG91 G0 X10 Y10\nG90");
        var result = await new JobRunner(client, p.Lines, null).RunAsync();
        Assert.True(result.Outcome == JobOutcome.Finished, result.Message);
        Assert.Equal(p.Lines.Count, result.AckedLines);
    }

    [Fact(Timeout = 60000)]
    public async Task A_macro_that_would_pass_a_limit_stops_with_the_alarm()
    {
        var sim = new SimulatedGrbl(speedFactor: 60);
        using var client = new GrblClient(sim);
        await client.ConnectAsync(TimeSpan.FromSeconds(1));
        Assert.True((await client.SendLineAsync("$H", timeout: TimeSpan.FromSeconds(30))).IsOk);
        var p = MacroCode.Prepare("G53 G0 Z-5\nG53 G0 X50");   // machine X must stay ≤ 0
        var result = await new JobRunner(client, p.Lines, null).RunAsync();
        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Contains("Soft limit", result.Message);
    }

    [Theory]
    [InlineData("G53 G0 Z-5", true)] [InlineData("G1 X5 F100", true)] [InlineData("G2 X1 Y1 I1", true)] [InlineData("G38.2 Z-10 F50", true)]
    [InlineData("M3 S10000", true)] [InlineData("$H", true)] [InlineData("$J=G91 X1 F100", true)] [InlineData("G90 G0 X0", true)]
    [InlineData("$#", false)] [InlineData("$G", false)] [InlineData("M5", false)] [InlineData("G10 L20 P0 X0", false)] [InlineData("G4 P1", false)]
    [InlineData("G21", false)] [InlineData("G17", false)] [InlineData("G54", false)]
    public void Moving_macros_are_told_apart_from_reading_ones(string code, bool moves) =>
        Assert.Equal(moves, MacroCode.MovesMachine(MacroCode.Prepare(code).Lines));
}
