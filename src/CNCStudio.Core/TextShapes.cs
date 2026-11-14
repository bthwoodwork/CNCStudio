using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace CNCStudio.Core;

public enum TextAlign { Left, Center, Right }

/// <summary>What a text object was made from, so it can be edited again (Rita → Text).</summary>
public sealed class TextSource
{
    public string Content { get; set; } = "";
    public string FontFamily { get; set; } = "Arial";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public TextAlign Align { get; set; } = TextAlign.Left;
    /// <summary>Line spacing as a share of the normal (1 = the font's own).</summary>
    public double LineSpacing { get; set; } = 1;

    public TextSource Clone() => (TextSource)MemberwiseClone();

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Content)) return "Skriv en text.";
        if (Content.Length > 2000) return "Texten är för lång (högst 2000 tecken).";
        if (string.IsNullOrWhiteSpace(FontFamily)) return "Välj ett typsnitt.";
        if (!double.IsFinite(LineSpacing) || LineSpacing < 0.5 || LineSpacing > 3) return "Radavståndet måste vara 0,5–3.";
        return null;
    }
}

/// <summary>
/// Text med typsnitt (like Easel's text tool): the letters' outlines (from the font, made by the Desktop) become a
/// vector shape that can be pocketed or cut out like an imported SVG. Overlapping letters (script fonts, accents)
/// are joined into one region, holes (o, a, e …) are kept, and the outline is scaled so the whole text is the
/// height asked for.
/// </summary>
public static class TextShapes
{
    public const double MinHeightMm = 1, MaxHeightMm = 2000;
    private const int Precision = 4;
    private const double SimplifyMm = 0.01;

    /// <summary>
    /// Outlines in any unit with Y down (as WPF gives them) → a VectorPath shape with its top-left corner at
    /// (<paramref name="x"/>, <paramref name="y"/>) and the given height in mm.
    /// </summary>
    public static DesignShape FromOutlines(IEnumerable<IReadOnlyList<(double X, double Y)>> outlines, double heightMm, double x, double y, TextSource info)
    {
        if (!double.IsFinite(heightMm) || heightMm < MinHeightMm || heightMm > MaxHeightMm)
            throw new ArgumentException($"Texthöjden måste vara {MinHeightMm:0}–{MaxHeightMm:0} mm.");
        var raw = outlines.Where(o => o.Count >= 3 && o.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y))).ToList();
        if (raw.Count == 0) throw new ArgumentException("Texten har inga tecken som kan fräsas (bara mellanslag?).");
        double minX = raw.Min(o => o.Min(p => p.X)), maxX = raw.Max(o => o.Max(p => p.X));
        double minY = raw.Min(o => o.Min(p => p.Y)), maxY = raw.Max(o => o.Max(p => p.Y));
        if (maxY - minY <= 1e-9) throw new ArgumentException("Texten har ingen höjd.");
        double scale = heightMm / (maxY - minY);

        var paths = new PathsD(raw.Select(o => new PathD(o.Select(p => new PointD((p.X - minX) * scale, (p.Y - minY) * scale)))));
        // Font outlines are filled non-zero: overlapping strokes join, counters (holes) wind the other way.
        var region = Clipper.Union(paths, new PathsD(), FillRule.NonZero, Precision);
        region = new PathsD(Clipper.SimplifyPaths(region, SimplifyMm, true).Where(p => p.Count >= 3 && Math.Abs(Clipper.Area(p)) > 1e-6));
        if (region.Count == 0) throw new ArgumentException("Texten blev för liten för att fräsas – öka höjden.");

        double rx0 = region.Min(p => p.Min(v => v.x)), rx1 = region.Max(p => p.Max(v => v.x));
        double ry0 = region.Min(p => p.Min(v => v.y)), ry1 = region.Max(p => p.Max(v => v.y));
        double w = Math.Max(rx1 - rx0, 1e-6), h = Math.Max(ry1 - ry0, 1e-6);
        return new DesignShape
        {
            Kind = ShapeKind.VectorPath, X = x, Y = y, Width = w, Height = h, Text = info.Clone(),
            Contours = region.Select(p => new VectorContour
            {
                Closed = true,
                Points = p.Select(v => new VectorPoint((v.x - rx0) / w, (v.y - ry0) / h)).ToList()
            }).ToList(),
        };
    }
}
