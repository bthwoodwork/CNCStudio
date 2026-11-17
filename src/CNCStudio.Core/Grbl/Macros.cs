using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CNCStudio.Core.Grbl;

/// <summary>
/// When a macro runs by itself around a program: before it starts (also from a line and when resumed), after it
/// has run to the end, or after it was stopped with Stoppa. Not at a pause: in a feed hold GRBL takes no G-code.
/// </summary>
public enum MacroTrigger { None, JobStart, JobEnd, JobStopped }

/// <summary>A user's own piece of G-code, run from the Makron panel (e.g. "Lyft Z till toppen").</summary>
public sealed class Macro
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Nytt makro";
    public string Description { get; set; } = "";
    /// <summary>G-code and GRBL commands, one per line. Comments in ( ) or after ; are allowed.</summary>
    public string Code { get; set; } = "";
    /// <summary>Runs by itself at this point of a program run; at most one macro per trigger.</summary>
    public MacroTrigger RunAt { get; set; }

    public static string TriggerText(MacroTrigger t) => t switch
    {
        MacroTrigger.JobStart => "före varje program",
        MacroTrigger.JobEnd => "när ett program har körts klart",
        MacroTrigger.JobStopped => "efter Stoppa",
        _ => "bara när du kör det"
    };

    public Macro Clone() => (Macro)MemberwiseClone();
}

