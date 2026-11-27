namespace CNCStudio.Core.Tests;

public class MaterialLibraryDefaultsTests
{
    [Fact]
    public void Default_materials_keep_their_ids_between_starts()
    {
        var first = MaterialLibrary.Defaults();
        var second = MaterialLibrary.Defaults();
        Assert.Equal(first.Materials.Select(m => m.Id), second.Materials.Select(m => m.Id));
        Assert.Equal(first.Materials.Count, first.Materials.Select(m => m.Id).Distinct().Count());
        // A project that chose "Ek" before a restart still finds it, also when the library file does not exist.
        var oak = first.Materials.Single(m => m.Name == "Ek");
        Assert.Same(second.Materials.Single(m => m.Name == "Ek").Name, second.Find(oak.Id)!.Name);
        var missing = Path.Combine(Path.GetTempPath(), "cncstudio-no-such-" + Guid.NewGuid() + ".json");
        Assert.NotNull(MaterialLibrary.Load(missing).Find(oak.Id));
    }
}
