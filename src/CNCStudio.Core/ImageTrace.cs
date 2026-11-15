using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace CNCStudio.Core;

public sealed class TraceOptions
{
    /// <summary>Pixels darker than this (0–255) are the shape.</summary>
    public int Threshold { get; set; } = 128;
    /// <summary>Light parts are the shape instead (white text on black).</summary>
    public bool Invert { get; set; }
    /// <summary>How far (pixels) the outline may move to become smoother and have fewer points.</summary>
    public double SmoothPx { get; set; } = 1.0;
    /// <summary>Specks and holes smaller than this many pixels are left out.</summary>
    public double MinAreaPx { get; set; } = 16;
}

/// <summary>
/// Bild till vektor (as Easel's and UGS's image trace): a picture – a logo, a drawing, a silhouette – becomes outlines
/// that can be pocketed, V-carved or cut out. The image is made black and white at a threshold; each row's runs of
/// "ink" are joined into regions (holes kept), the pixel steps are smoothed away and specks dropped.
/// The Desktop reads the file and gives the grey values; this is the tracing itself.
/// </summary>
public static class ImageTrace
{
    /// <summary>Longest side traced; larger pictures are scaled down first by the Desktop.</summary>
    public const int MaxSidePx = 1200;

    /// <summary>
    /// Traces a grey image (row by row, 0 = black, 255 = white) to closed outlines in pixel coordinates, Y down.
    /// </summary>
    public static PathsD Trace(byte[] gray, int width, int height, TraceOptions o)
    {
        if (width <= 0 || height <= 0 || gray.Length < width * height) throw new ArgumentException("Bilden är tom eller trasig.");
        bool Ink(int x, int y) { byte v = gray[y * width + x]; return o.Invert ? v >= o.Threshold : v < o.Threshold; }

        // Runs of ink per row → rectangles; joined row block by row block so no single union gets huge.
        var all = new PathsD();
        var block = new PathsD();
        for (int y = 0; y < height; y++)
        {
            int x = 0;
            while (x < width)
            {
                while (x < width && !Ink(x, y)) x++;
                if (x >= width) break;
                int start = x;
                while (x < width && Ink(x, y)) x++;
                block.Add(new PathD { new(start, y), new(x, y), new(x, y + 1), new(start, y + 1) });
            }
            if (block.Count > 2000 || y == height - 1)
            {
                all.AddRange(Clipper.Union(block, FillRule.NonZero));
                block.Clear();
            }
        }
        if (all.Count == 0) return new PathsD();
        var region = Clipper.Union(all, FillRule.NonZero);

        // Drop specks (small islands) and pinholes (small holes): islands have positive area, holes negative.
        region = new PathsD(region.Where(p => Math.Abs(Clipper.Area(p)) >= o.MinAreaPx));
        region = Clipper.Union(region, FillRule.NonZero);
        if (o.SmoothPx > 0)
            region = new PathsD(Clipper.SimplifyPaths(region, Math.Max(o.SmoothPx, 0.5), true).Where(p => p.Count >= 3));
        return new PathsD(region.Where(p => Math.Abs(Clipper.Area(p)) >= Math.Max(1, o.MinAreaPx / 4)));
    }

    /// <summary>The traced outlines as one vector shape, <paramref name="widthMm"/> wide, top-left at (x, y).</summary>
    public static DesignShape ToShape(PathsD traced, double widthMm, double x, double y)
    {
        if (traced.Count == 0) throw new ArgumentException("Inget att spåra – ändra tröskeln (eller Invertera).");
        if (!double.IsFinite(widthMm) || widthMm < 1 || widthMm > 5000) throw new ArgumentException("Bredden måste vara 1–5000 mm.");
        double x0 = traced.Min(p => p.Min(v => v.x)), x1 = traced.Max(p => p.Max(v => v.x));
        double y0 = traced.Min(p => p.Min(v => v.y)), y1 = traced.Max(p => p.Max(v => v.y));
        double w = Math.Max(x1 - x0, 1e-6), h = Math.Max(y1 - y0, 1e-6);
        double scale = widthMm / w;
        return new DesignShape
        {
            Kind = ShapeKind.VectorPath, X = x, Y = y, Width = widthMm, Height = h * scale,
            Contours = traced.Select(p => new VectorContour
            {
                Closed = true,
                Points = p.Select(v => new VectorPoint((v.x - x0) / w, (v.y - y0) / h)).ToList()
            }).ToList(),
        };
    }

    public static int PointCount(PathsD traced) => traced.Sum(p => p.Count);
}
