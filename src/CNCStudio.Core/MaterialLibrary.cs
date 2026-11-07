using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CNCStudio.Core;

/// <summary>A material in the material library. Tools hold their cutting data per material.</summary>
public sealed class MaterialDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Nytt material";
    /// <summary>Group, e.g. "Trä", "Skivmaterial", "Plast", "Metall".</summary>
    public string Category { get; set; } = "Trä";
    public string Notes { get; set; } = "";

    [JsonIgnore] public string DisplayName => $"{Category}: {Name}";

    public string? Validate() => string.IsNullOrWhiteSpace(Name) ? "Ange ett namn på materialet." : null;
    public MaterialDefinition Clone() => (MaterialDefinition)MemberwiseClone();
}

/// <summary>The user's materials, shared by all projects (stored in the Material folder).</summary>
public sealed class MaterialLibrary
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public List<MaterialDefinition> Materials { get; set; } = new();

    /// <summary>Common starting materials – without cutting data; add that per tool in the tool library.</summary>
    public static MaterialLibrary Defaults() => new()
    {
        Materials =
        {
            new MaterialDefinition { Name = "Furu", Category = "Trä" },
            new MaterialDefinition { Name = "Ek", Category = "Trä" },
            new MaterialDefinition { Name = "Björkplywood", Category = "Skivmaterial" },
            new MaterialDefinition { Name = "MDF", Category = "Skivmaterial" },
            new MaterialDefinition { Name = "Akryl (plexiglas)", Category = "Plast" },
        }
    };

    public static MaterialLibrary Load(string path)
    {
        if (!File.Exists(path)) return Defaults();
        var lib = JsonSerializer.Deserialize<MaterialLibrary>(File.ReadAllText(path), Options) ?? Defaults();
        lib.Materials ??= new();
        lib.Materials.RemoveAll(m => m is null || m.Validate() is not null);
        return lib;
    }

    public void Save(string path)
    {
        var bad = Materials.FirstOrDefault(m => m.Validate() is not null);
        if (bad is not null) throw new InvalidDataException(bad.Validate());
        if (Materials.GroupBy(m => m.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidDataException("Två material har samma namn.");
        AppStorage.WriteAtomic(path, JsonSerializer.Serialize(this, Options));
    }

    public MaterialDefinition? Find(Guid? id) => id is Guid g ? Materials.FirstOrDefault(m => m.Id == g) : null;
}
