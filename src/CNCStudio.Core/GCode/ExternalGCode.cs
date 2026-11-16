using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.GCode;

/// <summary>What a G-code file from another program contains: its extent, depths, feeds, speeds, tools and time.</summary>
public sealed record GCodeFileInfo(
    int Lines, double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ,
    double? MinFeed, double? MaxFeed, IReadOnlyList<double> SpindleSpeeds, IReadOnlyList<string> Tools,
    bool Inches, bool UsesArcs, bool StartsSpindle, bool Pauses, double EstimatedMinutes);

/// <summary>A G-code file checked for GRBL: the lines to send, what stops it, and what to look at before running it.</summary>
public sealed record ExternalProgram(string Name, IReadOnlyList<string> Lines, IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings, GCodeFileInfo? Info)
{
    public bool Ok => Problems.Count == 0 && Lines.Count > 0;
}

/// <summary>
/// Kör G-code från andra CAM-program: the file is read and checked before it can
/// be loaded. Comments, blank lines and % go; every line is checked for what GRBL 1.1 understands – commands it
/// does not have (M6 tool change, G41/G42 cutter compensation, G81–G89 canned cycles, A/B/C axes, …) stop the file
/// with the line and the reason, and so do settings writes and the other commands macros may not send. The rest
/// is sent as written. The run itself gets the same checks as a project's program: X/Y/Z inside the machine with
/// the zero that is set when Starta is clicked.
/// </summary>
public static class ExternalGCode
{
    /// <summary>GRBL's line buffer holds 80 characters; a longer line is refused by GRBL.</summary>
    public const int MaxLineLength = 78;

    private static readonly HashSet<string> SupportedG = new(StringComparer.Ordinal)
    {
        "0", "1", "2", "3", "4", "10", "17", "18", "19", "20", "21", "28", "28.1", "30", "30.1",
        "38.2", "38.3", "38.4", "38.5", "40", "43.1", "49", "53", "54", "55", "56", "57", "58", "59",
        "61", "80", "90", "91", "91.1", "92", "92.1", "93", "94"
    };

    private static readonly HashSet<string> SupportedM = new(StringComparer.Ordinal) { "0", "1", "2", "3", "4", "5", "7", "8", "9", "30", "56" };

    private static readonly Regex Word = new(@"([A-Z])([-+]?[0-9]*\.?[0-9]*)", RegexOptions.Compiled);

    public static ExternalProgram Analyze(string text, string name, double rapidMmPerMin = 3000)
    {
        var send = new List<string>();
        var problems = new List<string>();
        var warnings = new List<string>();
        var tools = new List<string>();
        var raw = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var toolComment = new Regex(@"\(\s*T\s*(\d+)\b[^)]*\)", RegexOptions.IgnoreCase);
        for (int i = 0; i < raw.Length; i++)
        {
            foreach (Match m in toolComment.Matches(raw[i])) Add(tools, "T" + m.Groups[1].Value);
            string line = ResumePlanner.Strip(raw[i]);
            if (line.Length == 0 || line == "%") continue;
            string where = $"Rad {i + 1} ({Short(line)})";
            if (line.StartsWith("$", StringComparison.Ordinal))
            {
                // The same rules as macros: reading and homing are fine, changing settings is not.
                var check = MacroCode.Prepare(line);
                if (!check.Ok) problems.Add(where + ": " + check.Problems[0].Split(": ", 2).Last());
                else send.Add(line);
                continue;
            }
            if (line.Any(c => c > 126 || c < 32)) { problems.Add($"{where}: tecken som GRBL inte förstår."); continue; }
            if (line.Length > MaxLineLength) { problems.Add($"{where}: raden är {line.Length} tecken – GRBL tar högst {MaxLineLength}."); continue; }
            string? lineProblem = null;
            foreach (Match m in Word.Matches(line))
            {
                string letter = m.Groups[1].Value, value = m.Groups[2].Value;
                if (value.Length == 0) { lineProblem = $"{letter} saknar ett tal."; break; }
                string number = Normalize(value);
                switch (letter)
                {
                    case "G" when !SupportedG.Contains(number):
                        lineProblem = number switch
                        {
                            "41" or "42" => "G41/G42 (radiekompensering) finns inte i GRBL – välj kompensering i datorn (\"in computer\") i CAM-programmet.",
                            "81" or "82" or "83" or "73" or "85" or "86" or "89" => $"G{number} (borrcykel) finns inte i GRBL – välj att borrcykler skrivs ut som vanliga rörelser i CAM-programmet.",
                            "43" => "G43 (verktygslängd från tabell) finns inte i GRBL – bara G43.1.",
                            _ => $"G{number} finns inte i GRBL 1.1."
                        };
                        break;
                    case "M" when number == "6":
                        lineProblem = "M6 (verktygsbyte) finns inte i GRBL. Spara ett program per fräs i CAM-programmet och kör dem efter varandra (nolla Z med varje ny fräs).";
                        break;
                    case "M" when !SupportedM.Contains(number):
                        lineProblem = $"M{number} finns inte i GRBL 1.1.";
                        break;
                    case "A" or "B" or "C" or "U" or "V" or "W":
                        lineProblem = $"{letter}-axeln finns inte på maskinen (bara X, Y och Z).";
                        break;
                    case "O":
                        lineProblem = "programnummer (O) förstår inte GRBL – ta bort raden eller välj en GRBL-postprocessor.";
                        break;
                    case "T":
                        Add(tools, "T" + number);
                        break;
                }
                if (lineProblem is not null) break;
            }
            if (lineProblem is not null) problems.Add($"{where}: {lineProblem}");
            else send.Add(line);
        }
        if (send.Count == 0 && problems.Count == 0) problems.Add("Filen innehåller ingen G-code.");
        if (problems.Count > 0) return new ExternalProgram(name, send, problems, warnings, null);

        var info = Describe(send, tools, rapidMmPerMin);
        if (info.Inches) warnings.Add("Filen är i tum (G20). GRBL räknar om, och kontrollerna gör det också – men kontrollera att det är meningen.");
        if (send.Any(l => l.Contains("G91") && !l.StartsWith("$J", StringComparison.Ordinal)))
            warnings.Add("Filen har relativa rörelser (G91). Utbredningen räknas så gott det går – kör gärna Kontrollkör ($C) och titta på banan.");
        if (info.Tools.Count > 1)
            warnings.Add($"Filen nämner flera verktyg ({string.Join(", ", info.Tools)}) men byter inte verktyg själv. Kontrollera att den är för en fräs.");
        return new ExternalProgram(name, send, problems, warnings, info);
    }

