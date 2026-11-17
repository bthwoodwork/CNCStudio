using System.IO;
using CNCStudio.Core.Grbl;
using Xunit;

public class MacroTriggerTests
{
    [Fact]
    public void One_macro_per_trigger_and_it_survives_save_and_load()
    {
        var lib = new MacroLibrary();
        var park = new Macro { Name = "Parkera", Code = "G53 G0 Z-5", RunAt = MacroTrigger.JobEnd };
        var other = new Macro { Name = "Annat", Code = "M5", RunAt = MacroTrigger.JobEnd };
        lib.Put(park);
        lib.Put(other);                                   // takes JobEnd from Parkera
        Assert.Same(other, lib.For(MacroTrigger.JobEnd));
        Assert.Equal(MacroTrigger.None, park.RunAt);
        Assert.Null(lib.For(MacroTrigger.JobStart));
        Assert.Null(lib.For(MacroTrigger.None));

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "makron.json");
        lib.Save(path);
        Assert.Contains("\"JobEnd\"", File.ReadAllText(path));
        Assert.Equal("Annat", MacroLibrary.Load(path).For(MacroTrigger.JobEnd)!.Name);
    }
}
