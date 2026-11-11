using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CNCStudio.Core;

/// <summary>
/// One machine profile per machine (Inställningar\Maskiner\&lt;id&gt;.json) and which one is active
/// (Inställningar\maskiner.json). The demo machine is a machine of its own with its own profile, so it
/// can be set up and tested exactly like a second real machine. The old single profile
/// (Inställningar\maskinprofil.json) becomes the first machine the first time the library is used.
/// </summary>
public sealed class MachineLibrary
{
    public const string DemoPort = "DEMO (simulerad maskin)";
    public const string DemoName = "Demomaskin";
    public const string FolderName = "Maskiner";
    public const string DefaultName = "Min maskin";
    private const string LegacyDefaultName = "Maskinprofil (ej verifierad)";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _settingsFolder;

    /// <param name="settingsFolder">The Inställningar folder.</param>
    public MachineLibrary(string settingsFolder) => _settingsFolder = settingsFolder;

    public string Folder => Path.Combine(_settingsFolder, FolderName);
    private string ActiveFile => Path.Combine(_settingsFolder, "maskiner.json");
    private string LegacyFile => Path.Combine(_settingsFolder, "maskinprofil.json");

    public static bool IsDemo(MachineProfile p) => p.Port == DemoPort;

    /// <summary>All machines: real ones by name, the demo machine last. Creates the first two if there are none.</summary>
    public IReadOnlyList<MachineProfile> All()
    {
        var list = ReadAll();
        if (!list.Any(p => !IsDemo(p)))
        {
            var first = Load(LegacyFile) ?? new MachineProfile();
            if (string.IsNullOrWhiteSpace(first.Name) || first.Name == LegacyDefaultName) first.Name = DefaultName;
            first.Id = null; first.Port = null;
            list.Add(Save(first));
            if (ReadActiveId() is null) SetActive(first.Id!);
        }
        if (!list.Any(IsDemo))
        {
            var demo = Clone(list.First(p => !IsDemo(p)));
            demo.Id = null; demo.Name = DemoName; demo.Port = DemoPort; demo.CalibratedToGrbl = false;
            list.Add(Save(demo));
        }
        return list.OrderBy(p => IsDemo(p)).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The machine new projects get and that Maskininställningar edits when nothing is connected.</summary>
    public MachineProfile Active
    {
        get
        {
            var all = All();
            return all.FirstOrDefault(p => p.Id == ReadActiveId()) ?? all.First(p => !IsDemo(p));
        }
    }

    public MachineProfile? Find(string? id) => id is null ? null : All().FirstOrDefault(p => p.Id == id);

    public void SetActive(string id)
    {
        Directory.CreateDirectory(_settingsFolder);
        AppStorage.WriteAtomic(ActiveFile, JsonSerializer.Serialize(new ActiveMachine { Active = id }, Json));
    }

    /// <summary>
    /// The machine to use when connecting on <paramref name="port"/>: the demo machine for the demo port,
    /// otherwise the machine last connected there. Null = no machine has used that port yet.
    /// </summary>
    public MachineProfile? ForPort(string port)
    {
        var all = All();
        return port == DemoPort ? all.First(IsDemo) : all.FirstOrDefault(p => !IsDemo(p) && p.Port == port);
    }

    /// <summary>Saves the profile under its Id (a new Id if it has none) and returns it.</summary>
    public MachineProfile Save(MachineProfile profile)
    {
        profile.Id ??= Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Folder);
        AppStorage.WriteAtomic(Path.Combine(Folder, profile.Id + ".json"), JsonSerializer.Serialize(profile, Json));
        return profile;
    }

    /// <summary>A new real machine, starting as a copy of <paramref name="template"/> (not yet connected anywhere).</summary>
    public MachineProfile CreateNew(string name, MachineProfile template)
    {
        var p = Clone(template);
        p.Id = null; p.Port = null; p.Name = name; p.CalibratedToGrbl = false;
        return Save(p);
    }

    /// <summary>A name not used by any machine: "Ny maskin", "Ny maskin 2", …</summary>
    public string UnusedName(string baseName)
    {
        var names = All().Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (int i = 2; ; i++) if (!names.Contains($"{baseName} {i}")) return $"{baseName} {i}";
    }

    /// <summary>Deletes a real machine. The demo machine and the last real machine cannot be deleted.</summary>
    public string? Delete(string id)
    {
        var all = All();
        if (all.FirstOrDefault(p => p.Id == id) is not { } p) return "Maskinen finns inte.";
        if (IsDemo(p)) return "Demomaskinen kan inte tas bort.";
        if (all.Count(x => !IsDemo(x)) <= 1) return "Det måste finnas minst en maskin.";
        File.Delete(Path.Combine(Folder, id + ".json"));
        if (ReadActiveId() == id) SetActive(All().First(x => !IsDemo(x)).Id!);
        return null;
    }

    public static MachineProfile Clone(MachineProfile p) =>
        JsonSerializer.Deserialize<MachineProfile>(JsonSerializer.Serialize(p, Json), Json)!;

    private List<MachineProfile> ReadAll()
    {
        var list = new List<MachineProfile>();
        if (!Directory.Exists(Folder)) return list;
        foreach (var file in Directory.GetFiles(Folder, "*.json"))
            if (Load(file) is { } p)
            {
                p.Id = Path.GetFileNameWithoutExtension(file);   // the file name is the Id
                list.Add(p);
            }
        return list;
    }

    private static MachineProfile? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<MachineProfile>(File.ReadAllText(path), Json) : null; }
        catch (Exception) { return null; }   // a damaged file must not stop the program
    }

    private string? ReadActiveId()
    {
        try { return File.Exists(ActiveFile) ? JsonSerializer.Deserialize<ActiveMachine>(File.ReadAllText(ActiveFile), Json)?.Active : null; }
        catch (Exception) { return null; }
    }

    private sealed class ActiveMachine { public string? Active { get; set; } }
}
