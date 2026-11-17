using System;
using System.Linq;
using System.Xml.Linq;
using CNCStudio.Core;
using Xunit;

public class DrawingExportTests
{
    private static CncDocument Doc()
    {
        var d = new CncDocument { MaterialWidth = 200, MaterialHeight = 100 };
        var rect = new DesignShape { Kind = ShapeKind.Rectangle, X = 10, Y = 20, Width = 50, Height = 30 };
        var hole = new DesignShape { Kind = ShapeKind.Ellipse, X = 100, Y = 40, Width = 20, Height = 20 };
        var free = new DesignShape { Kind = ShapeKind.Ellipse, X = 150, Y = 10, Width = 30, Height = 10 };
        d.Shapes.Add(rect); d.Shapes.Add(hole); d.Shapes.Add(free);
        d.CamOperations.Add(new CamOperation { ShapeId = rect.Id, Kind = CamOperationKind.Outside });
        d.CamOperations.Add(new CamOperation { ShapeId = hole.Id, Kind = CamOperationKind.Pocket });
        return d;
    }

    [Fact]
    public void Shapes_go_in_one_layer_per_machining_kind_with_the_material_first()
    {
        var layers = DrawingExport.Layers(Doc());
        Assert.Equal(new[] { "Material", "Utskärning utvändig", "Ficka", "Ingen bearbetning" }, layers.Select(l => l.Name));
        Assert.Single(layers[2].Circles);                       // a round ellipse stays a circle
        Assert.Equal(144, layers[3].Lines[0].Points.Count);     // an oval becomes a polyline
    }

    [Fact]
    public void Svg_is_in_millimetres_with_y_down_and_a_group_per_layer()
    {
        var x = XDocument.Parse(DrawingExport.Svg(Doc()));
        XNamespace n = "http://www.w3.org/2000/svg";
        Assert.Equal("200mm", (string?)x.Root!.Attribute("width"));
        Assert.Equal("0 0 200 100", (string?)x.Root.Attribute("viewBox"));
        Assert.Equal(4, x.Root.Elements(n + "g").Count());
        var rect = x.Root.Elements(n + "g").ElementAt(1).Element(n + "polygon")!;
        Assert.Equal("10,20 60,20 60,50 10,50", (string?)rect.Attribute("points"));
    }

    [Fact]
    public void Dxf_has_mm_units_y_up_from_the_front_edge_and_ascii_layer_names()
    {
        var dxf = DrawingExport.Dxf(Doc(), includeMaterial: false);
        var lines = dxf.Split('\n');
        int u = Array.IndexOf(lines, "$INSUNITS");
        Assert.Equal("4", lines[u + 2]);
        Assert.Contains("UTSKARNING_UTVANDIG", lines);
        Assert.DoesNotContain("MATERIAL", lines);
        int c = Array.IndexOf(lines, "CIRCLE");
        Assert.Equal("110", lines[Array.IndexOf(lines, "10", c) + 1]);
        Assert.Equal("50", lines[Array.IndexOf(lines, "20", c) + 1]);   // 100 − 50
        Assert.Equal("EOF", lines[^2]);
    }

    [Fact]
    public void Only_the_chosen_shapes_are_exported()
    {
        var d = Doc();
        var layers = DrawingExport.Layers(d, new[] { d.Shapes[1] }, includeMaterial: false);
        Assert.Equal("Ficka", Assert.Single(layers).Name);
    }
}
