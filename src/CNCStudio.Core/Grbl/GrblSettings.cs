using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CNCStudio.Core.Grbl;

public enum GrblSettingKind { Number, Integer, Boolean, Mask }

public sealed record GrblSettingInfo(int Number, string Name, string Unit, GrblSettingKind Kind);

/// <summary>
/// GRBL 1.1 settings, error and alarm texts. Numbers, names and units are taken verbatim from
/// gnea/grbl doc/csv/setting_codes_en_US.csv, error_codes_en_US.csv and alarm_codes_en_US.csv.
/// </summary>
public static class GrblSettings
{
    public static readonly IReadOnlyDictionary<int, GrblSettingInfo> Catalog = new[]
    {
        new GrblSettingInfo(0, "Step pulse time", "µs", GrblSettingKind.Integer),
        new GrblSettingInfo(1, "Step idle delay", "ms", GrblSettingKind.Integer),
        new GrblSettingInfo(2, "Step pulse invert", "mask", GrblSettingKind.Mask),
        new GrblSettingInfo(3, "Step direction invert", "mask", GrblSettingKind.Mask),
        new GrblSettingInfo(4, "Invert step enable pin", "boolean", GrblSettingKind.Boolean),
        new GrblSettingInfo(5, "Invert limit pins", "boolean", GrblSettingKind.Boolean),
        new GrblSettingInfo(6, "Invert probe pin", "boolean", GrblSettingKind.Boolean),
        new GrblSettingInfo(10, "Status report options", "mask", GrblSettingKind.Mask),
        new GrblSettingInfo(11, "Junction deviation", "mm", GrblSettingKind.Number),
        new GrblSettingInfo(12, "Arc tolerance", "mm", GrblSettingKind.Number),
        new GrblSettingInfo(13, "Report in inches", "boolean", GrblSettingKind.Boolean),
        new GrblSettingInfo(20, "Soft limits enable", "boolean", GrblSettingKind.Boolean),
        new GrblSettingInfo(21, "Hard limits enable", "boolean", GrblSettingKind.Boolean),
        new GrblSettingInfo(22, "Homing cycle enable", "boolean", GrblSettingKind.Boolean),
        new GrblSettingInfo(23, "Homing direction invert", "mask", GrblSettingKind.Mask),
        new GrblSettingInfo(24, "Homing locate feed rate", "mm/min", GrblSettingKind.Number),
        new GrblSettingInfo(25, "Homing search seek rate", "mm/min", GrblSettingKind.Number),
        new GrblSettingInfo(26, "Homing switch debounce delay", "ms", GrblSettingKind.Integer),
        new GrblSettingInfo(27, "Homing switch pull-off distance", "mm", GrblSettingKind.Number),
        new GrblSettingInfo(30, "Maximum spindle speed", "RPM", GrblSettingKind.Number),
        new GrblSettingInfo(31, "Minimum spindle speed", "RPM", GrblSettingKind.Number),
        new GrblSettingInfo(32, "Laser-mode enable", "boolean", GrblSettingKind.Boolean),
        new GrblSettingInfo(100, "X-axis travel resolution", "steg/mm", GrblSettingKind.Number),
        new GrblSettingInfo(101, "Y-axis travel resolution", "steg/mm", GrblSettingKind.Number),
        new GrblSettingInfo(102, "Z-axis travel resolution", "steg/mm", GrblSettingKind.Number),
        new GrblSettingInfo(110, "X-axis maximum rate", "mm/min", GrblSettingKind.Number),
        new GrblSettingInfo(111, "Y-axis maximum rate", "mm/min", GrblSettingKind.Number),
        new GrblSettingInfo(112, "Z-axis maximum rate", "mm/min", GrblSettingKind.Number),
        new GrblSettingInfo(120, "X-axis acceleration", "mm/s²", GrblSettingKind.Number),
        new GrblSettingInfo(121, "Y-axis acceleration", "mm/s²", GrblSettingKind.Number),
        new GrblSettingInfo(122, "Z-axis acceleration", "mm/s²", GrblSettingKind.Number),
        new GrblSettingInfo(130, "X-axis maximum travel", "mm", GrblSettingKind.Number),
        new GrblSettingInfo(131, "Y-axis maximum travel", "mm", GrblSettingKind.Number),
        new GrblSettingInfo(132, "Z-axis maximum travel", "mm", GrblSettingKind.Number),
    }.ToDictionary(s => s.Number);

    public static readonly IReadOnlyDictionary<int, string> Errors = new Dictionary<int, string>
    {
        [1] = "Expected command letter", [2] = "Bad number format", [3] = "Invalid statement",
        [4] = "Value < 0", [5] = "Setting disabled (homing not enabled)", [6] = "Value < 3 usec",
        [7] = "EEPROM read fail. Using defaults", [8] = "Not idle", [9] = "G-code lock",
        [10] = "Homing not enabled", [11] = "Line overflow", [12] = "Step rate > 30kHz",
        [13] = "Check Door", [14] = "Line length exceeded", [15] = "Travel exceeded",
        [16] = "Invalid jog command", [17] = "Setting disabled (laser mode requires PWM)",
        [20] = "Unsupported command", [21] = "Modal group violation", [22] = "Undefined feed rate",
        [23] = "Invalid gcode ID:23", [24] = "Invalid gcode ID:24", [25] = "Invalid gcode ID:25",
        [26] = "Invalid gcode ID:26", [27] = "Invalid gcode ID:27", [28] = "Invalid gcode ID:28",
        [29] = "Invalid gcode ID:29", [30] = "Invalid gcode ID:30", [31] = "Invalid gcode ID:31",
        [32] = "Invalid gcode ID:32", [33] = "Invalid gcode ID:33", [34] = "Invalid gcode ID:34",
        [35] = "Invalid gcode ID:35", [36] = "Invalid gcode ID:36", [37] = "Invalid gcode ID:37",
        [38] = "Invalid gcode ID:38",
    };

