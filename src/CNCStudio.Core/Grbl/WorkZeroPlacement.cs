using System;
using System.Collections.Generic;

namespace CNCStudio.Core.Grbl;

/// <summary>
/// Nolla X/Y enligt materialplaceringen: where the project's X0 Y0 lies on the machine, from where the material
/// is drawn on the table, so the work offset can be set without jogging there. The table position counts from
/// the home corner like the tool marker; machine positions use the connected machine's own $23/$130/$131 and
/// GRBL's machine space (−$13x…0 after homing; the switch side is −$13x when the axis homes towards minus).
/// The offset is set with G10 L20 P0 from where the tool stands now: nothing moves.
/// </summary>
public static class WorkZeroPlacement
{
    /// <summary>The project's X0 Y0 (material's front-left corner, or its centre) from the home corner, as the tool marker would show it there.</summary>
    public static (double X, double Y) PointFromHome(CncDocument doc)
    {
        var m = doc.Machine;
        bool left = m.HomeCorner is HomeCorner.BackLeft or HomeCorner.FrontLeft;
        bool front = m.HomeCorner is HomeCorner.FrontLeft or HomeCorner.FrontRight;
        // The point on the table canvas (from the table's back-left corner, Y towards the front).
        double x = doc.Stock.X + (doc.WorkZero == WorkZero.Center ? doc.MaterialWidth / 2 : 0);
        double y = doc.Stock.Y + (doc.WorkZero == WorkZero.Center ? doc.MaterialHeight / 2 : doc.MaterialHeight);
        return (left ? x : m.WorkWidthMm - x, front ? m.WorkLengthMm - y : y);
    }

    /// <summary>A point given from the home corner in machine coordinates (the inverse of <see cref="Commissioning.DistanceFromSwitches"/>).</summary>
    public static (double X, double Y) MachinePosition(double fromHomeX, double fromHomeY, IReadOnlyDictionary<int, double> s)
    {
        int mask = (int)s.GetValueOrDefault(23);
        double M(int i, double d) => (mask & (1 << i)) != 0 ? d - s.GetValueOrDefault(130 + i) : -d;
        return (M(0, fromHomeX), M(1, fromHomeY));
    }

    /// <summary>
    /// The reason the zero cannot be set from the placement, or null: the point must lie inside the machine
    /// (−$13x…0), and the tool's position must be known.
    /// </summary>
    public static string? Check((double X, double Y) zero, IReadOnlyDictionary<int, double> s, double[]? mpos)
    {
        if (mpos is not { Length: >= 2 }) return "Maskinens position är okänd – vänta på status och försök igen.";
        if (!s.TryGetValue(130, out var tx) || !s.TryGetValue(131, out var ty)) return "Maskinens arbetsområde ($130/$131) är inte inläst.";
        if (zero.X > 0.001 || zero.X < -tx - 0.001 || zero.Y > 0.001 || zero.Y < -ty - 0.001)
            return "Materialets nollpunkt ligger utanför maskinens arbetsområde – kontrollera materialplaceringen.";
        return null;
    }

    /// <summary>The G10 line that makes <paramref name="zero"/> (machine X/Y) the work zero, sent where the tool is now (<paramref name="mpos"/>).</summary>
    public static string SetLine(double[] mpos, (double X, double Y) zero) => GrblMachineCommands.SetWork(mpos[0] - zero.X, mpos[1] - zero.Y, null);
}
