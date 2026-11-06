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
            var op = mv.OperationIndex >= 0 && mv.OperationIndex < report.Operations.Count
                ? report.Operations[mv.OperationIndex].Operation : null;
            if (op is null) continue;
            double minutes = xy > 1e-9
                ? Math.Sqrt(xy * xy + dz * dz) / op.FeedRate // cuts, links and ramps
                : dz / op.PlungeRate;                        // straight plunges and tab lifts
            cutting += minutes;
            perOp[mv.OperationIndex] = perOp.GetValueOrDefault(mv.OperationIndex) + minutes;
        }

        // Spindle starts/changes: GRBL-controlled ones wait the spin-up time; manual ones are pauses.
        int changes = report.Operations.Where(o => o.Analyzed).Select(o => o.Operation.SpindleRpm).Distinct().Count();
        bool manual = m.Spindle == SpindleControl.Manual;
        double dwell = manual ? 0 : changes * doc.Motion.SpindleSpinUpSeconds / 60.0;
        return new RunTimeEstimate(cutting, rapid, dwell, perOp, assumed, manual ? changes : 0);
    }

    public static string Format(double minutes)
    {
        if (minutes < 1) return "< 1 min";
        var t = TimeSpan.FromMinutes(minutes);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Ceiling(minutes)} min";
    }
}