    public static readonly IReadOnlyDictionary<int, string> Alarms = new Dictionary<int, string>
    {
        [1] = "Hard limit", [2] = "Soft limit", [3] = "Abort during cycle",
        [4] = "Probe fail (initial state)", [5] = "Probe fail (no contact)",
        [6] = "Homing fail (reset during homing)", [7] = "Homing fail (door opened)",
        [8] = "Homing fail (pull-off did not clear switch)",
        [9] = "Homing fail (switch not found within search distance)",
        [10] = "Homing fail (second dual-axis switch)",
    };

    public static string Describe(GrblLine line) => line.Kind switch
    {
        GrblLineKind.Error when line.Code is int c => $"{line.Raw} – {(Errors.TryGetValue(c, out var e) ? e : "okänt fel")}",
        GrblLineKind.Alarm when line.Code is int c => $"{line.Raw} – {(Alarms.TryGetValue(c, out var a) ? a : "okänt larm")}",
        _ => line.Raw
    };

    /// <summary>One difference between the machine profile and the controller's $$ values.</summary>
    public sealed record ProfileDifference(int Setting, string What, string ProfileValue, string MachineValue);

    /// <summary>The $$ values the machine profile corresponds to (verified meanings).</summary>
    public static IReadOnlyList<ProfileDifference> CompareWithProfile(MachineProfile p, IReadOnlyDictionary<int, double> s)
    {
        var list = new List<ProfileDifference>();
        void Num(int n, string what, double profile)
        {
            if (s.TryGetValue(n, out var v) && Math.Abs(v - profile) > 0.0005)
                list.Add(new ProfileDifference(n, what, F(profile), F(v)));
        }
        void Int(int n, string what, int profile)
        {
            if (s.TryGetValue(n, out var v) && (int)v != profile)
                list.Add(new ProfileDifference(n, what, profile.ToString(CultureInfo.InvariantCulture), F(v)));
        }
        Int(3, "Axelriktning (mask)", p.DirInvertMask);
        Int(20, "Mjuka gränser", p.SoftLimits ? 1 : 0);
        Int(22, "Homing aktiverat", p.HomeOnStartup ? 1 : 0);
        Int(23, "Homingriktning (mask)", p.HomingDirInvertMask);
        Num(27, "Pull-off", p.HomingPullOffMm);
        Num(130, "X-rörelse", p.WorkWidthMm);
        Num(131, "Y-rörelse", p.WorkLengthMm);
        Num(132, "Z-rörelse", p.ZTravelMm);
        return list;
    }

    /// <summary>The travel settings ($130–$132) that may be written from the profile to the machine.</summary>
    public static readonly int[] TravelSettings = { 130, 131, 132 };

    /// <summary>
    /// The other direction: the $130–$132 writes that make the machine's travel match the profile's work
    /// area (e.g. a demo machine left at 750 × 750 when the real one is 500 × 500). Only travel is written –
    /// homing direction and pull-off decide where the machine drives and are left to the settings window.
    /// </summary>
    public static IReadOnlyList<(int Number, double Value)> TravelWritesFromProfile(MachineProfile p, IReadOnlyDictionary<int, double> s)
    {
        var list = new List<(int, double)>();
        foreach (var (n, v) in new[] { (130, p.WorkWidthMm), (131, p.WorkLengthMm), (132, p.ZTravelMm) })
            if (s.TryGetValue(n, out var current) && Math.Abs(current - v) > 0.0005) list.Add((n, v));
        return list;
    }

    /// <summary>
    /// A copy of the profile updated with the controller's values. Values that are missing from
    /// $$ keep the profile's value.
    /// </summary>
    public static MachineProfile ApplyToProfile(MachineProfile p, IReadOnlyDictionary<int, double> s)
    {
        double Get(int n, double fallback) => s.TryGetValue(n, out var v) ? v : fallback;
        return new MachineProfile
        {
            Name = p.Name,
            DirInvertMask = (int)Get(3, p.DirInvertMask),
            SoftLimits = Get(20, p.SoftLimits ? 1 : 0) == 1,
            HomeOnStartup = Get(22, p.HomeOnStartup ? 1 : 0) == 1,
            HomingDirInvertMask = (int)Get(23, p.HomingDirInvertMask),
            HomingPullOffMm = Get(27, p.HomingPullOffMm),
            WorkWidthMm = Get(130, p.WorkWidthMm),
            WorkLengthMm = Get(131, p.WorkLengthMm),
            ZTravelMm = Get(132, p.ZTravelMm),
            LimitMarginMm = Math.Max(p.LimitMarginMm, Get(27, p.HomingPullOffMm)),
            MaxRateX = s.TryGetValue(110, out var rx) ? rx : p.MaxRateX,
            MaxRateY = s.TryGetValue(111, out var ry) ? ry : p.MaxRateY,
            MaxRateZ = s.TryGetValue(112, out var rz) ? rz : p.MaxRateZ,
            Spindle = p.Spindle, // not a GRBL setting: keep the user's choice
            CalibratedToGrbl = true,
            Id = p.Id, Port = p.Port   // still the same machine
        };
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
