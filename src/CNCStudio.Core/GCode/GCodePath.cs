using System;
using System.Collections.Generic;
using System.Globalization;

namespace CNCStudio.Core.GCode;

/// <summary>One straight move of a program, in work coordinates (G54), with the line it comes from.</summary>
public readonly record struct ProgramMove(int LineIndex, double X0, double Y0, double Z0, double X1, double Y1, double Z1, bool Rapid);

/// <summary>
/// Reads the moves of a G-code program (G0/G1/G2/G3 with X Y Z, I J or R, G90/G91, G20/G21, G17–G19) so the
/// run can be drawn, checked against the machine and the part already done shown differently. Arcs in the XY
/// plane are split into short straight pieces (at most <see cref="ArcSegmentMm"/> long, all with the arc's line
/// index); arcs in another plane are taken as straight moves. Lines with G53 (machine coordinates) are not part
/// of the drawing. The start position is unknown until the first move names every axis; until then missing
/// axes are 0.
/// </summary>
public static class GCodePath
{
    /// <summary>The longest straight piece an arc is split into (mm).</summary>
    public const double ArcSegmentMm = 1.0;

    public static IReadOnlyList<ProgramMove> Parse(IReadOnlyList<string> lines)
    {
        var list = new List<ProgramMove>();
        double x = 0, y = 0, z = 0;
        bool absolute = true, inches = false;
        int motion = 0, plane = 17;
        for (int n = 0; n < lines.Count; n++)
        {
            var code = Strip(lines[n]);
            if (code.Length == 0 || code[0] == '$') continue;
            double? nx = null, ny = null, nz = null, ci = null, cj = null, cr = null;
            bool g53 = false;
            int i = 0;
            while (i < code.Length)
            {
                char letter = char.ToUpperInvariant(code[i]);
                int start = ++i;
                while (i < code.Length && (char.IsDigit(code[i]) || code[i] is '.' or '-' or '+')) i++;
                if (!double.TryParse(code[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                switch (letter)
                {
                    case 'G':
                        if (v is 0 or 1 or 2 or 3) motion = (int)v;
                        else if (v == 90) absolute = true;
                        else if (v == 91) absolute = false;
                        else if (v == 20) inches = true;
                        else if (v == 21) inches = false;
                        else if (v == 53) g53 = true;
                        else if (v is 17 or 18 or 19) plane = (int)v;
                        else if (v is 80) motion = -1;   // motion cancelled: axis words alone do not move
                        break;
                    case 'X': nx = v; break;
                    case 'Y': ny = v; break;
                    case 'Z': nz = v; break;
                    case 'I': ci = v; break;
                    case 'J': cj = v; break;
                    case 'R': cr = v; break;
                }
            }
            if (nx is null && ny is null && nz is null) continue;
            if (g53 || motion < 0) continue;                  // machine coordinates: not part of the drawing
            double k = inches ? 25.4 : 1;
            double tx = nx is double ax ? (absolute ? ax * k : x + ax * k) : x;
            double ty = ny is double ay ? (absolute ? ay * k : y + ay * k) : y;
            double tz = nz is double az ? (absolute ? az * k : z + az * k) : z;
            if (motion is 2 or 3 && plane == 17 && Arc(x, y, tx, ty, ci * k, cj * k, cr * k, motion == 2) is { } arc)
            {
                // Short straight pieces along the arc; Z changes evenly (helix). The points where the arc crosses
                // a quarter (its outermost X and Y) are always included, so the extent is exact.
                int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(arc.Sweep) * arc.Radius / ArcSegmentMm));
                var ts = new SortedSet<double>();
                for (int s = 1; s <= pieces; s++) ts.Add((double)s / pieces);
                for (int q = -8; q <= 8; q++)
                {
                    double t = (q * Math.PI / 2 - arc.Start) / arc.Sweep;
                    if (t > 1e-9 && t < 1 - 1e-9) ts.Add(t);
                }
                double px = x, py = y, pz = z;
                foreach (var t in ts)
                {
                    double a = arc.Start + arc.Sweep * t;
                    bool last = t >= 1 - 1e-12;
                    double qx = last ? tx : arc.Cx + arc.Radius * Math.Cos(a);
                    double qy = last ? ty : arc.Cy + arc.Radius * Math.Sin(a);
                    double qz = z + (tz - z) * t;
                    list.Add(new ProgramMove(n, px, py, pz, qx, qy, qz, false));
                    px = qx; py = qy; pz = qz;
                }
            }
            else if (tx != x || ty != y || tz != z) list.Add(new ProgramMove(n, x, y, z, tx, ty, tz, motion == 0));
            x = tx; y = ty; z = tz;
        }
        return list;
    }

    /// <summary>
    /// An XY arc from (x0, y0) to (x1, y1): centre from I/J (offsets from the start, GRBL's default G91.1) or
    /// from the radius R (negative R = the longer way round). Start angle and sweep (negative = clockwise, G2).
    /// A full circle when the end is the start (I/J only). Null when the arc cannot be formed.
    /// </summary>
    internal static (double Cx, double Cy, double Radius, double Start, double Sweep)? Arc(
        double x0, double y0, double x1, double y1, double? i, double? j, double? r, bool clockwise)
    {
        double cx, cy;
        if (i is not null || j is not null)
        {
            cx = x0 + (i ?? 0); cy = y0 + (j ?? 0);
        }
        else if (r is double rr && rr != 0)
        {
            double dx = x1 - x0, dy = y1 - y0, d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-9 || d > 2 * Math.Abs(rr) + 1e-6) return null;
            double h = Math.Sqrt(Math.Max(0, rr * rr - d * d / 4));
            // GRBL: the centre is to the right of the chord for G2 with positive R, to the left for G3 (and the reverse for negative R).
            double side = (clockwise ? 1 : -1) * (rr > 0 ? 1 : -1);
            cx = (x0 + x1) / 2 + side * h * dy / d;
            cy = (y0 + y1) / 2 - side * h * dx / d;
        }
        else return null;
        double radius = Math.Sqrt((x0 - cx) * (x0 - cx) + (y0 - cy) * (y0 - cy));
        if (radius < 1e-9) return null;
        double a0 = Math.Atan2(y0 - cy, x0 - cx), a1 = Math.Atan2(y1 - cy, x1 - cx);
        double sweep = a1 - a0;
        bool full = Math.Abs(x1 - x0) < 1e-9 && Math.Abs(y1 - y0) < 1e-9;
        if (clockwise) { if (sweep >= -1e-12) sweep -= 2 * Math.PI; if (full) sweep = -2 * Math.PI; }
        else { if (sweep <= 1e-12) sweep += 2 * Math.PI; if (full) sweep = 2 * Math.PI; }
        return (cx, cy, radius, a0, sweep);
    }

    private static string Strip(string line)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        bool paren = false;
        foreach (var c in line)
        {
            if (c == ';') break;
            if (c == '(') { paren = true; continue; }
            if (c == ')') { paren = false; continue; }
            if (!paren && !char.IsWhiteSpace(c)) sb.Append(c);
        }
        return sb.ToString();
    }
}
