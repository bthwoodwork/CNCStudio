using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

public sealed record RunTimeEstimate(double CuttingMinutes, double RapidMinutes, double DwellMinutes,
    IReadOnlyDictionary<int, double> MinutesPerOperation, bool RapidRateAssumed, int ManualPauses)
{
    public double TotalMinutes => CuttingMinutes + RapidMinutes + DwellMinutes;
}

/// <summary>
/// Run time from the motion plan: distance / feed for feed moves (plunges at the plunge rate) and
/// distance / max rate for rapids ($110–$112, or an assumed rate if they have not been read).
/// Acceleration is ignored, so real time is usually somewhat longer. Manual pauses (M0) are counted
/// but not timed.
/// </summary>
public static class RunTimeEstimator
{
    /// <summary>Used when $110–$112 are unknown. Deliberately modest.</summary>
    public const double AssumedRapidXY = 3000, AssumedRapidZ = 500;

    public static RunTimeEstimate Estimate(CncDocument doc, CamReport report)
    {
        var m = doc.Machine;
        bool assumed = m.MaxRateX is null || m.MaxRateY is null || m.MaxRateZ is null;
        double rapidXY = m.MaxRateX is double rx && m.MaxRateY is double ry ? Math.Min(rx, ry) : AssumedRapidXY;
        double rapidZ = m.MaxRateZ ?? AssumedRapidZ;

        double cutting = 0, rapid = 0;
        var perOp = new Dictionary<int, double>();
        foreach (var mv in report.Motion.Moves)
        {
            double dz = Math.Abs(mv.Z1 - mv.Z0);
            double xy = mv.FollowsPath ? mv.S1 - mv.S0 : mv.XYLength;
            if (mv.Kind == MoveKind.Rapid)
            {
                // GRBL moves all axes together; the slowest axis decides the time.
                rapid += Math.Max(xy / rapidXY, dz / rapidZ);
                continue;
            }
            var result = mv.OperationIndex >= 0 && mv.OperationIndex < report.Operations.Count ? report.Operations[mv.OperationIndex] : null;
            var op = result?.Operation;
            if (op is null) continue;
            if (mv.FollowsPath && result!.Preview is { Is3D: true } p3 && mv.PathIndex < p3.PathZ.Count)
            {
                // 3D: the G-code follows the surface point by point at the cutting feed – its 3D length, not a ramp.
                double minutes3D = Length3D(p3.Centerlines[mv.PathIndex], p3.PathZ[mv.PathIndex]) / op.FeedRate;
                cutting += minutes3D;
                perOp[mv.OperationIndex] = perOp.GetValueOrDefault(mv.OperationIndex) + minutes3D;
                continue;
            }
            double minutes = xy > 1e-9
                ? Math.Sqrt(xy * xy + dz * dz) / (dz > 1e-9 && mv.FollowsPath ? op.RampFeedRate : op.FeedRate) // ramps / cuts and links
                : dz / op.PlungeRate;                        // straight plunges and tab lifts
            cutting += minutes;
            perOp[mv.OperationIndex] = perOp.GetValueOrDefault(mv.OperationIndex) + minutes;
        }

        // Spindle starts/changes: GRBL-controlled ones wait the spin-up time; manual ones are pauses.
        // One program per tool (no tool changer): each program starts the spindle, and changes speed within it.
        int changes = report.Operations.Where(o => o.Analyzed).GroupBy(o => GCode.GCodeGenerator.ToolKey(o.Operation))
            .Sum(g => g.Select(o => o.Operation.SpindleRpm).Distinct().Count());
        bool manual = m.Spindle == SpindleControl.Manual;
        double dwell = manual ? 0 : changes * doc.Motion.SpindleSpinUpSeconds / 60.0;
        return new RunTimeEstimate(cutting, rapid, dwell, perOp, assumed, manual ? changes : 0);
    }

    private static double Length3D(IReadOnlyList<CamPoint> path, IReadOnlyList<double> z)
    {
        double total = 0;
        for (int i = 1; i < path.Count && i < z.Count; i++)
        {
            double dx = path[i].X - path[i - 1].X, dy = path[i].Y - path[i - 1].Y, dz = z[i] - z[i - 1];
            total += Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        return total;
    }

    public static string Format(double minutes)
    {
        if (minutes < 1) return "< 1 min";
        var t = TimeSpan.FromMinutes(minutes);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Ceiling(minutes)} min";
    }
}
