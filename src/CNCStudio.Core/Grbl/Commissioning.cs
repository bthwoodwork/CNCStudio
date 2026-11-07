using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CNCStudio.Core.Grbl;

public enum CheckLevel { Ok, Info, Warning, Stop }

public sealed record SettingCheck(CheckLevel Level, string Text);

/// <summary>
/// Pure checks for the step-by-step first test against a real machine. Every rule refers to a
/// verified fact in docs/GRBL-verifierat.md; nothing here sends anything.
/// </summary>
public static class Commissioning
{
    /// <summary>Reviews the machine's $$ before anything moves. "Stop" means: do not continue.</summary>
    public static IReadOnlyList<SettingCheck> ReviewSettings(IReadOnlyDictionary<int, double> s, MachineProfile profile)
    {
        var list = new List<SettingCheck>();
        double Get(int n) => s.TryGetValue(n, out var v) ? v : double.NaN;
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        if (Get(13) == 1) list.Add(new(CheckLevel.Stop, "$13=1: maskinen rapporterar i tum. CNC Studio räknar i mm – sätt $13=0 först."));
        else list.Add(new(CheckLevel.Ok, "$13=0: millimeter."));
        if (Get(32) == 1) list.Add(new(CheckLevel.Stop, "$32=1: laserläge är på. Det måste vara av ($32=0) för fräsning (GRBL-wikin)."));
        else list.Add(new(CheckLevel.Ok, "$32=0: laserläge av."));
        if (Get(22) != 1) list.Add(new(CheckLevel.Stop, "$22=0: homing är avstängt. CNC Studio kräver homing ($22=1) – annars är Z-brytarens läge okänt."));
        else list.Add(new(CheckLevel.Ok, "$22=1: homing på. Maskinen startar låst (larm) tills $H körts."));
        int home = (int)(double.IsNaN(Get(23)) ? 0 : Get(23));
        if ((home & 4) != 0) list.Add(new(CheckLevel.Stop, $"$23={home}: Z homar nedåt. Z-brytaren sitter upptill – Z-biten (4) måste vara 0."));
        else list.Add(new(CheckLevel.Ok, $"$23={home}: Z homar uppåt; X/Y-brytare {CornerText(home)}."));
        if (home != (profile.HomingDirInvertMask & 7))
            list.Add(new(CheckLevel.Warning, $"Projektets profil säger $23={profile.HomingDirInvertMask} ({CornerText(profile.HomingDirInvertMask)}), maskinen har $23={home}. Använd maskinens värden i profilen."));
        if (!double.IsNaN(Get(3)) && (int)Get(3) != profile.DirInvertMask)
            list.Add(new(CheckLevel.Info, $"$3={F(Get(3))} i maskinen, profilen har {profile.DirInvertMask}. Riktningen testas i nästa steg."));
        list.Add(Get(21) == 1
            ? new(CheckLevel.Ok, "$21=1: hårda gränser på – en brytare stoppar maskinen direkt.")
            : new(CheckLevel.Warning, "$21=0: hårda gränser av. Brytarna stoppar bara vid homing."));
        list.Add(Get(20) == 1
            ? new(CheckLevel.Ok, "$20=1: mjuka gränser på.")
            : new(CheckLevel.Info, "$20=0: mjuka gränser av. Kan sättas efter homing med \"Arbetsområde hit…\"."));
        double pull = Get(27);
        if (!double.IsNaN(pull) && pull > profile.LimitMarginMm)
            list.Add(new(CheckLevel.Warning, $"$27={F(pull)} mm pull-off är större än profilens marginal ({F(profile.LimitMarginMm)} mm)."));
        for (int i = 0; i < 3; i++)
        {
            double t = Get(130 + i), want = new[] { profile.WorkWidthMm, profile.WorkLengthMm, profile.ZTravelMm }[i];
            string axis = "XYZ"[i].ToString();
            if (double.IsNaN(t) || t <= 0) list.Add(new(CheckLevel.Stop, $"${130 + i} ({axis}-rörelse) saknas eller är 0."));
            else if (Math.Abs(t - want) > 0.5)
                list.Add(new(CheckLevel.Info, $"${130 + i}={F(t)} mm i maskinen, profilen har {F(want)} mm."));
            double rate = Get(110 + i);
            if (double.IsNaN(rate) || rate <= 0) list.Add(new(CheckLevel.Stop, $"${110 + i} ({axis} maxhastighet) saknas eller är 0."));
        }
        return list;
    }

    public static string CornerText(int homingMask) =>
        ((homingMask & 2) != 0 ? "fram" : "bak") + " " + ((homingMask & 1) != 0 ? "vänster" : "höger");

    /// <summary>$3 with the bit for one axis flipped (X=1, Y=2, Z=4 – verified, GRBL wiki).</summary>
    public static int FlipDirection(int dirMask, int axis) => dirMask ^ (1 << axis);

    /// <summary>Expected MPos after $H (limits.c): homed toward plus → −$27, toward minus → −$13x + $27.</summary>
    public static double[] ExpectedHomePosition(IReadOnlyDictionary<int, double> s)
    {
        int mask = (int)s.GetValueOrDefault(23);
        double pull = s.GetValueOrDefault(27);
        return Enumerable.Range(0, 3)
            .Select(i => (mask & (1 << i)) != 0 ? -s.GetValueOrDefault(130 + i) + pull : -pull).ToArray();
    }

    /// <summary>
    /// Where the tool stands, in mm from the X and Y switches (the home corner), from MPos and the
    /// machine's own $23/$130/$131 – independent of any project profile. Machine space is −$13x…0
    /// (limits.c): a plus-homed switch sits at MPos 0, a minus-homed one at −$13x.
    /// </summary>
    public static (double X, double Y) DistanceFromSwitches(double[] mpos, IReadOnlyDictionary<int, double> s)
    {
        int mask = (int)s.GetValueOrDefault(23);
        double D(int i) => (mask & (1 << i)) != 0 ? mpos[i] + s.GetValueOrDefault(130 + i) : -mpos[i];
        return (D(0), D(1));
    }

    /// <summary>
    /// An air run: a square drawn in the air, starting where the machine stands after homing and going
    /// away from the switches (inward). Z first goes near the top in machine coordinates (G53 G0), all
    /// XY moves are incremental (G91) at a slow feed, and the program returns to its start and to G90.
    /// </summary>
    public static IReadOnlyList<string> AirSquare(IReadOnlyDictionary<int, double> s, double size, double feed, double margin)
    {
        int mask = (int)s.GetValueOrDefault(23);
        double sx = (mask & 1) != 0 ? 1 : -1, sy = (mask & 2) != 0 ? 1 : -1;   // inward from the switch corner
        string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        return new[]
        {
            "(Luftkorning: kvadrat i luften, ingen fras)",
            "G21", "G90", "G94",
            $"G53 G0 Z{N(-margin)}",
            "G91",
            $"G1 X{N(sx * size)} F{N(feed)}",
            $"G1 Y{N(sy * size)}",
            $"G1 X{N(-sx * size)}",
            $"G1 Y{N(-sy * size)}",
            "G90",
        };
    }
}
