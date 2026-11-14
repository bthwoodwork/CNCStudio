using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Grbl;

/// <summary>
/// Before a program starts: does it stay inside the machine with the X/Y zero that is set now? GRBL's soft
/// limits keep machine positions between −$13x and 0 on every axis (machine zero is where homing ends), and a
/// move outside stops the run with ALARM:2. The CAM checks plan against where the material is drawn on the
/// table; this check uses the work offset the machine really has (G54 + G92), so a forgotten or wrong X/Y
/// zero is caught before anything moves. Z is checked against the probed or zeroed Z elsewhere.
/// </summary>
public static class JobEnvelope
{
    /// <summary>The machine's X/Y range with the offset added: where the program's X/Y extremes land.</summary>
    public sealed record Reach(double MinX, double MaxX, double MinY, double MaxY, double MinZ = 0, double MaxZ = 0);

    /// <summary>Where the program's moves reach in machine coordinates, with <paramref name="offset"/> = work offset (MPos − WPos).</summary>
    public static Reach? MachineReach(IReadOnlyList<ProgramMove> moves, double[] offset)
    {
        if (moves.Count == 0 || offset.Length < 2) return null;
        var xs = moves.SelectMany(m => new[] { m.X0, m.X1 }).ToList();
        var ys = moves.SelectMany(m => new[] { m.Y0, m.Y1 }).ToList();
        var zs = moves.SelectMany(m => new[] { m.Z0, m.Z1 }).ToList();
        double oz = offset.Length >= 3 ? offset[2] : 0;
        return new Reach(xs.Min() + offset[0], xs.Max() + offset[0], ys.Min() + offset[1], ys.Max() + offset[1], zs.Min() + oz, zs.Max() + oz);
    }

    /// <summary>
    /// Null when every move is inside −$130…0, −$131…0 and (with Z in <paramref name="offset"/>) −$132…0,
    /// otherwise what goes outside and what to do.
    /// <paramref name="workZeroHint"/> says where X0 Y0 belongs (e.g. "materialets främre vänstra hörn").
    /// </summary>
    public static string? Check(IReadOnlyList<ProgramMove> moves, double[] offset, IReadOnlyDictionary<int, double> settings, string workZeroHint)
    {
        if (MachineReach(moves, offset) is not { } r) return null;
        if (!settings.TryGetValue(130, out var tx) || !settings.TryGetValue(131, out var ty) || tx <= 0 || ty <= 0) return null;
        const double tolerance = 0.01;
        var problems = new List<string>();
        void Axis(string name, double min, double max, double travel)
        {
            if (max > tolerance) problems.Add($"{name} går till MPos {F(max)} mm – {F(max)} mm förbi maskinens gräns (0)");
            if (min < -travel - tolerance) problems.Add($"{name} går till MPos {F(min)} mm – {F(-travel - min)} mm förbi maskinens gräns (−{F(travel)})");
        }
        Axis("X", r.MinX, r.MaxX, tx);
        Axis("Y", r.MinY, r.MaxY, ty);
        bool xy = problems.Count > 0;
        // Z with the Z zero that is set (offset[2] = G54 Z + G92 Z + tool length): not into the switch, not below the travel.
        bool z = false;
        if (offset.Length >= 3 && settings.TryGetValue(132, out var tz) && tz > 0)
        {
            if (r.MaxZ > tolerance) { problems.Add($"Z går upp till MPos {F(r.MaxZ)} mm – {F(r.MaxZ)} mm in i Z-brytaren (0)"); z = true; }
            if (r.MinZ < -tz - tolerance) { problems.Add($"Z går ned till MPos {F(r.MinZ)} mm – {F(-tz - r.MinZ)} mm under maskinens Z-område (−{F(tz)})"); z = true; }
        }
        if (problems.Count == 0) return null;
        var text = "Programmet skulle gå utanför maskinens arbetsområde med den nollpunkt som är satt nu:\n" +
                   string.Join("\n", problems.Select(p => "• " + p)) + "\n";
        if (xy) text += $"\nX/Y-nollan (G54) står på MPos X {F(offset[0])}  Y {F(offset[1])}. Den ska vara i {workZeroHint}. Jogga dit och klicka Nolla X + Y.";
        if (z) text += $"\nZ-nollan står på MPos Z {F(offset[2])}. Den ska vara på materialets ovansida – nolla eller proba Z igen. " +
                       "Är den rätt är programmet för djupt eller för högt för maskinen (eller fräsen för kort).";
        return text + "\n\nStarta igen när det är rättat. (GRBL hade stoppat med ALARM:2 – Soft limit.)";
    }

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
