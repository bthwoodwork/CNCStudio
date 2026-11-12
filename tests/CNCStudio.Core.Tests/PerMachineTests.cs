using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

/// <summary>0.9.5: $$ copies per machine, and the main window's remembered size and place.</summary>
public class PerMachineTests
{
    private static string Temp() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cncstudio-permachine-" + Guid.NewGuid().ToString("N"))).FullName;
    private static readonly Dictionary<int, double> Settings = new() { [100] = 40, [130] = 500 };

    private static MachineProfile Real(string id, string name) => new() { Id = id, Name = name };
    private static MachineProfile Demo() => new() { Id = "demo", Name = MachineLibrary.DemoName, Port = MachineLibrary.DemoPort };

    [Fact]
    public void A_copy_says_which_machine_it_came_from()
    {
        var root = Temp();
        var m = Real("abc", "Verkstaden");
        var path = GrblBackup.Save(GrblBackup.MachineFolder(root, m), Settings, new DateTime(2026, 11, 12, 10, 0, 0), "PC", null, "Test", m);
        Assert.Equal(Path.Combine(root, "Verkstaden"), Path.GetDirectoryName(path));
        var back = GrblBackup.Read(path)!;
        Assert.Equal(("abc", "Verkstaden"), (back.MachineId, back.MachineName));
        Assert.False(GrblBackup.IsUnassigned(back));
    }

    [Fact]
    public void Each_machine_sees_its_own_copies_and_old_ones_go_where_they_belong()
    {
        var root = Temp();
        MachineProfile a = Real("a", "X-Carve"), b = Real("b", "Shapeoko"), demo = Demo();
        var t = new DateTime(2026, 11, 12, 10, 0, 0);
        GrblBackup.Save(GrblBackup.MachineFolder(root, a), Settings, t, "PC", null, "A", a);
        GrblBackup.Save(GrblBackup.MachineFolder(root, b), Settings, t.AddMinutes(1), "PC", null, "B", b);
        GrblBackup.Save(GrblBackup.MachineFolder(root, demo), Settings, t.AddMinutes(2), "PC", null, "Demo", demo);
        GrblBackup.Save(root, Settings, t.AddMinutes(3), "PC", null, "Gammal riktig", null);                                   // before 0.9.5
        GrblBackup.Save(Path.Combine(root, MachineLibrary.DemoName), Settings, t.AddMinutes(4), "PC", null, "Gammal demo", null); // 0.9.4 demo copies

        var all = GrblBackup.List(new[] { root }.Concat(Directory.GetDirectories(root)));
        Assert.Equal(5, all.Count);
        string[] Reasons(MachineProfile m) => GrblBackup.ForMachine(all, root, m).Select(f => f.Reason!).OrderBy(r => r).ToArray();
        Assert.Equal(new[] { "A", "Gammal riktig" }, Reasons(a));
        Assert.Equal(new[] { "B", "Gammal riktig" }, Reasons(b));
        Assert.Equal(new[] { "Demo", "Gammal demo" }, Reasons(demo));
    }

    [Fact]
    public void Renaming_a_machine_keeps_its_copies()
    {
        var root = Temp();
        var m = Real("a", "Gamla namnet");
        GrblBackup.Save(GrblBackup.MachineFolder(root, m), Settings, DateTime.Now, "PC", null, "Före", m);
        m.Name = "Nya namnet";
        var all = GrblBackup.List(new[] { root }.Concat(Directory.GetDirectories(root)));
        Assert.Single(GrblBackup.ForMachine(all, root, m));
    }

    [Fact]
    public void A_machine_name_that_is_not_a_valid_folder_name_still_gets_a_folder()
    {
        var folder = Path.GetFileName(GrblBackup.MachineFolder("root", Real("a", "X/Carve: 750?")));
        Assert.DoesNotContain('/', folder); Assert.DoesNotContain(':', folder); Assert.DoesNotContain('?', folder);
        Assert.Equal("Maskin", Path.GetFileName(GrblBackup.MachineFolder("root", Real("a", "  "))));
        Assert.Equal(MachineLibrary.DemoName, Path.GetFileName(GrblBackup.MachineFolder("root", Demo())));
    }

    [Fact]
    public void The_window_comes_back_where_it_was()
    {
        var p = new WindowPlacement { Left = 100, Top = 50, Width = 1400, Height = 900, Maximized = true };
        var fit = p.FitTo(0, 0, 1920, 1080, 1060, 650)!;
        Assert.Equal((100.0, 50.0, 1400.0, 900.0, true), (fit.Left, fit.Top, fit.Width, fit.Height, fit.Maximized));
    }

    [Fact]
    public void A_window_left_on_a_monitor_that_is_gone_starts_in_the_default_place()
    {
        var p = new WindowPlacement { Left = 2100, Top = 100, Width = 1400, Height = 900 };   // was on a second monitor
        Assert.Null(p.FitTo(0, 0, 1920, 1080, 1060, 650));
        Assert.Null(new WindowPlacement { Left = 100, Top = -500, Width = 1400, Height = 900 }.FitTo(0, 0, 1920, 1080, 1060, 650));
        Assert.Null(new WindowPlacement().FitTo(0, 0, 1920, 1080, 1060, 650));                 // nothing saved
    }

    [Fact]
    public void The_size_is_kept_within_the_minimum_and_the_screen()
    {
        var small = new WindowPlacement { Left = 0, Top = 0, Width = 300, Height = 200 }.FitTo(0, 0, 1920, 1080, 1060, 650)!;
        Assert.Equal((1060.0, 650.0), (small.Width, small.Height));
        var big = new WindowPlacement { Left = 0, Top = 0, Width = 4000, Height = 3000 }.FitTo(0, 0, 1920, 1080, 1060, 650)!;
        Assert.Equal((1920.0, 1080.0), (big.Width, big.Height));
    }

    [Fact]
    public void The_window_placement_is_remembered_and_survives_choosing_a_sync_folder()
    {
        string local = Temp(), docs = Temp(), drive = Temp();
        var s = new AppStorage(local, docs);
        s.Settings.MainWindow = new WindowPlacement { Left = 10, Top = 20, Width = 1200, Height = 800 };
        s.SaveSettings();
        s.SetSyncFolder(drive);
        var again = new AppStorage(local, docs);
        Assert.Equal(1200, again.Settings.MainWindow!.Width);
        Assert.Equal(drive, again.Settings.SyncFolder);
    }
}
