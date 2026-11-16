using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.GCode;

/// <summary>A 2D affine map of the XY plane: x' = A·x + B·y + E, y' = C·x + D·y + F.</summary>
public readonly record struct XYMap(double A, double B, double C, double D, double E, double F)
{
    public static readonly XYMap Identity = new(1, 0, 0, 1, 0, 0);
    public static XYMap Translate(double dx, double dy) => new(1, 0, 0, 1, dx, dy);
    /// <summary>Mirror left–right about the vertical line x = <paramref name="cx"/>.</summary>
    public static XYMap MirrorX(double cx) => new(-1, 0, 0, 1, 2 * cx, 0);
    /// <summary>Mirror front–back about the horizontal line y = <paramref name="cy"/>.</summary>
    public static XYMap MirrorY(double cy) => new(1, 0, 0, -1, 0, 2 * cy);
    /// <summary>Rotation by a multiple of 90° (counter-clockwise positive, as seen from above) about (px, py).</summary>
    public static XYMap Rotate(int degrees, double px, double py)
    {
        int q = ((degrees / 90) % 4 + 4) % 4;
        var (a, b, c, d) = q switch { 1 => (0, -1, 1, 0), 2 => (-1, 0, 0, -1), 3 => (0, 1, -1, 0), _ => (1, 0, 0, 1) };
        return new(a, b, c, d, px - a * px - b * py, py - c * px - d * py);
    }
    /// <summary>A mirror (negative determinant) turns clockwise arcs into counter-clockwise ones.</summary>
    public bool Flips => A * D - B * C < 0;
    /// <summary>Axis-separable: X depends only on X and Y only on Y, so a line with only one of them can be mapped alone.</summary>
    public bool Separable => B == 0 && C == 0;
    public (double X, double Y) Point(double x, double y) => (A * x + B * y + E, C * x + D * y + F);
    public (double X, double Y) Vector(double x, double y) => (A * x + B * y, C * x + D * y);
}

