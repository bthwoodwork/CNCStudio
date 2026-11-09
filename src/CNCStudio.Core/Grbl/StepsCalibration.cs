using System;
using System.Globalization;

namespace CNCStudio.Core.Grbl;

/// <summary>The result of one calibration measurement.</summary>
public sealed record StepsCalibrationResult(int Axis, double OldStepsPerMm, double NewStepsPerMm, double CommandedMm, double MeasuredMm)
{
    /// <summary>How far off the axis was, in % of the commanded distance (+ = moved too far).</summary>
    public double ErrorPercent => (MeasuredMm - CommandedMm) / CommandedMm * 100;
    public int SettingNumber => 100 + Axis;
}

/// <summary>
/// Steps/mm calibration ($100–$102, verified: GRBL settings $100/$101/$102 = X/Y/Z steps/mm): the machine
/// moves a known distance, the real distance is measured, and new = old × commanded ÷ measured. The
/// distances and limits are settings in the window; nothing here is tied to one machine.
/// </summary>
public static class StepsCalibration
{
    /// <summary>A bigger difference than this is almost always a measuring mistake (or a slipping pulley): ask first.</summary>
    public const double SuspiciousPercent = 10;

    public static string AxisName(int axis) => "XYZ"[axis].ToString();

    public static string? Validate(double oldSteps, double commanded, double measured)
    {
        if (!double.IsFinite(oldSteps) || oldSteps <= 0) return "Maskinens nuvarande steg/mm saknas eller är 0.";
        if (!double.IsFinite(commanded) || commanded <= 0) return "Den beordrade sträckan måste vara större än 0.";
        if (!double.IsFinite(measured) || measured <= 0) return "Ange den uppmätta sträckan i mm (större än 0).";
        return null;
    }

    public static StepsCalibrationResult Compute(int axis, double oldSteps, double commanded, double measured)
    {
        if (axis is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(axis));
        if (Validate(oldSteps, commanded, measured) is string error) throw new ArgumentException(error);
        // GRBL stores the value with three decimals ($100=40.000).
        double value = Math.Round(oldSteps * commanded / measured, 3);
        return new StepsCalibrationResult(axis, oldSteps, value, commanded, measured);
    }

    /// <summary>The jog that moves the commanded distance ($J=G91 G21 …, a jog line GRBL can cancel).</summary>
    public static string MoveLine(int axis, double distance, double feed) =>
        GrblMachineCommands.Jog(axis == 0 ? distance : 0, axis == 1 ? distance : 0, axis == 2 ? distance : 0, feed);

    public static string Describe(StepsCalibrationResult r)
    {
        string F(double v, string f = "0.###") => v.ToString(f, CultureInfo.InvariantCulture);
        string sign = r.ErrorPercent >= 0 ? "för långt" : "för kort";
        return $"{AxisName(r.Axis)} gick {F(r.MeasuredMm)} mm i stället för {F(r.CommandedMm)} mm ({F(Math.Abs(r.ErrorPercent), "0.##")} % {sign}). " +
               $"${r.SettingNumber}: {F(r.OldStepsPerMm)} → {F(r.NewStepsPerMm)} steg/mm.";
    }
}
