using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CNCStudio.Core.Grbl;

/// <summary>A jog preset: step distances and speed (like gSender's Precise / Normal / Rapid).</summary>
public sealed record JogPreset(string Name, double StepXY, double StepZ, double FeedRate);

/// <summary>
/// Builds and checks jog, zero and go-to commands. Every rule is verified against GRBL 1.1
/// (docs/GRBL-verifierat.md):
/// <list type="bullet">
/// <item>$J= needs axis words and F on every line; only G90/G91, G20/G21 and G53 are allowed, and
/// jogging does not change the parser's modal state. Accepted only in Idle or Jog.</item>
/// <item>G10 L20 P0 sets the ACTIVE work coordinate system so the current position becomes the given
/// value; axes not named are unchanged. G-code is refused in Alarm and Jog (error 9).</item>
/// <item>G53 G0 on one line moves in machine coordinates without changing the WCS.</item>
/// </list>
/// </summary>
public static class GrblMachineCommands
{
    public static readonly IReadOnlyList<JogPreset> DefaultPresets = new[]
    {
        new JogPreset("Precis", 0.5, 0.1, 1000),
        new JogPreset("Normal", 5, 2, 3000),
        new JogPreset("Snabb", 20, 10, 5000),
    };

    /// <summary>Wiki: continuous jogging works with a time step of 0.025–0.06 s.</summary>
    public const double ContinuousJogStepSeconds = 0.05;

    public static bool StateAllowsJog(string? state) => state is "Idle" or "Jog";
    /// <summary>G10 is G-code: refused in Alarm and Jog; only sent when the controller is Idle.</summary>
    public static bool StateAllowsGCode(string? state) => state == "Idle";

    /// <summary>Incremental jog in mm: "$J=G91 G21 X5 F3000". Units and distance mode apply to this line only.</summary>
    public static string Jog(double dx, double dy, double dz, double feed)
    {
        var words = new List<string>();
        if (dx != 0) words.Add("X" + N(dx));
        if (dy != 0) words.Add("Y" + N(dy));
        if (dz != 0) words.Add("Z" + N(dz));
        if (words.Count == 0) throw new ArgumentException("Jogg utan rörelse.");
        if (!double.IsFinite(feed) || feed <= 0) throw new ArgumentException("Jogghastigheten måste vara större än 0.");
        return $"$J=G91 G21 {string.Join(" ", words)} F{N(feed, 0)}";
    }

    /// <summary>Distance for one continuous-jog step at this speed (s = v · dt).</summary>
    public static double ContinuousStep(double feedMmPerMin) => feedMmPerMin / 60.0 * ContinuousJogStepSeconds;

    /// <summary>Jog speed limited to the machine's max rates ($110–$112) for the axes that move.</summary>
    public static double ClampFeed(double feed, MachineProfile m, bool xy, bool z)
    {
        double limit = double.MaxValue;
        if (xy && m.MaxRateX is double rx) limit = Math.Min(limit, rx);
        if (xy && m.MaxRateY is double ry) limit = Math.Min(limit, ry);
        if (z && m.MaxRateZ is double rz) limit = Math.Min(limit, rz);
        return Math.Min(feed, limit);
    }

    /// <summary>
    /// Refuses a jog in a state GRBL would reject, or one that would leave the machine's travel
    /// (−$13x … 0, minus the safety margin) – also when soft limits are off. A move that comes back
    /// towards the safe range is always allowed, e.g. away from the switch right after homing.
    /// </summary>
    public static string? CheckJog(GrblStatus? status, MachineProfile m, double dx, double dy, double dz)
    {
        if (status is null) return "Maskinens läge är okänt – vänta på status.";
        if (status.State == "Alarm")
            return "Maskinen är i larm. Kör homing ($H) först – jogg tas inte emot i larmläge.";
        if (!StateAllowsJog(status.State))
            return $"Jogg går bara i läge Idle eller Jog (nu: {status.State}).";
        if (!m.HomeOnStartup || status.MPos is not { Length: >= 3 } mpos) return null;
        var travel = new[] { m.WorkWidthMm, m.WorkLengthMm, m.ZTravelMm };
        var delta = new[] { dx, dy, dz };
        string[] names = { "X", "Y", "Z" };
        for (int i = 0; i < 3; i++)
        {
            if (delta[i] == 0) continue;
            double lo = -travel[i] + m.LimitMarginMm, hi = -m.LimitMarginMm;
            double now = Outside(mpos[i], lo, hi), after = Outside(mpos[i] + delta[i], lo, hi);
            if (after > 1e-9 && after > now + 1e-9)
                return $"{names[i]}: joggen skulle gå utanför maskinens arbetsområde " +
                       $"(MPos {N(mpos[i] + delta[i])}, tillåtet {N(lo)} till {N(hi)}).";
        }
        return null;

        static double Outside(double v, double lo, double hi) => v < lo ? lo - v : v > hi ? v - hi : 0;
    }

    /// <summary>"G10 L20 P0 X0 Y0": the current position becomes 0 on the named axes of the active WCS.</summary>
    public static string Zero(bool x, bool y, bool z) => SetWork(x ? 0 : null, y ? 0 : null, z ? 0 : null);

    /// <summary>"G10 L20 P0 X12.5": the current position gets this work coordinate value.</summary>
    public static string SetWork(double? x, double? y, double? z)
    {
        var words = new List<string>();
        if (x is double vx) words.Add("X" + N(vx));
        if (y is double vy) words.Add("Y" + N(vy));
        if (z is double vz) words.Add("Z" + N(vz));
        if (words.Count == 0) throw new ArgumentException("Ingen axel vald.");
        return "G10 L20 P0 " + string.Join(" ", words);
    }

    /// <summary>
    /// Go to a work position in XY: first lift Z near the top in machine coordinates (G53, just below
    /// the switch by the safety margin), then move in XY in the active WCS (G90 G0).
    /// </summary>
    public static IReadOnlyList<string> GoToXY(double? x, double? y, MachineProfile m)
    {
        if (x is null && y is null) throw new ArgumentException("Ingen axel vald.");
        var xy = new List<string>();
        if (x is double vx) xy.Add("X" + N(vx));
        if (y is double vy) xy.Add("Y" + N(vy));
        return new[] { $"G53 G0 Z{N(-m.LimitMarginMm)}", "G90 G0 " + string.Join(" ", xy) };
    }

    public static string SelectWcs(int index) => index is >= 54 and <= 59
        ? "G" + index.ToString(CultureInfo.InvariantCulture)
        : throw new ArgumentOutOfRangeException(nameof(index), "G54–G59.");

    /// <summary>Active WCS from a $G reply such as "[GC:G0 G54 G17 G21 G90 G94 M5 M9 T0 F0 S0]".</summary>
    public static string? ParseActiveWcs(string gcLine)
    {
        var m = Regex.Match(gcLine, @"\bG5[4-9]\b");
        return gcLine.StartsWith("[GC:", StringComparison.Ordinal) && m.Success ? m.Value : null;
    }

    private static string N(double v, int decimals = 3)
    {
        var r = Math.Round(v, decimals, MidpointRounding.AwayFromZero);
        if (r == 0) r = 0;
        return r.ToString("0." + new string('#', Math.Max(decimals, 0)), CultureInfo.InvariantCulture).TrimEnd('.');
    }
}
