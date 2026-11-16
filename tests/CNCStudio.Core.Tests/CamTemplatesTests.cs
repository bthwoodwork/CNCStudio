using CNCStudio.Core;
using Xunit;

public class CamTemplatesTests
{
    [Fact]
    public void A_template_is_saved_loaded_and_replaced_by_name()
    {
        var path = Path.Combine(Path.GetTempPath(), "cncstudio-mallar-" + Guid.NewGuid().ToString("N"), "cam-mallar.json");
        try
        {
            var lib = new CamTemplateLibrary();
            lib.Put("Ficka 3 mm", new CamOperation { Kind = CamOperationKind.Pocket, ToolDiameter = 3, TotalDepth = 4, ShapeId = Guid.NewGuid() }, false);
            lib.Put("ficka 3 MM", new CamOperation { Kind = CamOperationKind.Pocket, ToolDiameter = 3, TotalDepth = 6 }, false);
            Assert.Single(lib.Templates);
            Assert.Equal(Guid.Empty, lib.Templates[0].Operation.ShapeId);
            lib.Save(path);

            var back = CamTemplateLibrary.Load(path);
            Assert.Equal(6, back.Templates.Single().Operation.TotalDepth);
            Assert.Equal(CamOperationKind.Pocket, back.Templates[0].Operation.Kind);
        }
        finally { var d = Path.GetDirectoryName(path)!; if (Directory.Exists(d)) Directory.Delete(d, true); }
    }

    [Fact]
    public void Used_on_a_shape_it_gets_the_shape_and_a_through_cut_gets_the_material_thickness()
    {
        var t = CamTemplateLibrary.Defaults().Templates.First(x => x.ThroughMaterial);
        var shape = Guid.NewGuid();
        var op = t.For(shape, 12);
        Assert.Equal(shape, op.ShapeId);
        Assert.Equal(12, op.TotalDepth);
        Assert.NotSame(t.Operation, op);
        Assert.Equal(18, t.Operation.TotalDepth);                  // the template itself is unchanged

        var deep = new CamTemplate { Name = "Djup", Operation = new CamOperation { TotalDepth = 25 } };
        Assert.Equal(12, deep.For(shape, 12).TotalDepth);          // never deeper than the material
    }

    [Fact]
    public void The_examples_are_valid_settings()
    {
        foreach (var t in CamTemplateLibrary.Defaults().Templates)
            Assert.Null(CamValidation.GetError(t.For(Guid.NewGuid(), 18), 18));
    }
}
