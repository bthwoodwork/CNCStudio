using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace CNCStudio.Core;

public enum FigureKind { RoundedRectangle, Polygon, Star, Heart, Arrow, Ring }

public sealed class FigureOptions
{
    public FigureKind Kind { get; set; } = FigureKind.Star;
    public double WidthMm { get; set; } = 80;
    public double HeightMm { get; set; } = 80;
    /// <summary>Polygon: sides; star: points.</summary>
    public int Count { get; set; } = 5;
    /// <summary>Star: inner radius as % of the outer; ring: the hole as % of the outer size.</summary>
    public double InnerPercent { get; set; } = 45;
    /// <summary>Rounded rectangle: corner radius (mm).</summary>
    public double CornerMm { get; set; } = 8;
}

/// <summary>Figurbank (as in Easel): ready shapes that become vector shapes of the size asked for.</summary>
public static class Figures
{
    public static string Name(FigureKind k) => k switch
    {
        FigureKind.RoundedRectangle => "Rundad rektangel", FigureKind.Polygon => "Polygon", FigureKind.Star => "Stjärna",
        FigureKind.Heart => "Hjärta", FigureKind.Arrow => "Pil", FigureKind.Ring => "Ring", _ => k.ToString(),
    };

    public static string? Validate(FigureOptions o)
    {
        if (!Ok(o.WidthMm, 1, 5000) || !Ok(o.HeightMm, 1, 5000)) return "Bredd och höjd måste vara 1–5000 mm.";
        if (o.Kind == FigureKind.Polygon && (o.Count < 3 || o.Count > 64)) return "En polygon har 3–64 sidor.";
        if (o.Kind == FigureKind.Star && (o.Count < 3 || o.Count > 64)) return "En stjärna har 3–64 spetsar.";
        if (o.Kind is FigureKind.Star or FigureKind.Ring && !Ok(o.InnerPercent, 5, 95)) return "Inre storleken måste vara 5–95 %.";
        if (o.Kind == FigureKind.RoundedRectangle && !Ok(o.CornerMm, 0, Math.Min(o.WidthMm, o.HeightMm) / 2)) return "Hörnradien får vara högst halva den kortaste sidan.";
        return null;
    }

    private static bool Ok(double v, double min, double max) => double.IsFinite(v) && v >= min && v <= max;

    /// <summary>The figure as a vector shape with its top-left at (x, y).</summary>
    public static DesignShape Create(FigureOptions o, double x, double y)
    {
        if (Validate(o) is string problem) throw new ArgumentException(problem);
        var paths = Outline(o);
        double x0 = paths.Min(p => p.Min(v => v.x)), x1 = paths.Max(p => p.Max(v => v.x));
        double y0 = paths.Min(p => p.Min(v => v.y)), y1 = paths.Max(p => p.Max(v => v.y));
        double w = x1 - x0, h = y1 - y0;
        return new DesignShape
        {
            Kind = ShapeKind.VectorPath, X = x, Y = y, Width = o.WidthMm, Height = o.HeightMm,
            Contours = paths.Select(p => new VectorContour { Closed = true, Points = p.Select(v => new VectorPoint((v.x - x0) / w, (v.y - y0) / h)).ToList() }).ToList(),
        };
    }

    /// <summary>The outline in its own units (Y down); it is stretched to the width and height afterwards.</summary>
    public static PathsD Outline(FigureOptions o)
    {
        switch (o.Kind)
        {
            case FigureKind.RoundedRectangle:
            {
                double r = o.CornerMm, w = o.WidthMm, h = o.HeightMm;
                var p = new PathD();
                void Arc(double cx, double cy, double a0)
                {
                    int n = r <= 0 ? 1 : Math.Clamp((int)Math.Ceiling(r * Math.PI / 2 / 0.3), 6, 90);
                    for (int i = 0; i <= n; i++) { double a = a0 + Math.PI / 2 * i / Math.Max(n, 1); p.Add(new PointD(cx + r * Math.Cos(a), cy + r * Math.Sin(a))); }
                }
                Arc(w - r, h - r, 0); Arc(r, h - r, Math.PI / 2); Arc(r, r, Math.PI); Arc(w - r, r, 3 * Math.PI / 2);
                return Clean(new PathsD { p });
            }
            case FigureKind.Polygon:
                return new PathsD { Regular(o.Count, 1, 1, -Math.PI / 2) };
            case FigureKind.Star:
            {
                var p = new PathD();
                for (int i = 0; i < 2 * o.Count; i++)
                {
                    double r = i % 2 == 0 ? 1 : o.InnerPercent / 100, a = -Math.PI / 2 + Math.PI * i / o.Count;
                    p.Add(new PointD(r * Math.Cos(a), r * Math.Sin(a)));
                }
                return new PathsD { p };
            }
            case FigureKind.Heart:
            {
                var p = new PathD();
                for (int i = 0; i < 360; i++)
                {
                    double t = 2 * Math.PI * i / 360;
                    double hx = 16 * Math.Pow(Math.Sin(t), 3), hy = 13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t);
                    p.Add(new PointD(hx, -hy));                     // Y down: the point at the bottom
                }
                return Clean(new PathsD { p });
            }
            case FigureKind.Arrow:
                // Pointing right: shaft half the height, head the last 40 %.
                return new PathsD { new PathD { new(0, 0.25), new(0.6, 0.25), new(0.6, 0), new(1, 0.5), new(0.6, 1), new(0.6, 0.75), new(0, 0.75) } };
            default: // Ring
            {
                var outer = Regular(180, 1, 1, 0);
                var inner = Regular(180, o.InnerPercent / 100, o.InnerPercent / 100, 0);
                inner.Reverse();
                return new PathsD { outer, inner };
            }
        }
    }

    private static PathD Regular(int n, double rx, double ry, double start)
    {
        var p = new PathD();
        for (int i = 0; i < n; i++) { double a = start + 2 * Math.PI * i / n; p.Add(new PointD(rx * Math.Cos(a), ry * Math.Sin(a))); }
        return p;
    }

    private static PathsD Clean(PathsD p) => new(Clipper.SimplifyPaths(p, 1e-4, true).Where(q => q.Count >= 3));
}
