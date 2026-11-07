using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CNCStudio.Core;

/// <summary>A cutter in the tool library, with its default cutting settings.</summary>
public sealed class ToolDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Ny fräs";
    public ToolKind Kind { get; set; } = ToolKind.FlatEndMill;
    /// <summary>Cutting diameter in mm (for a V-bit: the largest diameter).</summary>
    public double Diameter { get; set; } = 3.175;
    /// <summary>V-bit included angle in degrees (ignored for other kinds).</summary>
    public double AngleDeg { get; set; } = 90;
    public int Flutes { get; set; } = 2;
    public double FeedRate { get; set; } = 1000;
    public double PlungeRate { get; set; } = 300;
    public double DepthPerPass { get; set; } = 2;
    public double SpindleRpm { get; set; } = 18000;
    public string Notes { get; set; } = "";

    /// <summary>Group in the library, e.g. "Raka fräsar", "V-fräsar". Empty = by tool type.</summary>
    public string Category { get; set; } = "";

    /// <summary>Cutting data for this tool in specific materials (overrides the defaults above).</summary>
    public List<MaterialCuttingData> CuttingData { get; set; } = new();

    [JsonIgnore] public string CategoryOrDefault => string.IsNullOrWhiteSpace(Category) ? DefaultCategory(Kind) : Category.Trim();
    [JsonIgnore] public string DisplayName => $"{CategoryOrDefault}: {Name}";

    public static string DefaultCategory(ToolKind kind) => kind switch
    {
        ToolKind.BallNose => "Kulfräsar", ToolKind.VBit => "V-fräsar", _ => "Raka fräsar"
    };

    /// <summary>Cutting data for a material, or the tool's defaults (Material = null) when there is none.</summary>
    public CuttingValues CuttingFor(Guid? materialId)
    {
        var d = materialId is Guid id ? CuttingData.FirstOrDefault(c => c.MaterialId == id) : null;
        return d is null
            ? new CuttingValues(FeedRate, PlungeRate, DepthPerPass, SpindleRpm, null)
            : new CuttingValues(d.FeedRate, d.PlungeRate, d.DepthPerPass, d.SpindleRpm, d.MaterialName);
    }

    public string? Validate()
    {
        foreach (var c in CuttingData ?? new())
            if (c.Validate() is string e) return $"Skärdata för {c.MaterialName}: {e}";
        if (string.IsNullOrWhiteSpace(Name)) return "Ange ett namn på fräsen.";
        if (!double.IsFinite(Diameter) || Diameter <= 0 || Diameter > 100) return "Diametern måste vara större än 0 och högst 100 mm.";
        if (Kind == ToolKind.VBit && (!double.IsFinite(AngleDeg) || AngleDeg <= 0 || AngleDeg >= 180)) return "V-fräsens vinkel måste vara mellan 0 och 180 grader.";
        if (Flutes < 1 || Flutes > 12) return "Antal skär måste vara 1–12.";
        if (!double.IsFinite(FeedRate) || FeedRate < 1 || FeedRate > 20000) return "Matningen måste vara 1–20000 mm/min.";
        if (!double.IsFinite(PlungeRate) || PlungeRate < 1 || PlungeRate > 20000) return "Nedmatningen måste vara 1–20000 mm/min.";
        if (!double.IsFinite(DepthPerPass) || DepthPerPass <= 0 || DepthPerPass > 50) return "Djup per pass måste vara större än 0 och högst 50 mm.";
        if (!double.IsFinite(SpindleRpm) || SpindleRpm < 0 || SpindleRpm > 60000) return "Varvtalet måste vara 0–60000 varv/min.";
        return null;
    }

    public ToolDefinition Clone()
    {
        var c = (ToolDefinition)MemberwiseClone();
        c.CuttingData = (CuttingData ?? new()).Select(d => d with { }).ToList();
        return c;
    }

    /// <summary>Copies the tool's geometry and its cutting data for the material (or the defaults) into a CAM operation.</summary>
    public void ApplyTo(CamOperation op, Guid? materialId = null)
    {
        var v = CuttingFor(materialId);
        op.ToolName = Name; op.ToolKind = Kind; op.ToolDiameter = Diameter;
        op.FeedRate = v.FeedRate; op.PlungeRate = v.PlungeRate; op.DepthPerPass = v.DepthPerPass; op.SpindleRpm = v.SpindleRpm;
    }
}

