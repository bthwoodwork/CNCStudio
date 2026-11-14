using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>The letters' outlines from a Windows font (WPF FormattedText), flattened to polylines, Y down.</summary>
internal static class TextOutlines
{
    private const double EmSize = 100;          // font units; scaled to the height in mm by TextShapes
    private const double Flatten = 0.02;        // of EmSize: 0.2 µm per mm of font size – smooth curves

    public static bool FontInstalled(string family) =>
        Fonts.SystemFontFamilies.Any(f => string.Equals(f.Source, family, StringComparison.OrdinalIgnoreCase));

    public static Geometry Geometry(TextSource t)
    {
        var family = new FontFamily(t.FontFamily);
        var typeface = new Typeface(family, t.Italic ? FontStyles.Italic : FontStyles.Normal,
            t.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(t.Content.Replace("\r\n", "\n"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, EmSize, Brushes.Black, 1.0)
        {
            TextAlignment = t.Align switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left },
            LineHeight = EmSize * family.LineSpacing * t.LineSpacing,
        };
        return text.BuildGeometry(new Point(0, 0));
    }

    public static List<IReadOnlyList<(double X, double Y)>> Polylines(Geometry geometry)
    {
        var flat = geometry.GetFlattenedPathGeometry(Flatten, ToleranceType.Absolute);
        var result = new List<IReadOnlyList<(double X, double Y)>>();
        foreach (var fig in flat.Figures)
        {
            var pts = new List<(double X, double Y)> { (fig.StartPoint.X, fig.StartPoint.Y) };
            foreach (var seg in fig.Segments)
            {
                if (seg is LineSegment line) pts.Add((line.Point.X, line.Point.Y));
                else if (seg is PolyLineSegment poly) pts.AddRange(poly.Points.Select(p => (p.X, p.Y)));
            }
            if (pts.Count > 1 && Math.Abs(pts[0].X - pts[^1].X) < 1e-9 && Math.Abs(pts[0].Y - pts[^1].Y) < 1e-9) pts.RemoveAt(pts.Count - 1);
            if (pts.Count >= 3) result.Add(pts);
        }
        return result;
    }

    /// <summary>The text as a shape with its top-left corner at (x, y) and the given height in mm.</summary>
    public static DesignShape Shape(TextSource t, double heightMm, double x, double y) =>
        TextShapes.FromOutlines(Polylines(Geometry(t)), heightMm, x, y, t);
}
