using CNCStudio.Core;
using Xunit;

public class AutosaveTests
{
    [Fact]
    public void A_written_copy_comes_back_with_where_it_belongs_and_is_gone_after_clear()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cncstudio-autosave-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = new Autosave(dir);
            Assert.Null(a.TryLoad());
            var doc = new CncDocument { Name = "Skylt" };
            doc.Shapes.Add(new DesignShape { Kind = ShapeKind.Rectangle, X = 5, Y = 5, Width = 40, Height = 20 });
            var when = new DateTime(2026, 11, 16, 14, 30, 0);
            a.Write(doc, @"C:\Projekt\Skylt.cncstudio.json", when);

            var back = a.TryLoad();
            Assert.NotNull(back);
            Assert.Equal("Skylt", back!.Value.Document.Name);
            Assert.Single(back.Value.Document.Shapes);
            Assert.Equal(@"C:\Projekt\Skylt.cncstudio.json", back.Value.Info.OriginalPath);
            Assert.Equal(when, back.Value.Info.SavedAt);

            a.Clear();
            Assert.Null(a.TryLoad());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_broken_copy_is_ignored()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cncstudio-autosave-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = new Autosave(dir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(a.ProjectPath, "{ inte json");
            File.WriteAllText(a.InfoPath, "{}");
            Assert.Null(a.TryLoad());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
