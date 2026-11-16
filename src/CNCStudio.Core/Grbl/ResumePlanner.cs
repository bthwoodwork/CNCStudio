using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CNCStudio.Core.Grbl;

/// <summary>Where and how an interrupted job continues.</summary>
public sealed record ResumePlan(int StartIndex, IReadOnlyList<string> Preamble, bool FromBeginning,
    double X, double Y, double Z, string Description);

/// <summary>
/// Finds a safe place to continue an interrupted program and the lines that get the machine there.
/// <para>
/// Verified (GRBL 1.1, docs/GRBL-verifierat.md): "ok" is sent when a line is parsed into the planner,
/// not when it has been cut; the planner holds BLOCK_BUFFER_SIZE = 16 blocks. So up to 16 lines before
/// the last "ok" may never have run. We therefore go back further than that, to the last rapid XY
/// move made at travel height (above the stock), and re-run from there – cutting air again is harmless.
/// </para>
/// <para>
/// The way back in, in this order: modal setup (G21 G90 G94 G17 G54) → lift Z near the top in machine
/// coordinates (G53 G0, as "Gå till X0 Y0") → rapid to the XY start → start the spindle (manual: M0
/// pause; GRBL: M3 S + G4) → down to the travel height with the feed rate restored (F on a G0 line sets
/// the modal feed in G94, gcode.c) → continue with the next program line. G54 is stored in EEPROM and
/// survives a power cut; G92 does not and is never used by CNC Studio.
/// </para>
/// </summary>
public static class ResumePlanner
{
    public const int PlannerBlocks = 16;
    private static readonly Regex Word = new(@"([A-Z])([-+]?\d*\.?\d+)", RegexOptions.Compiled);
    private static readonly Regex RpmComment = new(@"(\d+)\s*varv/min", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private enum Kind { Travel, Plunge, Cut }

    private sealed record Candidate(int Index, Kind Kind, double X, double Y, double Z, double Feed,
        double Rpm, double PlungeFeed, string Code, bool Inches, int Wcs)
    {
        /// <summary>The first program line that runs when continuing here.</summary>
        public int Start => Kind == Kind.Plunge ? Index : Index + 1;
    }

    public static ResumePlan Plan(IReadOnlyList<string> lines, int ackedLines, MachineProfile machine, MotionSettings motion)
    {
        // Lines up to here have certainly been executed: at most PlannerBlocks lines can sit in the
        // planner after the last "ok", plus the one being executed.
        int limit = ackedLines - (PlannerBlocks + 1);
        var plan = Build(lines, c => c.Index <= limit, machine, motion);
        if (plan.Pick is not Candidate pick)
            return Beginning("Jobbet hann inte längre än början – det körs om från början.");
        return plan.Result with
        {
            Description = $"Fortsätter från rad {pick.Index + 1} av {lines.Count}: {plan.Where}, " +
                $"{ackedLines - pick.Index} rader före sista bekräftade raden (GRBL kan ha upp till {PlannerBlocks} rader i kö som inte hunnit köras). " +
                "Den biten fräses en gång till."
        };
    }

    /// <summary>
    /// Starta från rad: start the program at line <paramref name="lineIndex"/> (0-based) –
    /// or, since a line in the middle of a cut has no safe way in from above, at the nearest safe point before it
    /// (a rapid at travel height, the start of a depth pass, or down through an already cut slot). The machine is
    /// brought there the same way as when resuming: modes, Z up in machine coordinates, XY, spindle, down.
    /// </summary>
    public static ResumePlan PlanFromLine(IReadOnlyList<string> lines, int lineIndex, MachineProfile machine, MotionSettings motion)
    {
        if (lineIndex <= 0) return Beginning("Programmet startar från början.");
        var plan = Build(lines, c => c.Start <= lineIndex, machine, motion);
        if (plan.Pick is not Candidate pick)
            return Beginning($"Det finns ingen säker punkt att börja på före rad {lineIndex + 1} – programmet startar från början.");
        int skipped = lineIndex - plan.Result.StartIndex;
        return plan.Result with
        {
            Description = $"Startar från rad {plan.Result.StartIndex + 1} av {lines.Count}" +
                (skipped > 0 ? $" – {skipped} rader före rad {lineIndex + 1}, närmaste säkra punkt" : "") + $": {plan.Where}."
        };
    }

    private static ResumePlan Beginning(string why) =>
        new(0, Array.Empty<string>(), true, double.NaN, double.NaN, double.NaN, why);

    private static (ResumePlan Result, Candidate? Pick, string Where) Build(IReadOnlyList<string> lines, Func<Candidate, bool> allowed,
        MachineProfile machine, MotionSettings motion)
    {
        double x = double.NaN, y = double.NaN, z = double.NaN, feed = double.NaN, rpm = double.NaN, plungeFeed = double.NaN;
        bool absolute = true, rapid = true, spindleStarted = false, inches = false;
        int wcs = 54;
        var candidates = new List<Candidate>();
        double clearanceMm = Math.Max(motion.PlungeClearanceMm, 0.5);

        for (int i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var comment = RpmComment.Match(raw);
            if (comment.Success && raw.TrimStart().StartsWith("(")) rpm = double.Parse(comment.Groups[1].Value, CultureInfo.InvariantCulture);
            var code = Strip(raw);
            if (code.Length == 0) continue;
            double feedBefore = feed, rpmBefore = rpm, plungeBefore = plungeFeed;     // state before this line
            double? nx = null, ny = null, nz = null, lineFeed = null;
            bool isM0 = false, isM3 = false;
            foreach (Match m in Word.Matches(code))
            {
                double v = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                switch (m.Groups[1].Value[0])
                {
                    case 'G' when v == 0: rapid = true; break;
                    case 'G' when v == 1: rapid = false; break;
                    case 'G' when v == 90: absolute = true; break;
                    case 'G' when v == 91: absolute = false; break;
                    case 'G' when v == 20: inches = true; break;
                    case 'G' when v == 21: inches = false; break;
                    case 'G' when v is >= 54 and <= 59 && v == Math.Floor(v): wcs = (int)v; break;
                    case 'X': nx = v; break;
                    case 'Y': ny = v; break;
                    case 'Z': nz = v; break;
                    case 'F': feed = v; lineFeed = v; break;
                    case 'S': rpm = v; break;
                    case 'M' when v == 0: isM0 = true; break;
                    case 'M' when v is 3 or 4: isM3 = true; break;
                }
            }
            bool xy = nx is not null || ny is not null;
            bool known = absolute && spindleStarted && !double.IsNaN(x) && !double.IsNaN(y) && !double.IsNaN(z);
            double clearance = inches ? clearanceMm / 25.4 : clearanceMm;     // in the program's units
            if (known && rapid && xy && z > 0 && (nz ?? z) > 0)
                // A rapid XY move above the stock: continue after it, at its travel height.
                candidates.Add(new(i, Kind.Travel, nx ?? x, ny ?? y, nz ?? z, feedBefore, rpmBefore, plungeBefore, code, inches, wcs));
            else if (known && !rapid && !xy && nz is double down && down < z && down <= 0)
                // A straight plunge at the start of a depth pass: re-run it from the plunge clearance.
                candidates.Add(new(i, Kind.Plunge, x, y, clearance, feedBefore, rpmBefore, plungeBefore, code, inches, wcs));
            else if (known && !rapid && xy && !double.IsNaN(feedBefore) && z < clearance)
                // A cutting move: its start point is already cut down to z, so plunge there through the
                // cleared slot and continue along the path.
                candidates.Add(new(i, Kind.Cut, x, y, z, feedBefore, rpmBefore, plungeBefore, code, inches, wcs));
            if (!rapid && !xy && nz is not null && lineFeed is double pf) plungeFeed = pf;
            if (isM0 || isM3) spindleStarted = true;
            if (absolute) { x = nx ?? x; y = ny ?? y; z = nz ?? z; }
        }

        var pick = candidates.LastOrDefault(allowed);
        if (pick is null) return (Beginning(""), null, "");

        // G53 Z first, in mm; then the program's own units and coordinate system for the way in.
        var pre = new List<string> { "G21", "G90", "G94", "G17", $"G{pick.Wcs}", $"G53 G0 Z{N(-machine.LimitMarginMm)}" };
        if (pick.Inches) pre.Add("G20");
        pre.Add($"G0 X{N(pick.X)} Y{N(pick.Y)}");
        if (machine.Spindle == SpindleControl.Manual)
        {
            pre.Add(double.IsNaN(pick.Rpm)
                ? "(ATERUPPTA: starta frasen for hand, fortsatt med ~)"
                : $"(ATERUPPTA: starta frasen for hand, {N(pick.Rpm, 0)} varv/min, fortsatt med ~)");
            pre.Add("M0");
        }
        else
        {
            if (!double.IsNaN(pick.Rpm)) pre.Add($"M3 S{N(pick.Rpm, 0)}");
            if (motion.SpindleSpinUpSeconds > 0) pre.Add($"G4 P{N(motion.SpindleSpinUpSeconds, 2)}");
        }
        int start;
        string where;
        switch (pick.Kind)
        {
            case Kind.Travel:
                pre.Add($"G0 Z{N(pick.Z)}" + (double.IsNaN(pick.Feed) ? "" : $" F{N(pick.Feed, 0)}"));
                start = pick.Index + 1;
                where = "en förflyttning på säker höjd";
                break;
            case Kind.Plunge:
                pre.Add($"G0 Z{N(pick.Z)}" + (double.IsNaN(pick.Feed) ? "" : $" F{N(pick.Feed, 0)}"));
                start = pick.Index;
                where = "början av ett djuppass (nedmatningen görs om)";
                break;
            default:
                double plunge = double.IsNaN(pick.PlungeFeed) ? pick.Feed : Math.Min(pick.PlungeFeed, pick.Feed);
                pre.Add($"G0 Z{N(pick.Inches ? clearanceMm / 25.4 : clearanceMm)}");
                pre.Add($"G1 Z{N(pick.Z)} F{N(plunge, 0)}");                    // down through the cut slot
                // The cutting line itself, with its feed restored (F is modal and was set to the plunge feed).
                pre.Add(pick.Code.Contains('F') ? pick.Code : $"{pick.Code}F{N(pick.Feed, 0)}");
                start = pick.Index + 1;
                where = "mitt i en bana – fräsen matas ned i det redan frästa spåret";
                break;
        }
        return (new ResumePlan(start, pre, false, pick.X, pick.Y, pick.Z, ""), pick, where);
    }
    /// <summary>Removes ( ) and ; comments and spaces, upper-case – what GRBL itself does before parsing.</summary>
    public static string Strip(string line)
    {
        var sb = new System.Text.StringBuilder();
        int depth = 0;
        foreach (var ch in line)
        {
            if (ch == ';' && depth == 0) break;
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { if (depth > 0) depth--; continue; }
            if (depth == 0 && !char.IsWhiteSpace(ch)) sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

    private static string N(double v, int decimals = 3)
    {
        var r = Math.Round(v, decimals, MidpointRounding.AwayFromZero);
        if (r == 0) r = 0;
        return r.ToString("0." + new string('#', Math.Max(1, decimals)), CultureInfo.InvariantCulture).TrimEnd('.');
    }
}
