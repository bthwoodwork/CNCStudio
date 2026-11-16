using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CNCStudio.Core;

/// <summary>A saved set of CAM settings with a name, e.g. "V-carve 60°, 3 mm" – used on a shape with one click.</summary>
public sealed class CamTemplate
{
    public string Name { get; set; } = "";
    /// <summary>All the settings (the shape it is used on replaces ShapeId).</summary>
    public CamOperation Operation { get; set; } = new();
    /// <summary>Cut through the material: the depth becomes the material's thickness when the template is used.</summary>
    public bool ThroughMaterial { get; set; }

    /// <summary>The settings for a shape and a material: a copy, with the depth through the material if asked.</summary>
    public CamOperation For(Guid shapeId, double materialThickness)
    {
        var op = Operation.CopyFor(shapeId);
        if (ThroughMaterial && materialThickness > 0) op.TotalDepth = materialThickness;
        if (op.TotalDepth > materialThickness && materialThickness > 0) op.TotalDepth = materialThickness;
        return op;
    }

    public string Summary
    {
        get
        {
            var o = Operation;
            string kind = o.Kind switch
            {
                CamOperationKind.Outside => "utvändig kontur", CamOperationKind.Inside => "invändig kontur", CamOperationKind.Pocket => "ficka",
                CamOperationKind.Drill => "borrning", CamOperationKind.Face => "planfräsning", CamOperationKind.VCarve => "V-carving",
                CamOperationKind.VCarvePlug => "inlay-hane", CamOperationKind.Carve3D => "3D-fräsning", _ => o.Kind.ToString()
            };
            string depth = ThroughMaterial ? "genom materialet" : $"{o.TotalDepth:0.##} mm djupt";
            return $"{kind}, {o.ToolName ?? $"Ø {o.ToolDiameter:0.###} mm"}, {depth}, {o.DepthPerPass:0.##} mm/pass, {o.FeedRate:0} mm/min";
        }
    }
}

/// <summary>The CAM templates (Inställningar\cam-mallar.json, synced like the tool library).</summary>
public sealed class CamTemplateLibrary
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public List<CamTemplate> Templates { get; set; } = new();

    /// <summary>Examples to start from; change, replace or remove them.</summary>
    public static CamTemplateLibrary Defaults() => new()
    {
        Templates =
        {
            new CamTemplate { Name = "Utskärning 6 mm, 4 flikar", ThroughMaterial = true, Operation = new CamOperation
                { Kind = CamOperationKind.Outside, ToolDiameter = 6, TotalDepth = 18, DepthPerPass = 3, UseTabs = true, TabCount = 4, TabWidth = 6, TabHeight = 3, FeedRate = 1200, PlungeRate = 300 } },
            new CamTemplate { Name = "V-carve 60°, 3 mm", Operation = new CamOperation
                { Kind = CamOperationKind.VCarve, ToolKind = ToolKind.VBit, ToolDiameter = 12.7, ToolAngleDeg = 60, TotalDepth = 3, DepthPerPass = 1.5, UseTabs = false, FeedRate = 1000, PlungeRate = 300 } },
            new CamTemplate { Name = "Ficka 6 mm, 40 %", Operation = new CamOperation
                { Kind = CamOperationKind.Pocket, ToolDiameter = 6, TotalDepth = 5, DepthPerPass = 2.5, UseTabs = false, StepoverPercent = 40, FeedRate = 1200, PlungeRate = 300 } },
        }
    };

    public static CamTemplateLibrary Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Defaults();
            var lib = JsonSerializer.Deserialize<CamTemplateLibrary>(File.ReadAllText(path), Json) ?? new CamTemplateLibrary();
            lib.Templates ??= new();
            lib.Templates.RemoveAll(t => t is null || t.Operation is null || string.IsNullOrWhiteSpace(t.Name));
            return lib;
        }
        catch (Exception) { return Defaults(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AppStorage.WriteAtomic(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Adds the settings under a name; a template with the same name (case ignored) is replaced.</summary>
    public CamTemplate Put(string name, CamOperation op, bool throughMaterial)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Ge mallen ett namn.");
        var copy = op.CopyFor(Guid.Empty);
        copy.Parent = null;
        var template = new CamTemplate { Name = name, Operation = copy, ThroughMaterial = throughMaterial };
        int at = Templates.FindIndex(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) Templates[at] = template; else Templates.Add(template);
        return template;
    }
}