/// <summary>Feed, plunge, depth per pass and speed; Material is the material they are for (null = tool defaults).</summary>
public sealed record CuttingValues(double FeedRate, double PlungeRate, double DepthPerPass, double SpindleRpm, string? Material);

/// <summary>Cutting data for one tool in one material.</summary>
public sealed record MaterialCuttingData
{
    public Guid MaterialId { get; init; }
    /// <summary>Kept for readability of the file and in case the material is removed.</summary>
    public string MaterialName { get; init; } = "";
    public double FeedRate { get; init; }
    public double PlungeRate { get; init; }
    public double DepthPerPass { get; init; }
    public double SpindleRpm { get; init; }

    public string? Validate()
    {
        if (!double.IsFinite(FeedRate) || FeedRate < 1 || FeedRate > 20000) return "matningen måste vara 1–20000 mm/min.";
        if (!double.IsFinite(PlungeRate) || PlungeRate < 1 || PlungeRate > 20000) return "nedmatningen måste vara 1–20000 mm/min.";
        if (!double.IsFinite(DepthPerPass) || DepthPerPass <= 0 || DepthPerPass > 50) return "djup per pass måste vara större än 0 och högst 50 mm.";
        if (!double.IsFinite(SpindleRpm) || SpindleRpm < 0 || SpindleRpm > 60000) return "varvtalet måste vara 0–60000 varv/min.";
        return null;
    }
}

/// <summary>
/// The user's tool library. Shared by all projects (like a toolbox) and stored as JSON in the
/// user's application data folder.
/// </summary>
public sealed class ToolLibrary
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public List<ToolDefinition> Tools { get; set; } = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CNCStudio", "tools.json");

    /// <summary>Starting tools. Settings are cautious starting values for wood – adjust for your machine.</summary>
    public static ToolLibrary Defaults() => new()
    {
        Tools =
        {
            new ToolDefinition { Name = "1/8\" rak fräs, 2 skär", Diameter = 3.175, FeedRate = 1000, PlungeRate = 300, DepthPerPass = 1.5, SpindleRpm = 18000 },
            new ToolDefinition { Name = "1/4\" rak fräs, 2 skär", Diameter = 6.35, FeedRate = 1500, PlungeRate = 400, DepthPerPass = 3, SpindleRpm = 18000 },
            new ToolDefinition { Name = "6 mm rak fräs, 2 skär", Diameter = 6, FeedRate = 1500, PlungeRate = 400, DepthPerPass = 3, SpindleRpm = 18000 },
            new ToolDefinition { Name = "90° V-fräs", Kind = ToolKind.VBit, Diameter = 12.7, AngleDeg = 90, FeedRate = 1200, PlungeRate = 300, DepthPerPass = 3, SpindleRpm = 18000 },
        }
    };

    public static ToolLibrary Load(string path)
    {
        if (!File.Exists(path)) return Defaults();
        var lib = JsonSerializer.Deserialize<ToolLibrary>(File.ReadAllText(path), Options) ?? Defaults();
        lib.Tools ??= new();
        lib.Tools.RemoveAll(t => t is null);
        foreach (var t in lib.Tools) t.CuttingData ??= new();
        lib.Tools.RemoveAll(t => t.Validate() is not null);
        return lib;
    }

    public void Save(string path)
    {
        var bad = Tools.FirstOrDefault(t => t.Validate() is not null);
        if (bad is not null) throw new InvalidDataException($"{bad.Name}: {bad.Validate()}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true); // never leave a half-written library
    }
}
