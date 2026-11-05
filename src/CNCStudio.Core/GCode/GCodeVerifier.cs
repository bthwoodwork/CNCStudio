using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CNCStudio.Core.GCode;

/// <summary>
/// Reads G-code back line by line, independently of the generator, and checks it against the
/// verified GRBL 1.1 rules and CNC Studio's safety rules. Any issue blocks export.
/// </summary>
public static class GCodeVerifier
{
    /// <summary>GRBL's line buffer is 80 characters; we keep a wide margin.</summary>
    public const int MaxLineLength = 70;

    /// <param name="ManualSpindle">
    /// Router started by hand: M3/M5 must not appear and an M0 pause must come before the first cut.
    /// </param>
    public sealed record Limits(double PlungeClearance, double MaxZ, double MinZ,
        double MinX, double MaxX, double MinY, double MaxY, double EndZ, bool ManualSpindle = false);

    private static readonly HashSet<string> AllowedG = new() { "0", "1", "4", "17", "21", "54", "90", "94" };
    private static readonly HashSet<string> AllowedM = new() { "0", "3", "5", "30" };
    private static readonly HashSet<char> AllowedWords = new() { 'G', 'M', 'X', 'Y', 'Z', 'F', 'S', 'P' };

    public static IReadOnlyList<string> Verify(IReadOnlyList<string> lines, Limits limits)
    {
        var issues = new List<string>();
        void Fail(int n, string msg) { if (issues.Count < 50) issues.Add($"Rad {n + 1}: {msg}"); }

        bool g21 = false, g90 = false, g94 = false, g17 = false, g54 = false;
        bool spindleOn = false, ended = false, anyMotion = false, firstMotionChecked = false;
        string motion = "";
        double feed = 0;
        double? x = null, y = null, z = null;

        for (int n = 0; n < lines.Count; n++)
        {
            var raw = lines[n];
            if (raw.Length > MaxLineLength) Fail(n, $"för lång ({raw.Length} tecken).");
            if (raw.Any(c => c < 0x20 || c > 0x7E)) Fail(n, "innehåller tecken utanför ASCII.");
            var code = StripComments(raw, out var commentError);
            if (commentError) Fail(n, "ofullständig kommentar.");
            if (code.Length == 0) continue;
            if (ended) Fail(n, "kod efter programslut (M30).");

            var words = Words(code, out var parseError);
            if (parseError is not null) { Fail(n, parseError); continue; }
            double? nx = null, ny = null, nz = null, f = null, s = null, p = null;
            bool dwell = false;
            foreach (var (letter, text, value) in words)
            {
                if (!AllowedWords.Contains(letter)) { Fail(n, $"ordet {letter} används inte av CNC Studio."); continue; }
                switch (letter)
                {
                    case 'G':
                        if (!AllowedG.Contains(text)) { Fail(n, $"G{text} är inte tillåten."); break; }
                        switch (text)
                        {
                            case "0": case "1": motion = text; break;
                            case "4": dwell = true; break;
                            case "17": g17 = true; break; case "21": g21 = true; break;
                            case "54": g54 = true; break; case "90": g90 = true; break; case "94": g94 = true; break;
                        }
                        break;
                    case 'M':
                        if (!AllowedM.Contains(text)) { Fail(n, $"M{text} är inte tillåten."); break; }
                        if (limits.ManualSpindle)
                        {
                            if (text is "3" or "5") Fail(n, $"M{text} används inte med manuell fräs (har ingen effekt).");
                            if (text == "0") spindleOn = true; // the operator starts the router at the pause
                        }
                        else
                        {
                            if (text == "0") Fail(n, "M0 används bara med manuell fräs.");
                            if (text == "3") spindleOn = true;
                            if (text == "5") spindleOn = false;
                        }
                        if (text == "30") ended = true;
                        break;
                    case 'X': nx = value; break;
                    case 'Y': ny = value; break;
                    case 'Z': nz = value; break;
                    case 'F': f = value; break;
                    case 'S': s = value; break;
                    case 'P': p = value; break;
                }
            }
            if (f is double fv) { if (fv <= 0) Fail(n, "F måste vara större än 0."); else feed = fv; }
            if (s is double sv && sv < 0) Fail(n, "S får inte vara negativt.");
            if (dwell && (p is not double pv || pv <= 0)) Fail(n, "G4 kräver P > 0 (sekunder).");
            if (ended && spindleOn && !limits.ManualSpindle) Fail(n, "programmet avslutas med spindeln igång (M5 saknas före M30).");

            bool hasAxis = nx is not null || ny is not null || nz is not null;
            if (!hasAxis || dwell) continue;
            if (!(g21 && g90 && g94 && g17 && g54)) Fail(n, "rörelse före G21/G90/G94/G17/G54.");
            if (motion == "") { Fail(n, "rörelse utan G0/G1."); continue; }

            if (!firstMotionChecked)
            {
                firstMotionChecked = true;
                if (nx is not null || ny is not null || nz is null)
                    Fail(n, "första rörelsen måste vara att lyfta Z (G0 Z…) – maskinens position är okänd.");
            }
            var tx = nx ?? x; var ty = ny ?? y; var tz = nz ?? z;
            bool movesXY = (nx is not null && nx != x) || (ny is not null && ny != y);
            if (motion == "0" && movesXY)
            {
                if (tz is null || z is null) Fail(n, "sidledsförflyttning innan Z är känd.");
                else if (Math.Min(z.Value, tz.Value) < limits.PlungeClearance - 1e-6)
                    Fail(n, $"snabbförflyttning i sidled på Z {Math.Min(z.Value, tz.Value):0.###} – under säker höjd.");
            }
            if (motion == "1")
            {
                if (feed <= 0) Fail(n, "G1 utan matning (F).");
                if (!spindleOn && tz < 0) Fail(n, limits.ManualSpindle
                    ? "skär i materialet utan paus (M0) för att starta fräsen." : "skär i materialet utan att spindeln är på (M3).");
            }
            if (tz is double zz && (zz > limits.MaxZ + 1e-6 || zz < limits.MinZ - 1e-6))
                Fail(n, $"Z {zz:0.###} utanför tillåtet område ({limits.MinZ:0.###} till {limits.MaxZ:0.###}).");
            if (tx is double xx && (xx < limits.MinX || xx > limits.MaxX)) Fail(n, $"X {xx:0.###} utanför rimligt område.");
            if (ty is double yy && (yy < limits.MinY || yy > limits.MaxY)) Fail(n, $"Y {yy:0.###} utanför rimligt område.");
            x = tx; y = ty; z = tz; anyMotion = true;
        }
        if (!ended) issues.Add("Programmet slutar inte med M30.");
        if (spindleOn && !limits.ManualSpindle) issues.Add("Spindeln stängs aldrig av (M5 saknas).");
        if (anyMotion && (z is null || z < limits.EndZ - 1e-6)) issues.Add("Fräsen är inte lyft till säker höjd vid programslut.");
        return issues;
    }

    /// <summary>Removes "( … )" and ";" comments (GRBL protocol.c discards both).</summary>
    public static string StripComments(string line, out bool error)
    {
        error = false;
        var sb = new System.Text.StringBuilder();
        bool inParen = false;
        foreach (var c in line)
        {
            if (inParen) { if (c == ')') inParen = false; continue; }
            if (c == '(') { inParen = true; continue; }
            if (c == ';') break;
            if (c > ' ') sb.Append(char.ToUpperInvariant(c));
        }
        if (inParen) error = true;
        return sb.ToString();
    }

    private static List<(char Letter, string Text, double Value)> Words(string code, out string? error)
    {
        error = null;
        var list = new List<(char, string, double)>();
        int i = 0;
        while (i < code.Length)
        {
            char letter = code[i++];
            if (letter < 'A' || letter > 'Z') { error = $"oväntat tecken '{letter}'."; return list; }
            int start = i;
            while (i < code.Length && (char.IsDigit(code[i]) || code[i] is '.' or '-' or '+')) i++;
            var text = code[start..i];
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            { error = $"ogiltigt tal efter {letter}."; return list; }
            list.Add((letter, text, v));
        }
        return list;
    }
}
