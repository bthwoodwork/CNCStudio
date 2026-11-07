namespace CNCStudio.Core.Tests;

public class AppStorageTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "cncstudio-storage-" + Guid.NewGuid().ToString("N"));

    private static ToolLibrary OneTool(string name) => new()
    {
        Tools = { new ToolDefinition { Name = name, Diameter = 4, FeedRate = 900, PlungeRate = 200, DepthPerPass = 1, SpindleRpm = 16000 } }
    };

    [Fact]
    public void Without_a_sync_folder_the_data_folder_has_projekt_verktyg_material_and_installningar()
    {
        string local = Temp(), docs = Temp();
        var s = new AppStorage(local, docs);
        s.EnsureFolders();
        Assert.Equal(Path.Combine(docs, "Projekt"), s.ProjectsFolder);
        Assert.Equal(Path.Combine(docs, "Verktyg", "verktyg.json"), s.ToolLibraryPath);
        Assert.Equal(Path.Combine(docs, "Material", "material.json"), s.MaterialLibraryPath);
        Assert.Equal(Path.Combine(docs, "Inställningar", "maskinprofil.json"), s.MachineProfilePath);
        foreach (var sub in new[] { "Projekt", "Verktyg", "Material", "Inställningar" }) Assert.True(Directory.Exists(Path.Combine(docs, sub)));
    }

    [Fact]
    public void Sync_folder_gets_the_libraries_and_profile_and_is_remembered()
    {
        string local = Temp(), docs = Temp(), drive = Temp();
        var s = new AppStorage(local, docs);
        OneTool("Min fräs").Save(s.ToolLibraryPath);
        new MaterialLibrary { Materials = { new MaterialDefinition { Name = "Valnöt" } } }.Save(s.MaterialLibraryPath);
        s.SaveMachineProfile(new MachineProfile { Name = "Verkstaden", WorkWidthMm = 760 });

        var copied = s.SetSyncFolder(drive);
        Assert.Equal(3, copied.Count);
        Assert.Equal(Path.Combine(drive, "Projekt"), s.ProjectsFolder);
        Assert.Equal("Min fräs", ToolLibrary.Load(s.ToolLibraryPath).Tools.Single().Name);
        Assert.Equal("Valnöt", MaterialLibrary.Load(s.MaterialLibraryPath).Materials.Single().Name);

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
        here.SetSyncFolder(drive);
        Assert.Equal("Från andra datorn", here.LoadMachineProfile()!.Name);
    }

    [Fact]
    public void Old_tool_library_and_profile_are_moved_into_the_new_folders()
    {
        string local = Temp(), docs = Temp();
        Directory.CreateDirectory(local);
        OneTool("Gammal fräs").Save(Path.Combine(local, "tools.json"));                   // 0.8.3 and earlier
        File.WriteAllText(Path.Combine(local, "maskinprofil.json"), "{\"Name\":\"Gammal profil\"}");
        var s = new AppStorage(local, docs);
        Assert.Equal("Gammal fräs", ToolLibrary.Load(s.ToolLibraryPath).Tools.Single().Name);
        Assert.Equal("Gammal profil", s.LoadMachineProfile()!.Name);
    }

    [Fact]
    public void Unreachable_sync_folder_falls_back_to_local_and_says_so()
    {
        string local = Temp(), docs = Temp(), drive = Temp();
        var s = new AppStorage(local, docs);
        s.SetSyncFolder(drive);
        Directory.Delete(drive, recursive: true);                         // e.g. Google Drive not running
        var later = new AppStorage(local, docs);
        Assert.True(later.SyncFolderMissing);
        Assert.Equal(Path.Combine(docs, "Verktyg", "verktyg.json"), later.ToolLibraryPath);
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

public class MaterialCuttingDataTests
{
    [Fact]
    public void Tool_uses_material_cutting_data_and_falls_back_to_its_defaults()
    {
        var ek = new MaterialDefinition { Name = "Ek" };
        var mdf = new MaterialDefinition { Name = "MDF", Category = "Skivmaterial" };
        var tool = new ToolDefinition { Name = "6 mm rak", Diameter = 6, FeedRate = 1500, PlungeRate = 400, DepthPerPass = 3, SpindleRpm = 18000 };
        tool.CuttingData.Add(new MaterialCuttingData { MaterialId = ek.Id, MaterialName = "Ek", FeedRate = 900, PlungeRate = 250, DepthPerPass = 1.5, SpindleRpm = 16000 });

        var forEk = tool.CuttingFor(ek.Id);
        Assert.Equal((900.0, 250.0, 1.5, 16000.0, "Ek"), (forEk.FeedRate, forEk.PlungeRate, forEk.DepthPerPass, forEk.SpindleRpm, forEk.Material));
        var forMdf = tool.CuttingFor(mdf.Id);
        Assert.Equal((1500.0, null as string), (forMdf.FeedRate, forMdf.Material));   // no data for MDF: defaults

        var op = new CamOperation();
        tool.ApplyTo(op, ek.Id);
        Assert.Equal((6.0, 900.0, 1.5), (op.ToolDiameter, op.FeedRate, op.DepthPerPass));
    }

    [Fact]
    public void Cutting_data_and_categories_survive_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), "cncstudio-tools-" + Guid.NewGuid().ToString("N") + ".json");
        var id = Guid.NewGuid();
        var lib = new ToolLibrary { Tools = { new ToolDefinition { Name = "Kul 3 mm", Kind = ToolKind.BallNose, Category = "Finbearbetning",
            CuttingData = { new MaterialCuttingData { MaterialId = id, MaterialName = "Furu", FeedRate = 1200, PlungeRate = 300, DepthPerPass = 1, SpindleRpm = 18000 } } } } };
        lib.Save(path);
        var t = ToolLibrary.Load(path).Tools.Single();
        Assert.Equal("Finbearbetning", t.CategoryOrDefault);
        Assert.Equal(1200, t.CuttingFor(id).FeedRate);
        Assert.Equal("Kulfräsar", new ToolDefinition { Kind = ToolKind.BallNose }.CategoryOrDefault);
    }

    [Fact]
    public void Invalid_cutting_data_is_refused()
    {
        var tool = new ToolDefinition();
        tool.CuttingData.Add(new MaterialCuttingData { MaterialName = "Ek", FeedRate = 0, PlungeRate = 100, DepthPerPass = 1, SpindleRpm = 1000 });
        Assert.Contains("Ek", tool.Validate());
    }

    [Fact]
    public void Material_names_must_be_unique()
    {
        var lib = new MaterialLibrary { Materials = { new MaterialDefinition { Name = "Ek" }, new MaterialDefinition { Name = "ek" } } };
        Assert.Throws<InvalidDataException>(() => lib.Save(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json")));
    }
}
