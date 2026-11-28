using System.Text.Json;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;
using Xunit;

namespace CNCStudio.Core.Tests;

public class JogPresetSaveTests
{
    [Fact]
    public void NoSavedPresets_GivesTheDefaults()
    {
        Assert.Equal(GrblMachineCommands.DefaultPresets, GrblMachineCommands.PresetsFrom(null));
    }

    [Fact]
    public void SavedPreset_IsUsed_NameStaysTheDefault()
    {
        var saved = new JogPreset?[] { null, new("x", 3, 1.5, 2500), null };
        var p = GrblMachineCommands.PresetsFrom(saved);
        Assert.Equal(new JogPreset("Normal", 3, 1.5, 2500), p[1]);
        Assert.Equal(GrblMachineCommands.DefaultPresets[0], p[0]);
        Assert.Equal(GrblMachineCommands.DefaultPresets[2], p[2]);
    }

    [Theory]
    [InlineData(0, 1, 1000)]
    [InlineData(1, -1, 1000)]
    [InlineData(1, 1, double.NaN)]
    public void DamagedPreset_FallsBackToTheDefault(double xy, double z, double feed)
    {
        var p = GrblMachineCommands.PresetsFrom(new JogPreset?[] { new("Precis", xy, z, feed) });
        Assert.Equal(GrblMachineCommands.DefaultPresets[0], p[0]);
        Assert.Equal(3, p.Count);
    }

    [Fact]
    public void Presets_SurviveTheSettingsFile()
    {
        var settings = new AppSettings { JogPresets = new() { null, new JogPreset("Normal", 4, 1, 1800), null }, JogPresetIndex = 1 };
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(1, back.JogPresetIndex);
        Assert.Equal(1800, GrblMachineCommands.PresetsFrom(back.JogPresets)[1].FeedRate);
    }
}
