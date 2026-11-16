using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CNCStudio.Core.Grbl;

/// <summary>One active mode from $G, with what it means.</summary>
public sealed record ModalState(string Group, string Code, string Meaning, bool Unusual);

/// <summary>One input from the status report's Pn: field.</summary>
public sealed record PinState(char Pin, string Name, bool On);

/// <summary>
/// Maskininformation: the active modes from $G ("[GC:G0 G54 G17 G21 G90
/// G94 M5 M9 T0 F0 S0]") in plain words, and the inputs from Pn: (X Y Z limit switches, P probe, D door,
/// H hold, R soft reset, S cycle start). "Unusual" marks a mode CNC Studio's programs do not expect – inches,
/// relative, another work coordinate system, inverse-time feed, another plane.
/// </summary>
public static class MachineInfo
{
    public static IReadOnlyList<ModalState>? ParseModes(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith("[GC:", StringComparison.Ordinal) || !t.EndsWith("]", StringComparison.Ordinal)) return null;
        var words = t[4..^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<ModalState>();
        foreach (var w in words)
        {
            var m = Describe(w);
            if (m is not null) list.Add(m);
        }
        return list;
    }

    private static ModalState? Describe(string w) => w switch
    {
        "G0" => new("Rörelse", w, "snabbförflyttning (senaste rörelsekod)", false),
        "G1" => new("Rörelse", w, "matning (senaste rörelsekod)", false),
        "G2" => new("Rörelse", w, "cirkelbåge medurs (senaste rörelsekod)", false),
        "G3" => new("Rörelse", w, "cirkelbåge moturs (senaste rörelsekod)", false),
        "G38.2" or "G38.3" or "G38.4" or "G38.5" => new("Rörelse", w, "probning (senaste rörelsekod)", false),
        "G80" => new("Rörelse", w, "ingen rörelse vald", false),
        "G54" => new("Koordinatsystem", w, "G54 – det CNC Studios program använder", false),
        "G55" or "G56" or "G57" or "G58" or "G59" => new("Koordinatsystem", w, $"{w} – CNC Studios program använder G54", true),
        "G17" => new("Plan", w, "XY-planet", false),
        "G18" => new("Plan", w, "XZ-planet", true),
        "G19" => new("Plan", w, "YZ-planet", true),
        "G21" => new("Enheter", w, "millimeter", false),
        "G20" => new("Enheter", w, "TUM – CNC Studio räknar i mm", true),
        "G90" => new("Avstånd", w, "absolut", false),
        "G91" => new("Avstånd", w, "relativt (inkrementellt)", true),
        "G94" => new("Matningsläge", w, "mm per minut", false),
        "G93" => new("Matningsläge", w, "inverterad tid", true),
        "M3" => new("Spindel", w, "på, medurs", false),
        "M4" => new("Spindel", w, "på, moturs", false),
        "M5" => new("Spindel", w, "av", false),
        "M7" => new("Kylning", w, "dimma på", false),
        "M8" => new("Kylning", w, "flöde på", false),
        "M9" => new("Kylning", w, "av", false),
        "M0" or "M1" or "M2" or "M30" => new("Programläge", w, w is "M0" or "M1" ? "pausat" : "programslut", false),
        _ when w.StartsWith('T') => new("Verktyg", w, "verktygsnummer (CNC Studio använder inte T)", false),
        _ when w.StartsWith('F') => new("Matning", w, $"{w[1..]} mm/min (senast satt)", false),
        _ when w.StartsWith('S') => new("Varvtal", w, $"{w[1..]} varv/min (senast satt)", false),
        _ => new("Övrigt", w, "", false)
    };

    private static readonly (char Pin, string Name)[] Pins =
    {
        ('X', "X-brytare"), ('Y', "Y-brytare"), ('Z', "Z-brytare"), ('P', "Prob"), ('D', "Dörr"),
        ('H', "Håll (feed hold)"), ('R', "Återställning"), ('S', "Cykelstart")
    };

    /// <summary>Every input with on/off from the Pn: field (null = none active; GRBL only sends Pn: when one is).</summary>
    public static IReadOnlyList<PinState> ParsePins(string? pn) =>
        Pins.Select(p => new PinState(p.Pin, p.Name, pn?.IndexOf(p.Pin) >= 0)).ToList();

    /// <summary>$1 = 255 keeps the stepper motors powered at all times (GRBL: step idle delay; 255 = always on).</summary>
    public const int LockedIdleDelay = 255;
    public const int DefaultIdleDelay = 25;

    public static bool MotorsLocked(IReadOnlyDictionary<int, double> settings) =>
        settings.TryGetValue(1, out var v) && (int)Math.Round(v) == LockedIdleDelay;

    /// <summary>The $1 to go back to when unlocking: the value from before locking, or GRBL's default 25 ms.</summary>
    public static int UnlockValue(int? before) => before is int b && b != LockedIdleDelay && b >= 0 && b < 255 ? b : DefaultIdleDelay;
}
