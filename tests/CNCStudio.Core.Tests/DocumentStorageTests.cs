using CNCStudio.Core;

namespace CNCStudio.Core.Tests;

public class DocumentStorageTests
{
    [Fact]
    public void Project_saved_before_tab_settings_loads_with_defaults()
    {
        var shapeId = Guid.NewGuid();
        var json = $$"""
        {
          "Name": "Gammalt projekt",
          "MaterialWidth": 600, "MaterialHeight": 600, "MaterialThickness": 18,
          "Shapes": [ { "Id": "{{shapeId}}", "Kind": "Rectangle", "X": 10, "Y": 10, "Width": 50, "Height": 40 } ],
          "CamOperations": [ { "ShapeId": "{{shapeId}}", "Kind": "Outside", "ToolDiameter": 3.175,
                               "TotalDepth": 6, "DepthPerPass": 2, "UseTabs": true } ]
        }
        """;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var op = Assert.Single(DocumentStorage.Load(path).CamOperations);
            Assert.Equal(4, op.TabCount);
            Assert.Equal(6, op.TabWidth);
            Assert.Equal(3, op.TabHeight);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Tab_settings_survive_save_and_load()
    {
        var doc = new CncDocument();
        var shape = new DesignShape { Kind = ShapeKind.Rectangle, X = 10, Y = 10, Width = 50, Height = 40 };
        doc.Shapes.Add(shape);
        doc.CamOperations.Add(new CamOperation { ShapeId = shape.Id, TotalDepth = 18, DepthPerPass = 3,
            TabCount = 6, TabWidth = 8.5, TabHeight = 2.5 });
        var path = Path.GetTempFileName();
        try
        {
            DocumentStorage.Save(path, doc);
            var op = Assert.Single(DocumentStorage.Load(path).CamOperations);
            Assert.Equal((6, 8.5, 2.5), (op.TabCount, op.TabWidth, op.TabHeight));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Machine_profile_from_older_project_gets_new_defaults()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
            { "Name": "x", "MaterialWidth": 600, "MaterialHeight": 600, "MaterialThickness": 18,
              "Machine": { "Name": "X-Carve", "WorkWidthMm": 750, "WorkLengthMm": 750, "CalibratedToGrbl": false } }
            """);
            var m = DocumentStorage.Load(path).Machine;
            Assert.Equal("X-Carve", m.Name);
            Assert.True(m.HomeOnStartup);
            Assert.Equal(65, m.ZTravelMm);
            Assert.Equal(3, m.LimitMarginMm);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0, 1, 3)]
    [InlineData(65, -1, 3)]
    [InlineData(65, 1, -1)]
    public void Invalid_machine_limits_are_rejected(double zTravel, double pullOff, double margin)
    {
        var doc = new CncDocument();
        doc.Machine.ZTravelMm = zTravel; doc.Machine.HomingPullOffMm = pullOff; doc.Machine.LimitMarginMm = margin;
        Assert.NotNull(MachineSetupValidation.Validate(doc));
    }

    [Fact]
    public void Example_project_still_loads()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CNCStudio.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var doc = DocumentStorage.Load(Path.Combine(dir!, "Examples", "projekt.cncstudio.json"));
        Assert.NotNull(doc);
    }
}
