using System.Text.Json;

namespace CNCStudio.Core.Tests;

/// <summary>One profile per machine; the demo machine is a machine of its own.</summary>
public class MachineLibraryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cncstudio-machines-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    private void WriteLegacy(MachineProfile p)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "maskinprofil.json"), JsonSerializer.Serialize(p));
    }

    [Fact]
    public void The_old_single_profile_becomes_the_first_machine_and_a_demo_machine_is_added()
    {
        WriteLegacy(new MachineProfile { WorkWidthMm = 500, WorkLengthMm = 500, ZTravelMm = 100 });
        var lib = new MachineLibrary(_folder);
        var all = lib.All();
        Assert.Equal(2, all.Count);
        Assert.Equal(MachineLibrary.DefaultName, all[0].Name);       // the old default name is replaced
        Assert.Equal(500, all[0].WorkWidthMm);
        Assert.Equal(MachineLibrary.DemoName, all[1].Name);          // demo last
        Assert.Equal(MachineLibrary.DemoPort, all[1].Port);
        Assert.Equal(all[0].Id, lib.Active.Id);
        Assert.Equal(2, new MachineLibrary(_folder).All().Count);    // created once, not again
    }

    [Fact]
    public void The_demo_machine_has_its_own_profile_that_is_kept_apart()
    {
        var lib = new MachineLibrary(_folder);
        var demo = lib.ForPort(MachineLibrary.DemoPort)!;
        demo.WorkWidthMm = 300; demo.WorkLengthMm = 300;
        lib.Save(demo);

        var again = new MachineLibrary(_folder);                     // "restart"
        Assert.Equal(300, again.ForPort(MachineLibrary.DemoPort)!.WorkWidthMm);
        Assert.Equal(750, again.Active.WorkWidthMm);                 // the real machine is untouched
    }

    [Fact]
    public void A_port_picks_the_machine_last_connected_there()
    {
        var lib = new MachineLibrary(_folder);
        Assert.Null(lib.ForPort("COM3"));
        var real = lib.Active; real.Port = "COM3"; lib.Save(real);
        var second = lib.CreateNew(lib.UnusedName("Ny maskin"), real);
        second.Port = "COM7"; lib.Save(second);
        Assert.Equal(real.Id, lib.ForPort("COM3")!.Id);
        Assert.Equal("Ny maskin", lib.ForPort("COM7")!.Name);
        Assert.Equal("Ny maskin 2", lib.UnusedName("Ny maskin"));
    }

    [Fact]
    public void A_renamed_machine_stays_one_machine()
    {
        var lib = new MachineLibrary(_folder);
        var p = lib.Active; p.Name = "X-Carve"; lib.Save(p);
        var all = lib.All();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, m => m.Name == "X-Carve");
    }

    [Fact]
    public void The_demo_machine_and_the_last_real_machine_cannot_be_deleted()
    {
        var lib = new MachineLibrary(_folder);
        Assert.NotNull(lib.Delete(lib.ForPort(MachineLibrary.DemoPort)!.Id!));
        Assert.NotNull(lib.Delete(lib.Active.Id!));
        var second = lib.CreateNew("Andra", lib.Active);
        lib.SetActive(second.Id!);
        Assert.Null(lib.Delete(second.Id!));
        Assert.Equal(MachineLibrary.DefaultName, lib.Active.Name);   // active falls back to a remaining machine
    }

    [Fact]
    public void Saving_a_profile_from_an_old_project_goes_to_the_active_machine()
    {
        var storage = new AppStorage(Path.Combine(_folder, "local"), Path.Combine(_folder, "data"));
        var fromOldProject = new MachineProfile { Name = "Gammal", WorkWidthMm = 600 };   // no Id
        storage.SaveMachineProfile(fromOldProject);
        var all = storage.Machines.All();
        Assert.Equal(2, all.Count);
        Assert.Equal(600, storage.Machines.Active.WorkWidthMm);
    }
}
