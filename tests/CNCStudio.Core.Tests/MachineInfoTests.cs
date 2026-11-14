using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

/// <summary>Maskininformation: modes from $G, inputs from Pn:, and locking the stepper motors.</summary>
public class MachineInfoTests
{
    [Fact]
    public void The_usual_modes_are_read_in_plain_words()
    {
        var m = MachineInfo.ParseModes("[GC:G0 G54 G17 G21 G90 G94 M5 M9 T0 F0 S0]")!;
        Assert.Equal("G54", m.Single(x => x.Group == "Koordinatsystem").Code);
        Assert.Equal("millimeter", m.Single(x => x.Group == "Enheter").Meaning);
        Assert.Equal("absolut", m.Single(x => x.Group == "Avstånd").Meaning);
        Assert.Equal("av", m.Single(x => x.Group == "Spindel").Meaning);
        Assert.DoesNotContain(m, x => x.Unusual);
    }

    [Fact]
    public void Modes_CNC_Studio_does_not_expect_are_marked()
    {
        var m = MachineInfo.ParseModes("[GC:G1 G55 G17 G20 G91 G94 M3 M9 T0 F500 S12000]")!;
        Assert.Equal(new[] { "G55", "G20", "G91" }, m.Where(x => x.Unusual).Select(x => x.Code));
        Assert.Equal("500 mm/min (senast satt)", m.Single(x => x.Group == "Matning").Meaning);
        Assert.Null(MachineInfo.ParseModes("ok"));
    }

    [Fact]
    public void Pins_are_on_only_when_reported()
    {
        var pins = MachineInfo.ParsePins("XP");
        Assert.True(pins.Single(p => p.Pin == 'X').On);
        Assert.True(pins.Single(p => p.Pin == 'P').On);
        Assert.False(pins.Single(p => p.Pin == 'Y').On);
        Assert.All(MachineInfo.ParsePins(null), p => Assert.False(p.On));
        Assert.Equal(8, pins.Count);
    }

    [Fact]
    public void Locking_the_motors_is_255_and_unlocking_goes_back()
    {
        Assert.True(MachineInfo.MotorsLocked(new Dictionary<int, double> { [1] = 255 }));
        Assert.False(MachineInfo.MotorsLocked(new Dictionary<int, double> { [1] = 25 }));
        Assert.Equal(100, MachineInfo.UnlockValue(100));
        Assert.Equal(25, MachineInfo.UnlockValue(null));
        Assert.Equal(25, MachineInfo.UnlockValue(255));
    }

    [Fact(Timeout = 60000)]
    public async Task The_simulated_machine_answers_G_with_modes_that_parse()
    {
        var sim = new SimulatedGrbl(speedFactor: 60);
        using var client = new GrblClient(sim);
        await client.ConnectAsync(TimeSpan.FromSeconds(1));
        var r = await client.SendLineAsync("$G");
        var modes = r.Lines.Select(l => MachineInfo.ParseModes(l.Raw)).FirstOrDefault(x => x is not null);
        Assert.NotNull(modes);
        Assert.Contains(modes!, x => x.Group == "Koordinatsystem");
    }
}
