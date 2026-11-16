using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CNCStudio.Core.Grbl;

/// <summary>A place the machine goes to with one click (fräsbyte, parkering …), in machine coordinates.</summary>
public sealed class SavedLocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Machine coordinates (MPos, after homing): 0 at the switches, negative into the travel.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Null: Z stays up near the switch (the usual for a tool change or parking).</summary>
    public double? Z { get; set; }
}

/// <summary>
/// Sparade platser: per machine, in Inställningar\Maskiner\Platser\&lt;id&gt;.json beside its
/// profile – machine coordinates belong to the machine, not to a project. Going there always lifts Z first in
/// machine coordinates (G53, as Gå till X0 Y0), then X/Y, then Z if the place has one. Needs homing: machine
/// coordinates mean nothing before it.
/// </summary>
public sealed class SavedLocations
{
    public const string FolderName = "Platser";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<SavedLocation> Locations { get; set; } = new();

    public static string PathFor(string machinesFolder, string machineId) => Path.Combine(machinesFolder, FolderName, machineId + ".json");

    public static SavedLocations Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new SavedLocations();
            var l = JsonSerializer.Deserialize<SavedLocations>(File.ReadAllText(path), Json) ?? new SavedLocations();
            l.Locations ??= new();
            l.Locations.RemoveAll(x => x is null);
            return l;
        }
        catch (Exception) { return new SavedLocations(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AppStorage.WriteAtomic(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Is the place inside the machine's travel (minus the margin to the switches)? Null when it is.</summary>
    public static string? Check(SavedLocation p, MachineProfile m)
    {
        if (string.IsNullOrWhiteSpace(p.Name)) return "Ge platsen ett namn.";
        double margin = m.LimitMarginMm;
        string? Axis(string a, double v, double travel) =>
            !double.IsFinite(v) ? $"{a} är inget tal." :
            v > -margin + 1e-9 || v < -(travel - margin) - 1e-9
                ? $"{a} {N(v)} ligger utanför maskinens rörelse ({N(-(travel - margin))} … {N(-margin)} i maskinkoordinater)." : null;
        return Axis("X", p.X, m.WorkWidthMm) ?? Axis("Y", p.Y, m.WorkLengthMm) ?? (p.Z is double z ? Axis("Z", z, m.ZTravelMm) : null);
    }

    /// <summary>The way there: Z up, across, then down to the place's Z if it has one – all in machine coordinates.</summary>
    public static IReadOnlyList<string> GoTo(SavedLocation p, MachineProfile m)
    {
        if (Check(p, m) is string problem) throw new ArgumentException(problem);
        var lines = new List<string> { $"G53 G0 Z{N(-m.LimitMarginMm)}", $"G53 G0 X{N(p.X)} Y{N(p.Y)}" };
        if (p.Z is double z && Math.Abs(z + m.LimitMarginMm) > 1e-9) lines.Add($"G53 G0 Z{N(z)}");
        return lines;
    }

    /// <summary>
    /// Two places to start from: a tool change at the front, in the middle (easy to reach), and parking at the back
    /// (out of the way when loading material).
    /// </summary>
    public static IReadOnlyList<SavedLocation> Suggestions(MachineProfile m)
    {
        // GRBL's machine space is −travel … 0 with 0 at the + end (the back for Y, +Y away from the operator),
        // whichever corner the switches are in: a switch at the − end sets MPos to −travel when homing.
        double margin = m.LimitMarginMm + 2;
        double front = -(m.WorkLengthMm - margin);
        double back = -margin;
        double middle = -m.WorkWidthMm / 2;
        return new[]
        {
            new SavedLocation { Name = "Fräsbyte", X = Math.Round(middle, 1), Y = Math.Round(front, 1) },
            new SavedLocation { Name = "Parkering", X = Math.Round(middle, 1), Y = Math.Round(back, 1) },
        };
    }

    private static string N(double v)
    {
        var r = Math.Round(v, 3, MidpointRounding.AwayFromZero);
        if (r == 0) r = 0;
        return r.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
