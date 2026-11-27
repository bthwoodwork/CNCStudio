using System;

namespace CNCStudio.Core;

/// <summary>
/// The shape of a cutter's end, as the height of its cutting surface above the tip at a distance from its axis.
/// <list type="bullet">
/// <item>Flat end mill: flat across its diameter.</item>
/// <item>Ball nose: a half sphere with the radius of the tool.</item>
/// <item>V-bit: a cone with the included angle.</item>
/// <item>Tapered ball nose (konisk kulfräs): a ball tip with radius = diameter / 2, then a cone with the included angle that
/// meets the ball tangentially and keeps widening upwards. The cone is what reaches steep walls first, so it must be counted.</item>
/// </list>
/// </summary>
public static class ToolShape
{
    /// <summary>
    /// Height above the tip of the tool's surface at <paramref name="distance"/> from its axis,
    /// or <see cref="double.PositiveInfinity"/> where the tool does not reach (outside its radius).
    /// </summary>
    /// <param name="radius">Half the cutting diameter; for a tapered ball nose the ball's radius.</param>
    /// <param name="angleDeg">Included angle (V-bit, tapered ball nose).</param>
    public static double Height(ToolKind kind, double radius, double angleDeg, double distance)
    {
        double d = Math.Abs(distance);
        switch (kind)
        {
            case ToolKind.BallNose:
                return d > radius + 1e-12 ? double.PositiveInfinity : radius - Math.Sqrt(Math.Max(radius * radius - d * d, 0));
            case ToolKind.VBit:
            {
                double tan = Math.Tan(angleDeg * Math.PI / 360);
                return d > radius + 1e-12 || tan <= 0 ? double.PositiveInfinity : d / tan;
            }
            case ToolKind.TaperedBallNose:
            {
                var (d0, h0, tan) = Taper(radius, angleDeg);
                if (d <= d0) return radius - Math.Sqrt(Math.Max(radius * radius - d * d, 0));
                return h0 + (d - d0) / tan;
            }
            default:
                return d > radius + 1e-12 ? double.PositiveInfinity : 0;
        }
    }

    /// <summary>The tool's radius at <paramref name="height"/> above its tip: how far out it can touch material that high.</summary>
    public static double RadiusAt(ToolKind kind, double radius, double angleDeg, double height)
    {
        double h = Math.Max(height, 0);
        switch (kind)
        {
            case ToolKind.BallNose:
                return h >= radius ? radius : Math.Sqrt(Math.Max(radius * radius - (radius - h) * (radius - h), 0));
            case ToolKind.VBit:
                return Math.Min(radius, h * Math.Tan(angleDeg * Math.PI / 360));
            case ToolKind.TaperedBallNose:
            {
                var (d0, h0, tan) = Taper(radius, angleDeg);
                if (h <= h0) return Math.Sqrt(Math.Max(radius * radius - (radius - h) * (radius - h), 0));
                return d0 + (h - h0) * tan;
            }
            default:
                return radius;
        }
    }

    /// <summary>Where the cone meets the ball: its distance from the axis, its height above the tip, and the cone's tan(half angle).</summary>
    private static (double D0, double H0, double Tan) Taper(double radius, double angleDeg)
    {
        double half = angleDeg * Math.PI / 360;
        return (radius * Math.Cos(half), radius * (1 - Math.Sin(half)), Math.Tan(half));
    }

    /// <summary>A tapered ball nose's included angle must be more than 0 and less than 90 degrees.</summary>
    public static string? ValidateTaper(double angleDeg, string who = "Den koniska kulfräsens") =>
        !double.IsFinite(angleDeg) || angleDeg <= 0 || angleDeg >= 90 ? $"{who} vinkel måste vara större än 0 och mindre än 90 grader (hela spetsvinkeln)." : null;
}