/// <summary>The macros, shared by all projects and computers (Inställningar\makron.json, synced like the libraries).</summary>
public sealed class MacroLibrary
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public List<Macro> Macros { get; set; } = new();

    /// <summary>Examples to start from; change or remove them.</summary>
    public static MacroLibrary Defaults() => new()
    {
        Macros =
        {
            new Macro { Name = "Lyft Z till toppen", Description = "Z upp till 5 mm under Z-brytaren (i maskinkoordinater). X och Y står still.",
                Code = "G53 G0 Z-5   ; maskinkoordinater: 5 mm under brytaren" },
            new Macro { Name = "Gå till arbetsnollan", Description = "Lyfter Z först, sedan till X0 Y0 i aktivt koordinatsystem.",
                Code = "G53 G0 Z-5   ; lyft först\nG90 G0 X0 Y0" },
            new Macro { Name = "Visa nollpunkter och lägen", Description = "Skriver ut G54–G59, G92, verktygslängd och aktiva lägen.",
                Code = "$#\n$G" },
            new Macro { Name = "Spindel av", Description = "Stänger av spindeln (bara om den styrs av GRBL).", Code = "M5" },
        }
    };

    /// <summary>The macro that runs at <paramref name="trigger"/>, or null.</summary>
    public Macro? For(MacroTrigger trigger) => trigger == MacroTrigger.None ? null : Macros.FirstOrDefault(m => m.RunAt == trigger);

    /// <summary>Puts <paramref name="macro"/> in the library (replacing the one with its id); a trigger it takes is taken from any other.</summary>
    public void Put(Macro macro)
    {
        if (macro.RunAt != MacroTrigger.None)
            foreach (var other in Macros.Where(m => m.Id != macro.Id && m.RunAt == macro.RunAt)) other.RunAt = MacroTrigger.None;
        int i = Macros.FindIndex(x => x.Id == macro.Id);
        if (i >= 0) Macros[i] = macro; else Macros.Add(macro);
    }

    public static MacroLibrary Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Defaults();
            var lib = JsonSerializer.Deserialize<MacroLibrary>(File.ReadAllText(path), Json) ?? new MacroLibrary();
            lib.Macros ??= new();
            lib.Macros.RemoveAll(m => m is null);
            return lib;
        }
        catch (Exception) { return Defaults(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AppStorage.WriteAtomic(path, JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>
/// Checks a macro before it is sent: comments and blank lines go, and what does not belong in a macro is
/// refused with the reason – settings writes ($n=, made from Maskininställningar so a copy is saved first),
/// $RST (restores defaults), startup blocks ($N), sleep ($SLP), real-time characters (they are buttons, not
/// lines) and lines GRBL cannot hold. Everything else is sent as written, one line at a time with send-response.
/// </summary>
public static class MacroCode
{
    /// <summary>The longest line sent (GRBL's line buffer is 80 characters; CNC Studio keeps to 70 as for programs).</summary>
    public const int MaxLineLength = 70;

    public sealed record Prepared(IReadOnlyList<string> Lines, IReadOnlyList<string> Problems)
    {
        public bool Ok => Problems.Count == 0 && Lines.Count > 0;
    }

    private static readonly Regex SettingWrite = new(@"^\$\d+\s*=", RegexOptions.Compiled);
    private static readonly Regex StartupBlock = new(@"^\$N\d*\s*=?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static Prepared Prepare(string code)
    {
        var lines = new List<string>();
        var problems = new List<string>();
        var raw = (code ?? "").Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < raw.Length; i++)
        {
            string line = ResumePlanner.Strip(raw[i]);
            if (line.Length == 0) continue;
            string where = $"Rad {i + 1} ({line})";
            string upper = line.ToUpperInvariant();
            if (line.Any(c => c > 126 || (c < 32 && c != '\t')))
                problems.Add($"{where}: tecken som GRBL inte förstår (bara vanliga ASCII-tecken).");
            else if (line.All(c => c is '!' or '~' or '?' or ' '))
                problems.Add($"{where}: realtidstecken (! ~ ?) skickas med knapparna Paus, Fortsätt och STOPP, inte som rad.");
            else if (SettingWrite.IsMatch(line))
                problems.Add($"{where}: ändrar en inställning. Gör det i Maskininställningar – där sparas en kopia av $$ först.");
            else if (upper.StartsWith("$RST", StringComparison.Ordinal))
                problems.Add($"{where}: $RST återställer GRBL:s inställningar eller nollpunkter – inte i ett makro.");
            else if (StartupBlock.IsMatch(line))
                problems.Add($"{where}: startblock ($N) skrivs till EEPROM och körs vid varje start – inte i ett makro.");
            else if (upper.StartsWith("$SLP", StringComparison.Ordinal))
                problems.Add($"{where}: $SLP (viloläge) kräver återställning efteråt – inte i ett makro.");
            else if (line.Length > MaxLineLength)
                problems.Add($"{where}: raden är {line.Length} tecken – högst {MaxLineLength}.");
            else lines.Add(line);
        }
        if (lines.Count == 0 && problems.Count == 0) problems.Add("Makrot har ingen kod.");
        return new Prepared(lines, problems);
    }

    private static readonly Regex Word = new(@"([A-Z])([-+]?[0-9]*\.?[0-9]+)", RegexOptions.Compiled);

    /// <summary>
    /// True when the lines can move the machine or start the spindle (G0–G3, G28/G30, G38.x probing, M3/M4,
    /// $H, $J=) – then the user is asked before it runs. Reading things ($#, $G) runs at once. Each word is
    /// read on its own, since lines are sent without spaces (G53G0Z-5).
    /// </summary>
    public static bool MovesMachine(IEnumerable<string> preparedLines) => preparedLines.Any(line =>
    {
        if (line.StartsWith("$H", StringComparison.Ordinal) || line.StartsWith("$J", StringComparison.Ordinal)) return true;
        if (line.StartsWith("$", StringComparison.Ordinal)) return false;
        foreach (Match m in Word.Matches(line))
        {
            if (!double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
            if (m.Groups[1].Value == "G" && (v is 0 or 1 or 2 or 3 or 28 or 30 || Math.Floor(v) == 38)) return true;
            if (m.Groups[1].Value == "M" && v is 3 or 4) return true;
        }
        return false;
    });

    /// <summary>Code read from a G-code file: kept as it is (comments too), CRLF made plain.</summary>
    public static string FromFile(string text) => (text ?? "").Replace("\r\n", "\n").TrimEnd('\n', ' ');

    /// <summary>Building blocks for the editor, each with what it does (GRBL 1.1 commands; see docs/GRBL-verifierat.md and the GRBL wiki).</summary>
    public static IReadOnlyList<(string Code, string What)> Snippets { get; } = new[]
    {
        ("G53 G0 Z-5", "Lyft Z i maskinkoordinater (5 mm under Z-brytaren) – säkert före förflyttning"),
        ("G90 G0 X0 Y0", "Snabbt till X0 Y0 i aktivt koordinatsystem (absolut)"),
        ("G91 G0 X10", "Flytta 10 mm i X från där fräsen står (relativt) – följ med G90 efteråt"),
        ("G90", "Absoluta koordinater igen"),
        ("G1 X0 Y0 F1000", "Matning (fräsning) till punkten med 1000 mm/min"),
        ("G10 L20 P0 X0 Y0", "Nolla X och Y där fräsen står (aktivt koordinatsystem)"),
        ("G10 L20 P0 Z0", "Nolla Z där fräsen står"),
        ("$H", "Homing"),
        ("M3 S12000", "Spindel på medurs, 12000 varv/min (bara spindel som styrs av GRBL)"),
        ("M5", "Spindel av"),
        ("G4 P2", "Vänta 2 sekunder"),
        ("M0", "Paus – fortsätt med Fortsätt (~)"),
        ("$#", "Visa nollpunkter (G54–G59, G92, verktygslängd, senaste probning)"),
        ("$G", "Visa aktiva lägen (koordinatsystem, enheter, absolut/relativt …)"),
    };
}
