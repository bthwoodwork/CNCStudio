namespace CNCStudio.Core.Tests;

public class AppStorageTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "cncstudio-storage-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Without_a_sync_folder_everything_is_local()
    {
        string local = Temp(), docs = Temp();
        var s = new AppStorage(local, docs);
        Assert.Equal(Path.Combine(local, "tools.json"), s.ToolLibraryPath);
        Assert.Equal(docs, s.ProjectsFolder);
    }

    [Fact]
    public void Sync_folder_holds_projects_and_shared_settings_and_is_remembered()
    {
        string local = Temp(), docs = Temp(), drive = Temp();
        var s = new AppStorage(local, docs);
        new ToolLibrary { Tools = { new ToolDefinition { Name = "Min fräs", Diameter = 4, FeedRate = 900, PlungeRate = 200, DepthPerPass = 1, SpindleRpm = 16000 } } }
            .Save(s.ToolLibraryPath);
        s.SaveMachineProfile(new MachineProfile { Name = "Verkstaden", WorkWidthMm = 760 });

        var copied = s.SetSyncFolder(drive);
        Assert.Equal(new[] { "tools.json", "maskinprofil.json" }, copied);
        Assert.Equal(Path.Combine(drive, "Projekt"), s.ProjectsFolder);
        Assert.Equal(Path.Combine(drive, "Inställningar", "tools.json"), s.ToolLibraryPath);
        Assert.Equal("Min fräs", ToolLibrary.Load(s.ToolLibraryPath).Tools.Single().Name);
        Assert.Equal(760, s.LoadMachineProfile()!.WorkWidthMm);

        // Another start (or the other computer, with the same folder chosen) sees the same files.
        var again = new AppStorage(local, docs);
        Assert.Equal(Path.GetFullPath(drive), again.ActiveSyncFolder);
        Assert.Equal("Verkstaden", again.LoadMachineProfile()!.Name);
    }

    [Fact]
    public void Files_already_in_the_sync_folder_are_not_overwritten()
    {
        string drive = Temp();
        var other = new AppStorage(Temp(), Temp());
        other.SetSyncFolder(drive);
        other.SaveMachineProfile(new MachineProfile { Name = "Från andra datorn" });

        var here = new AppStorage(Temp(), Temp());
        here.SaveMachineProfile(new MachineProfile { Name = "Den här datorn" });
        var copied = here.SetSyncFolder(drive);
        Assert.DoesNotContain("maskinprofil.json", copied);
        Assert.Equal("Från andra datorn", here.LoadMachineProfile()!.Name);
    }

    [Fact]
    public void Unreachable_sync_folder_falls_back_to_local_and_says_so()
    {
        string local = Temp(), drive = Temp();
        var s = new AppStorage(local, Temp());
        s.SetSyncFolder(drive);
        Directory.Delete(drive, recursive: true);                         // e.g. Google Drive not running
        var later = new AppStorage(local, Temp());
        Assert.True(later.SyncFolderMissing);
        Assert.Null(later.ActiveSyncFolder);
        Assert.Equal(Path.Combine(local, "tools.json"), later.ToolLibraryPath);
    }

    [Fact]
    public void Stopping_sync_returns_to_local_storage()
    {
        string local = Temp();
        var s = new AppStorage(local, Temp());
        s.SetSyncFolder(Temp());
        s.SetSyncFolder(null);
        Assert.Null(new AppStorage(local, Temp()).Settings.SyncFolder);
    }
}
