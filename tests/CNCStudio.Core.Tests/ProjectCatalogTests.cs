namespace CNCStudio.Core.Tests;

/// <summary>The start view lists the most recently saved projects in the Projekt folder.</summary>
public class ProjectCatalogTests
{
    [Fact]
    public void Lists_projects_newest_first_including_subfolders()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cncstudio-projects-" + Guid.NewGuid().ToString("N"))).FullName;
        var sub = Directory.CreateDirectory(Path.Combine(root, "Skyltar")).FullName;
        void Make(string folder, string name, int minutesAgo)
        {
            var p = Path.Combine(folder, name);
            File.WriteAllText(p, "{}");
            File.SetLastWriteTime(p, DateTime.Now.AddMinutes(-minutesAgo));
        }
        Make(root, "Kapsylöppnare.cncstudio.json", 30);
        Make(sub, "Husnummer.cncstudio.json", 5);
        Make(root, "Gammal.cncstudio.json", 300);
        Make(root, "Kapsylöppnare.nc", 1);                       // not a project

        var list = ProjectCatalog.Recent(root, 10);
        Assert.Equal(new[] { "Husnummer", "Kapsylöppnare", "Gammal" }, list.Select(p => p.Name));
        Assert.Equal("Skyltar", list[0].Subfolder);
        Assert.Null(list[1].Subfolder);
        Assert.Equal(2, ProjectCatalog.Recent(root, 2).Count);
    }

    [Fact]
    public void A_missing_folder_gives_an_empty_list()
    {
        Assert.Empty(ProjectCatalog.Recent(Path.Combine(Path.GetTempPath(), "saknas-" + Guid.NewGuid().ToString("N")), 10));
    }
}
