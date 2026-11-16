using System;
using System.Collections.Generic;

namespace CNCStudio.Core;

/// <summary>
/// Ruler marks for the 3D view: where the ticks go along an edge and which ones get a number. A ruler shows
/// values (mm from a zero) along a line; <see cref="Between"/> gives every tick between two values.
/// </summary>
public static class RulerTicks
{
    /// <param name="Value">The value at the tick, in mm from the ruler's zero.</param>
    /// <param name="Labelled">A long tick with a number.</param>
    /// <param name="Medium">A middle tick (half way between two numbers).</param>
    public readonly record struct Tick(double Value, bool Labelled, bool Medium);

    /// <summary>A number every 10, 20, 25, 50, 100, 200 or 500 mm – the smallest that gives at most <paramref name="maxLabels"/> numbers.</summary>
    public static double LabelStep(double length, int maxLabels = 12)
    {
        foreach (var step in new double[] { 10, 20, 25, 50, 100, 200, 500 })
            if (length / step <= maxLabels) return step;
        return 1000;
    }

    /// <summary>Small ticks between the numbers: a fifth of the label step (10 mm from 50 mm up, 2/4/5 mm below).</summary>
    public static double MinorStep(double labelStep) => labelStep >= 50 ? 10 : labelStep / 5;

    /// <summary>Every tick from <paramref name="from"/> to <paramref name="to"/> (either order), inclusive.</summary>
    public static List<Tick> Between(double from, double to, double labelStep)
    {
        double v0 = Math.Min(from, to), v1 = Math.Max(from, to), minor = MinorStep(labelStep);
        var ticks = new List<Tick>();
        if (!(minor > 0) || (v1 - v0) / minor > 5000) return ticks;
        for (double v = Math.Ceiling(v0 / minor - 1e-9) * minor; v <= v1 + 1e-9; v += minor)
        {
            double r = Math.Round(v, 6);
            bool labelled = At(r, labelStep);
            ticks.Add(new Tick(r == 0 ? 0 : r, labelled, !labelled && At(r, labelStep / 2) && labelStep / 2 >= minor));
        }
        return ticks;
    }

    private static bool At(double v, double step) => Math.Abs(v / step - Math.Round(v / step)) < 1e-6;
}