    /// <summary>Extent (with arcs), feeds, speeds and an estimated time from the lines that will be sent.</summary>
    public static GCodeFileInfo Describe(IReadOnlyList<string> lines, IReadOnlyList<string> tools, double rapidMmPerMin)
    {
        var moves = GCodePath.Parse(lines);
        var feeds = new List<double>();
        var speeds = new SortedSet<double>();
        bool inches = false, arcs = false, spindle = false, pauses = false;
        double feed = 0, minutes = 0;
        // Feed per line, to estimate the time of each move (F is modal; in inches it is inches per minute).
        var feedAt = new double[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            foreach (Match m in Word.Matches(lines[i]))
            {
                if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                switch (m.Groups[1].Value)
                {
                    case "G" when v == 20: inches = true; break;
                    case "G" when v is 2 or 3: arcs = true; break;
                    case "F": feed = v; if (v > 0) feeds.Add(v); break;
                    case "S": if (v > 0) speeds.Add(v); break;
                    case "M" when v is 3 or 4: spindle = true; break;
                    case "M" when v is 0 or 1: pauses = true; break;
                }
            }
            feedAt[i] = feed * (inches ? 25.4 : 1);
        }
        foreach (var mv in moves)
        {
            double d = Math.Sqrt(Sq(mv.X1 - mv.X0) + Sq(mv.Y1 - mv.Y0) + Sq(mv.Z1 - mv.Z0));
            double f = mv.Rapid ? rapidMmPerMin : feedAt[mv.LineIndex] > 0 ? feedAt[mv.LineIndex] : rapidMmPerMin;
            minutes += d / f;
        }
        bool any = moves.Count > 0;
        double Min(Func<ProgramMove, double> a, Func<ProgramMove, double> b) => any ? moves.Min(m => Math.Min(a(m), b(m))) : 0;
        double Max(Func<ProgramMove, double> a, Func<ProgramMove, double> b) => any ? moves.Max(m => Math.Max(a(m), b(m))) : 0;
        return new GCodeFileInfo(lines.Count,
            Min(m => m.X0, m => m.X1), Max(m => m.X0, m => m.X1), Min(m => m.Y0, m => m.Y1), Max(m => m.Y0, m => m.Y1),
            Min(m => m.Z0, m => m.Z1), Max(m => m.Z0, m => m.Z1),
            feeds.Count > 0 ? feeds.Min() : null, feeds.Count > 0 ? feeds.Max() : null, speeds.ToList(), tools,
            inches, arcs, spindle, pauses, minutes);
    }

    /// <summary>A plain summary for the load dialog.</summary>
    public static string Summary(GCodeFileInfo i)
    {
        string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            $"• {i.Lines} rader{(i.UsesArcs ? ", med bågar (G2/G3)" : "")}{(i.Inches ? ", i tum" : "")}.",
            $"• Utbredning i arbetskoordinater: X {F(i.MinX)} … {F(i.MaxX)}, Y {F(i.MinY)} … {F(i.MaxY)} mm ({F(i.MaxX - i.MinX)} × {F(i.MaxY - i.MinY)} mm).",
            $"• Z: djupast {F(i.MinZ)} mm, högst {F(i.MaxZ)} mm.",
            i.MinFeed is double a && i.MaxFeed is double b ? $"• Matning {F(a)}{(b > a ? $" … {F(b)}" : "")} {(i.Inches ? "tum" : "mm")}/min." : "• Ingen matning (F) angiven.",
            i.SpindleSpeeds.Count > 0 ? $"• Varvtal: {string.Join(", ", i.SpindleSpeeds.Select(F))} varv/min{(i.StartsSpindle ? "" : " (men ingen M3 – spindeln startas inte av filen)")}." :
                i.StartsSpindle ? "• Spindeln startas (M3) utan varvtal." : "• Filen startar inte spindeln (ingen M3).",
        };
        if (i.Tools.Count > 0) lines.Add($"• Verktyg: {string.Join(", ", i.Tools)}.");
        if (i.Pauses) lines.Add("• Filen pausar (M0/M1) – fortsätt med Fortsätt.");
        lines.Add($"• Ungefärlig tid: {RunTimeEstimator.Format(i.EstimatedMinutes)}.");
        return string.Join("\n", lines);
    }

    private static void Add(List<string> list, string tool) { if (!list.Contains(tool)) list.Add(tool); }
    private static double Sq(double v) => v * v;
    private static string Short(string line) => line.Length > 30 ? line[..30] + "…" : line;

    /// <summary>"01" → "1", "38.20" → "38.2", "17.0" → "17".</summary>
    private static string Normalize(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return value;
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
