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

    // ---- Identity and purchase (only for the user's own bookkeeping; nothing in CAM depends on them) ----

    /// <summary>The user's own number for the tool, e.g. the number on its box or holder (T-numbers are never written to G-code). Null = none.</summary>
    public int? ToolNumber { get; set; }
    /// <summary>Where the tool was bought ("Inköpt från"), e.g. a shop or manufacturer.</summary>
    public string Vendor { get; set; } = "";
    /// <summary>The seller's article number.</summary>
    public string ArticleNumber { get; set; } = "";
    /// <summary>Link to the product page.</summary>
    public string ProductLink { get; set; } = "";
    /// <summary>What the cutter is made of, e.g. "Hårdmetall" or "HSS".</summary>
    public string CutterMaterial { get; set; } = "";

    // ---- Geometry beyond the cutting diameter, as in Fusion's tool library (mm; null = not measured) ----

    /// <summary>Shank diameter – what the collet grips.</summary>
    public double? ShankDiameter { get; set; }
    /// <summary>Overall length of the tool.</summary>
    public double? OverallLength { get; set; }
    /// <summary>Length of the cutting edges (flute length) – the deepest a straight wall can be cut.</summary>
    public double? FluteLength { get; set; }
    /// <summary>How far the tool sticks out below the collet (length below holder).</summary>
    public double? StickoutLength { get; set; }
    /// <summary>V-bit: diameter of the flat tip (0 = sharp point).</summary>
    public double? TipDiameter { get; set; }

    /// <summary>Cutting data for this tool in specific materials (overrides the defaults above).</summary>
    public List<MaterialCuttingData> CuttingData { get; set; } = new();

    [JsonIgnore] public string CategoryOrDefault => string.IsNullOrWhiteSpace(Category) ? DefaultCategory(Kind) : Category.Trim();
    [JsonIgnore] public string DisplayName => ToolNumber is int n ? $"{CategoryOrDefault}: T{n} {Name}" : $"{CategoryOrDefault}: {Name}";
    /// <summary>The name in the library list: "T3  6 mm rak fräs" when the tool has a number.</summary>
    [JsonIgnore] public string ListName => ToolNumber is int n ? $"T{n}  {Name}" : Name;

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
        if (ToolNumber is int number && (number < 0 || number > 9999)) return "Verktygsnumret måste vara 0–9999.";
        if (Length(ShankDiameter, "Skaftdiametern", 100) is string e1) return e1;
        if (Length(OverallLength, "Totala längden", 500) is string e2) return e2;
        if (Length(FluteLength, "Skärlängden", 500) is string e3) return e3;
        if (Length(StickoutLength, "Utsticket", 500) is string e4) return e4;
        if (OverallLength is double total)
        {
            if (FluteLength > total) return "Skärlängden kan inte vara längre än fräsens totala längd.";
            if (StickoutLength > total) return "Utsticket kan inte vara längre än fräsens totala längd.";
        }
        if (TipDiameter is double tip && (!double.IsFinite(tip) || tip < 0 || tip >= Diameter)) return "Spetsdiametern måste vara 0 eller mer och mindre än diametern.";
        return null;
    }

    /// <summary>Something that looks wrong but may be right, shown in the library (never stops saving). Null = nothing.</summary>
    [JsonIgnore] public string? Warning =>
        FluteLength is double flute && StickoutLength is double stickout && flute > stickout + 1e-9
            ? "Skärlängden är längre än utsticket – en del av skären sitter i spännhylsan. Kontrollera måtten."
            : null;

    private static string? Length(double? value, string what, double max) =>
        value is double v && (!double.IsFinite(v) || v <= 0 || v > max) ? $"{what} måste vara större än 0 och högst {max} mm (eller tomt)." : null;

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

    /// <summary>
    /// The categories in the order they are shown, including empty ones the user has made. A category a
    /// tool uses but that is not in the list (old files, or the default by tool type) is shown after these.
    /// </summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>Every category: the listed ones in their order, then the others the tools use, alphabetically.</summary>
    public IReadOnlyList<string> AllCategories()
    {
        var listed = (Categories ?? new()).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var used = Tools.Select(t => t.CategoryOrDefault)
            .Where(c => !listed.Contains(c, StringComparer.CurrentCultureIgnoreCase))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase);
        return listed.Concat(used).ToList();
    }

    /// <summary>The position of a category in <see cref="AllCategories"/> (for sorting the list).</summary>
    public int CategoryOrder(string category)
    {
        var all = AllCategories();
        for (int i = 0; i < all.Count; i++) if (string.Equals(all[i], category, StringComparison.CurrentCultureIgnoreCase)) return i;
        return all.Count;
    }

    /// <summary>Adds an empty category last. Returns an error text, or null.</summary>
    public string? AddCategory(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return "Ange ett namn på kategorin.";
        if (AllCategories().Contains(name, StringComparer.CurrentCultureIgnoreCase)) return $"Kategorin \"{name}\" finns redan.";
        Categories = AllCategories().Append(name).ToList();
        return null;
    }

    /// <summary>Renames a category; every tool in it follows. Returns an error text, or null.</summary>
    public string? RenameCategory(string oldName, string newName)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) return "Ange ett namn på kategorin.";
        var all = AllCategories().ToList();
        int i = all.FindIndex(c => string.Equals(c, oldName, StringComparison.CurrentCultureIgnoreCase));
        if (i < 0) return $"Kategorin \"{oldName}\" finns inte.";
        if (all.Where((c, j) => j != i).Contains(newName, StringComparer.CurrentCultureIgnoreCase)) return $"Kategorin \"{newName}\" finns redan.";
        foreach (var t in Tools.Where(t => string.Equals(t.CategoryOrDefault, all[i], StringComparison.CurrentCultureIgnoreCase))) t.Category = newName;
        all[i] = newName;
        Categories = all;
        return null;
    }

    /// <summary>
    /// Removes a category. Its tools move to <paramref name="moveTo"/> (which is created if needed); a
    /// category with tools cannot be removed without somewhere to move them. Returns an error text, or null.
    /// </summary>
    public string? DeleteCategory(string name, string? moveTo)
    {
        var all = AllCategories().ToList();
        int i = all.FindIndex(c => string.Equals(c, name, StringComparison.CurrentCultureIgnoreCase));
        if (i < 0) return $"Kategorin \"{name}\" finns inte.";
        var tools = Tools.Where(t => string.Equals(t.CategoryOrDefault, all[i], StringComparison.CurrentCultureIgnoreCase)).ToList();
        moveTo = moveTo?.Trim();
        if (tools.Count > 0)
        {
            if (string.IsNullOrEmpty(moveTo)) return $"Kategorin har {tools.Count} fräs(ar) – välj vilken kategori de ska flyttas till.";
            if (string.Equals(moveTo, all[i], StringComparison.CurrentCultureIgnoreCase)) return "Välj en annan kategori att flytta fräsarna till.";
            foreach (var t in tools) t.Category = moveTo;
        }
        all.RemoveAt(i);
        if (!string.IsNullOrEmpty(moveTo) && !all.Contains(moveTo, StringComparer.CurrentCultureIgnoreCase)) all.Add(moveTo);
        Categories = all;
        // A tool type's default category comes back by itself while a tool of that type has no category.
        return null;
    }

    /// <summary>Tool numbers used by more than one tool, with the tools' names.</summary>
    public IReadOnlyList<(int Number, IReadOnlyList<string> Tools)> DuplicateToolNumbers() =>
        Tools.Where(t => t.ToolNumber is not null).GroupBy(t => t.ToolNumber!.Value).Where(g => g.Count() > 1)
            .OrderBy(g => g.Key).Select(g => (g.Key, (IReadOnlyList<string>)g.Select(t => t.Name).ToList())).ToList();

    /// <summary>The lowest tool number no tool has (starting at 1).</summary>
    public int NextFreeToolNumber()
    {
        var used = Tools.Where(t => t.ToolNumber is not null).Select(t => t.ToolNumber!.Value).ToHashSet();
        int n = 1;
        while (used.Contains(n)) n++;
        return n;
    }

    /// <summary>A deep copy (for editing in a window without touching the library in use).</summary>
    public ToolLibrary Clone() => new() { Tools = Tools.Select(t => t.Clone()).ToList(), Categories = (Categories ?? new()).ToList() };

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
        lib.Categories ??= new();
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
