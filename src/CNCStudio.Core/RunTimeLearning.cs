using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>How much longer (or shorter) this machine's runs take than estimated, learned from its log.</summary>
public sealed record RunTimeFactor(double Factor, int Runs)
{
    public double Apply(double estimatedMinutes) => estimatedMinutes * Factor;
}

/// <summary>
/// Tidsuppskattning som lär sig: the estimate ignores acceleration, so the real time differs by a fairly
/// steady share per machine. Finished, not resumed runs with a known estimate give a ratio each
/// (time cutting – that is, without pauses – over the estimate); the median of the latest ones is the factor.
/// </summary>
public static class RunTimeLearning
{
    public const int MinRuns = 2, MaxRuns = 10;
    /// <summary>Runs shorter than this say more about the start-up than about the machine.</summary>
    public const double MinEstimateMinutes = 1;
    /// <summary>Ratios outside this are something else (a long pause not seen as Hold, a wrong log entry).</summary>
    public const double MinRatio = 0.4, MaxRatio = 4;

    public static double? Ratio(LoggedJob j)
    {
        if (j.Outcome != LoggedJobOutcome.Finished || j.Resumed || j.EstimatedMinutes is not double est || est < MinEstimateMinutes) return null;
        double actual = j.Duration.TotalMinutes - (j.HeldMinutes ?? 0);
        double r = actual / est;
        return r is >= MinRatio and <= MaxRatio ? r : null;
    }

    /// <summary>The learned factor, or null until there are <see cref="MinRuns"/> usable runs.</summary>
    public static RunTimeFactor? Learn(IEnumerable<LoggedJob> jobs)
    {
        var ratios = jobs.OrderBy(j => j.Start).Select(Ratio).OfType<double>().TakeLast(MaxRuns).OrderBy(r => r).ToList();
        if (ratios.Count < MinRuns) return null;
        int n = ratios.Count;
        double median = n % 2 == 1 ? ratios[n / 2] : (ratios[n / 2 - 1] + ratios[n / 2]) / 2;
        return new RunTimeFactor(median, n);
    }

    /// <summary>"Efter 4 körningar: 12 min (+15 %)".</summary>
    public static string Describe(RunTimeFactor f, double estimatedMinutes)
    {
        int pct = (int)Math.Round((f.Factor - 1) * 100);
        return $"Efter {f.Runs} körningar på maskinen: {RunTimeEstimator.Format(f.Apply(estimatedMinutes))} ({(pct >= 0 ? "+" : "−")}{Math.Abs(pct)} %)";
    }
}