/// <summary>The result of an edit: the new lines, or why it could not be made (then nothing changes).</summary>
public sealed record GCodeEditResult(IReadOnlyList<string> Lines, IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// G-code-editorn: mirror, rotate (in 90° steps), move and copy a program in a grid – for a file from another CAM
/// program as well as one of our own. Only X/Y change; Z, feeds, speeds and everything else stay as written.
/// <para>
/// Handled: G0/G1/G2/G3 and G38.x in G90 (absolute) and G91 (relative), lines with only X or only Y (the other
/// axis is taken from the position so far), arcs by I/J (always relative in GRBL, G91.1) and by R; a mirror swaps
/// G2 and G3. Comments are kept. Refused, with the line: inch programs (G20), arcs outside the XY plane
/// (G18/G19), and commands whose X/Y mean something else (G10 offsets, G28/G30 stored positions, G92) – a
/// moved program must not quietly move a stored position too. G53 (machine coordinates) is kept as written.
/// </para>
/// </summary>
public static class GCodeTransform
{
    private sealed class Word
    {
        public char Letter;
        public string Text = "";
        public double Value;
    }

    /// <summary>Applies <paramref name="map"/> to every X/Y in the program.</summary>
    public static GCodeEditResult Apply(IReadOnlyList<string> lines, XYMap map)
    {
        var output = new List<string>(lines.Count);
        var problems = new List<string>();
        bool absolute = true;
        int motion = 0;                        // modal G0/G1/G2/G3 (38 for probing)
        int plane = 17;
        double? x = null, y = null;            // current position in the program's own coordinates
        for (int i = 0; i < lines.Count; i++)
        {
            var (code, comment) = Split(lines[i]);
            var words = Parse(code);
            if (words is null) { output.Add(lines[i]); continue; }
            if (words.Count == 0) { output.Add(lines[i]); continue; }
            string where = $"Rad {i + 1}";
            var gs = words.Where(w => w.Letter == 'G').Select(w => w.Value).ToList();
            bool machine = gs.Contains(53);
            foreach (var g in gs)
            {
                if (g == 90) absolute = true;
                else if (g == 91) absolute = false;
                else if (g is 0 or 1 or 2 or 3) motion = (int)g;
                else if (g is >= 38.2 and <= 38.5) motion = 38;
                else if (g is 17 or 18 or 19) plane = (int)g;
                else if (g == 20) problems.Add($"{where}: programmet är i tum (G20) – editorn ändrar bara program i mm.");
                else if (g is 10 or 28 or 30 or 92)
                    problems.Add($"{where}: G{Num(g)} – X/Y betyder en sparad position eller nollpunkt här och kan inte flyttas med programmet. Ta bort raden först.");
            }
            var wx = words.FirstOrDefault(w => w.Letter == 'X');
            var wy = words.FirstOrDefault(w => w.Letter == 'Y');
            var wi = words.FirstOrDefault(w => w.Letter == 'I');
            var wj = words.FirstOrDefault(w => w.Letter == 'J');
            bool hasXY = wx is not null || wy is not null;
            if (machine)
            {
                // Machine coordinates: left as written, and the work position is no longer known.
                if (wx is not null) x = null;
                if (wy is not null) y = null;
                output.Add(lines[i]);
                continue;
            }
            bool arc = motion is 2 or 3 && (hasXY || wi is not null || wj is not null || words.Any(w => w.Letter == 'R'));
            if (arc && plane != 17) problems.Add($"{where}: bågar i {(plane == 18 ? "XZ" : "YZ")}-planet (G{plane}) kan inte speglas eller roteras.");
            if (!hasXY && wi is null && wj is null) { output.Add(lines[i]); continue; }

            double? nx = null, ny = null;
            if (absolute)
            {
                if (hasXY)
                {
                    double? sx = wx?.Value ?? x, sy = wy?.Value ?? y;
                    if (map.Separable)
                    {
                        if (wx is not null) nx = map.A * wx.Value + map.E;
                        if (wy is not null) ny = map.D * wy.Value + map.F;
                    }
                    else if (sx is double px && sy is double py)
                    {
                        (nx, ny) = map.Point(px, py);
                    }
                    else problems.Add($"{where}: bara {(wx is null ? "Y" : "X")} anges innan programmet har angett {(wx is null ? "X" : "Y")} – positionen är okänd och raden kan inte roteras.");
                    if (wx is not null) x = wx.Value;
                    if (wy is not null) y = wy.Value;
                }
            }
            else if (hasXY)
            {
                var (dx, dy) = map.Vector(wx?.Value ?? 0, wy?.Value ?? 0);
                if (map.Separable) { if (wx is not null) nx = dx; if (wy is not null) ny = dy; }
                else { nx = dx; ny = dy; }
                if (wx is not null && x is double cx) x = cx + wx.Value;
                if (wy is not null && y is double cy) y = cy + wy.Value;
            }
            double? ni = null, nj = null;
            if (wi is not null || wj is not null)
            {
                var (vi, vj) = map.Vector(wi?.Value ?? 0, wj?.Value ?? 0);
                if (map.Separable) { if (wi is not null) ni = vi; if (wj is not null) nj = vj; }
                else { ni = vi; nj = vj; }
            }

            // Rebuild the line: the same words in the same order, X/Y/I/J replaced or added after the last one.
            var sb = new StringBuilder();
            bool swapArc = map.Flips && arc;
            bool wroteMotion = false, wroteX = false, wroteY = false, wroteI = false, wroteJ = false;
            foreach (var w in words)
            {
                string text = w.Letter switch
                {
                    'G' when swapArc && w.Value is 2 or 3 => Mark(ref wroteMotion, w.Value == 2 ? "G3" : "G2"),
                    'X' => nx is double v ? Mark(ref wroteX, "X" + N(v)) : w.Text,
                    'Y' => ny is double v ? Mark(ref wroteY, "Y" + N(v)) : w.Text,
                    'I' => ni is double v ? Mark(ref wroteI, "I" + N(v)) : w.Text,
                    'J' => nj is double v ? Mark(ref wroteJ, "J" + N(v)) : w.Text,
                    _ => w.Text
                };
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(text);
            }
            // Rotation can give a line that only had X a Y as well (and I without J a J).
            if (nx is double ax && !wroteX && wx is null) sb.Append(" X" + N(ax));
            if (ny is double ay && !wroteY && wy is null) sb.Append(" Y" + N(ay));
            if (ni is double ai && !wroteI && wi is null) sb.Append(" I" + N(ai));
            if (nj is double aj && !wroteJ && wj is null) sb.Append(" J" + N(aj));
            // A modal arc continued without G2/G3 on this line: write the swapped direction explicitly.
            if (swapArc && !wroteMotion) sb.Insert(0, (motion == 2 ? "G3" : "G2") + " ");
            output.Add(sb + comment);
        }
        return problems.Count > 0 ? new GCodeEditResult(lines, problems.Distinct().Take(20).ToList()) : new GCodeEditResult(output, problems);
    }

    /// <summary>
    /// <paramref name="columns"/> × <paramref name="rows"/> copies, <paramref name="stepX"/>/<paramref name="stepY"/> mm apart
    /// (centre to centre). Between copies the tool goes up to the program's highest Z in G90 first, so it never
    /// travels low over the work; the end of program (M2/M30, %) is kept only after the last copy.
    /// </summary>
    public static GCodeEditResult Grid(IReadOnlyList<string> lines, int columns, int rows, double stepX, double stepY, double safeZ)
    {
        if (columns < 1 || rows < 1 || columns * rows > 400)
            return new GCodeEditResult(lines, new[] { "Antal kopior: 1–400 sammanlagt, minst 1 i varje led." });
        if (!double.IsFinite(stepX) || !double.IsFinite(stepY))
            return new GCodeEditResult(lines, new[] { "Ange avståndet mellan kopiorna i mm." });
        var body = lines.Where(l => !IsProgramEnd(l)).ToList();
        bool hadEnd = lines.Any(IsProgramEnd);
        var output = new List<string>();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                // Row by row, back and forth, so the tool never crosses the whole grid between two copies.
                int col = r % 2 == 0 ? c : columns - 1 - c;
                var copy = Apply(body, XYMap.Translate(col * stepX, r * stepY));
                if (!copy.Ok) return new GCodeEditResult(lines, copy.Problems);
                if (output.Count > 0)
                {
                    output.Add($"(Kopia {r * columns + c + 1} av {columns * rows}: rad {r + 1}, kolumn {col + 1})");
                    output.Add("G90");
                    output.Add("G0 Z" + N(safeZ));
                }
                output.AddRange(copy.Lines);
            }
        if (hadEnd) output.Add("M30");
        return new GCodeEditResult(output, Array.Empty<string>());
    }

    private static bool IsProgramEnd(string line)
    {
        var code = ResumePlanner.Strip(line);
        return code == "%" || code is "M2" or "M02" or "M30";
    }

    private static string Mark(ref bool flag, string text) { flag = true; return text; }

    /// <summary>The code part and the comment (parentheses or ';'), so comments survive the edit.</summary>
    private static (string Code, string Comment) Split(string line)
    {
        int semi = -1, depth = 0;
        var code = new StringBuilder();
        var comment = new StringBuilder();
        for (int k = 0; k < line.Length; k++)
        {
            char ch = line[k];
            if (depth == 0 && ch == ';') { semi = k; break; }
            if (ch == '(') depth++;
            if (depth > 0) comment.Append(ch); else code.Append(ch);
            if (ch == ')' && depth > 0) depth--;
        }
        string rest = semi >= 0 ? line[semi..] : "";
        string c = (comment.ToString() + (rest.Length > 0 ? (comment.Length > 0 ? " " : "") + rest : "")).Trim();
        return (code.ToString(), c.Length > 0 ? " " + c : "");
    }

    /// <summary>Words of a code part; null for a line that is not plain words (kept as written, e.g. "%" or "$H").</summary>
    private static List<Word>? Parse(string code)
    {
        var s = code.Trim();
        if (s.Length == 0) return new List<Word>();
        if (s[0] == '$' || s[0] == '%') return null;
        var list = new List<Word>();
        int k = 0;
        while (k < s.Length)
        {
            if (char.IsWhiteSpace(s[k])) { k++; continue; }
            char letter = char.ToUpperInvariant(s[k]);
            if (!char.IsLetter(letter)) return null;
            int start = ++k;
            while (k < s.Length && (char.IsDigit(s[k]) || s[k] is '.' or '-' or '+' || char.IsWhiteSpace(s[k]) && k + 1 < s.Length && (char.IsDigit(s[k + 1]) || s[k + 1] == '.'))) k++;
            string number = s[start..k].Replace(" ", "");
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
            list.Add(new Word { Letter = letter, Value = v, Text = letter + number });
        }
        return list;
    }

    private static string Num(double g) => g.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Up to four decimals, no trailing zeros, never "-0".</summary>
    public static string N(double v)
    {
        var r = Math.Round(v, 4, MidpointRounding.AwayFromZero);
        if (r == 0) r = 0;
        return r.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
