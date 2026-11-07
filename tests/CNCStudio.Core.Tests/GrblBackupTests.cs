using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

/// <summary>Copies of $$: saved as "$n=value", compared with the machine, restored with read-back.</summary>
[Collection("GrblClientStatics")]
public class GrblBackupTests
{
    private static string Temp() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cncstudio-grbl-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void A_copy_is_saved_and_read_back_with_its_details()
    {
        var folder = Temp();
        var settings = new Dictionary<int, double>(SimulatedGrbl.DefaultSettings);
        var path = GrblBackup.Save(folder, settings, new DateTime(2026, 11, 8, 1, 2, 3), "VERKSTAD", "Grbl 1.1g", "Före kalibrering");
        var text = File.ReadAllText(path);
        Assert.Contains("$100=40.000", text);                      // numbers like GRBL prints them
        Assert.Contains("$23=3", text);                            // masks and switches as integers
        var back = GrblBackup.Read(path)!;
        Assert.Equal(settings.Count, back.Settings.Count);
        Assert.Equal(new DateTime(2026, 11, 8, 1, 2, 3), back.Time);
        Assert.Equal(("VERKSTAD", "Grbl 1.1g", "Före kalibrering"), (back.Computer, back.Firmware, back.Reason));
        Assert.Empty(GrblBackup.Compare(back.Settings, settings));
    }

    [Fact]
    public void Copies_from_the_first_test_are_read_too()
    {
        var folder = Temp();
        File.WriteAllLines(Path.Combine(folder, "$$-backup-20261107-120712.txt"), new[] { "$0=10", "$100=40", "$130=500" });
        var b = Assert.Single(GrblBackup.List(new[] { folder }));
        Assert.Equal(new DateTime(2026, 11, 7, 12, 7, 12), b.Time);
        Assert.Equal(500, b.Settings[130]);
    }

    [Fact]
    public void Differences_and_what_to_write_back()
    {
        var copy = new Dictionary<int, double> { [100] = 40, [130] = 500, [27] = 1 };
        var machine = new Dictionary<int, double> { [100] = 40.5, [130] = 500, [32] = 0 };
        var d = GrblBackup.Compare(copy, machine);
        Assert.Equal(new[] { 27, 32, 100 }, d.Select(x => x.Number));
        Assert.Equal(new[] { (100, 40.0) }, GrblBackup.RestoreChanges(d));   // only settings both sides have
    }

    [Fact]
    public async Task Every_change_is_preceded_by_a_copy_and_can_be_undone()
    {
        var folder = Temp();
        GrblClient.BeforeSettingsWrite = (s, changes) => GrblBackup.Save(folder, s, DateTime.Now, "TEST", null, "Före ändring");
        try
        {
            var sim = new SimulatedGrbl(speedFactor: 60);
            using var c = new GrblClient(sim);
            await c.ConnectAsync(TimeSpan.FromSeconds(1));
            Assert.True((await c.WriteSettingsAsync(new[] { (100, 41.5) })).Success);
            var copy = Assert.Single(GrblBackup.List(new[] { folder }));
            Assert.Equal(40, copy.Settings[100]);                          // the value before the change

            var undo = GrblBackup.RestoreChanges(GrblBackup.Compare(copy.Settings, await c.ReadSettingsAsync()));
            Assert.Equal(new[] { (100, 40.0) }, undo);
            Assert.True((await c.WriteSettingsAsync(undo)).Success);
            Assert.Equal(40, (await c.ReadSettingsAsync())[100]);
            Assert.Equal(2, GrblBackup.List(new[] { folder }).Count);    // the undo made a copy too
        }
        finally { GrblClient.BeforeSettingsWrite = null; }
    }
}

/// <summary>Tests that set GrblClient's static hook must not run in parallel with each other.</summary>
[CollectionDefinition("GrblClientStatics", DisableParallelization = true)]
public class GrblClientStaticsCollection { }
