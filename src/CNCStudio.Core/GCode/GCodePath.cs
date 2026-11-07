using System;
using System.Collections.Generic;
using System.Globalization;

namespace CNCStudio.Core.GCode;

/// <summary>One straight move of a program, in work coordinates (G54), with the line it comes from.</summary>
public readonly record struct ProgramMove(int LineIndex, double X0, double Y0, double Z0, double X1, double Y1, double Z1, bool Rapid);

/// <summary>
/// Reads the moves of a G-code program (G0/G1 with X Y Z, G90/G91, G20/G21) so the run can be drawn and
/// the part already done shown differently. Lines with G53 (machine coordinates) are not drawn. The
/// start position is unknown until the first move names every axis; until then missing axes are 0.
/// </summary>
public static class GCodePath
{
    public static IReadOnlyList<ProgramMove> Parse(IReadOnlyList<string> lines)
    {
        var list = new List<ProgramMove>();
        double x = 0, y = 0, z = 0;
        bool absolute = true, rapid = true, inches = false;
        for (int n = 0; n < lines.Count; n++)
        {
            var code = Strip(lines[n]);
            if (code.Length == 0 || code[0] == '$') continue;
            double? nx = null, ny = null, nz = null;
            bool g53 = false, motionWord = false;
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
                        if (v == 0) { rapid = true; motionWord = true; }
                        else if (v == 1) { rapid = false; motionWord = true; }
                        else if (v == 90) absolute = true;
                        else if (v == 91) absolute = false;
                        else if (v == 20) inches = true;
                        else if (v == 21) inches = false;
                        else if (v == 53) g53 = true;
                        break;
                    case 'X': nx = v; break;
                    case 'Y': ny = v; break;
                    case 'Z': nz = v; break;
                }
            }
            _ = motionWord;
            if (nx is null && ny is null && nz is null) continue;
            if (g53) continue;                                 // machine coordinates: not part of the drawing
            double k = inches ? 25.4 : 1;
            double tx = nx is double ax ? (absolute ? ax * k : x + ax * k) : x;
            double ty = ny is double ay ? (absolute ? ay * k : y + ay * k) : y;
            double tz = nz is double az ? (absolute ? az * k : z + az * k) : z;
            if (tx != x || ty != y || tz != z) list.Add(new ProgramMove(n, x, y, z, tx, ty, tz, rapid));
            x = tx; y = ty; z = tz;
        }
        return list;
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
