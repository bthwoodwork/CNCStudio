using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Safe, non-executable XY tool-centerline preview for closed imported/Boolean contours.
/// Uses filled-region offsets in WPF so disjoint islands and even-odd holes are supported.
/// This is NOT a CNC postprocessor or validated manufacturing toolpath.
/// </summary>
internal static class VectorContourPreview
{
    private const double FlattenToleranceMm = 0.05;
    private const int MaxPreviewPoints = 100000;

    public static ContourPreview Generate(DesignShape shape, CamOperation operation, double materialThickness)
    {
        if (shape.Kind != ShapeKind.VectorPath)
            return ContourToolpaths.Generate(shape, operation, materialThickness);

        var error = CamValidation.GetError(operation, materialThickness);
        if (error is not null) throw new ArgumentException(error);
        if (operation.Kind == CamOperationKind.Pocket)
            throw new NotSupportedException("Fickfräsning har ännu ingen beräknad verktygsbana.");
        if (shape.Contours is null || shape.Contours.Count == 0 ||
            shape.Contours.Any(c => !c.Closed || c.Points.Count < 3))
            throw new ArgumentException("Konturfräsning kräver slutna konturer med minst tre punkter.");
        if (!double.IsFinite(shape.X) || !double.IsFinite(shape.Y) ||
            !double.IsFinite(shape.Width) || !double.IsFinite(shape.Height) ||
            shape.Width <= 0 || shape.Height <= 0 ||
            shape.Contours.SelectMany(c => c.Points).Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            throw new ArgumentException("Objektet har ogiltiga mått eller konturpunkter.");

        // EvenOdd: nested contours represent holes regardless of their winding direction.
        var source = new PathGeometry { FillRule = FillRule.EvenOdd };
        foreach (var contour in shape.Contours)
        {
            var start = contour.Points[0];
            var fig = new PathFigure
            {
                StartPoint = new Point(shape.X + start.X * shape.Width,
                                       shape.Y + start.Y * shape.Height),
                IsClosed = true,
                IsFilled = true
            };
            foreach (var point in contour.Points.Skip(1))
                fig.Segments.Add(new LineSegment(
                    new Point(shape.X + point.X * shape.Width,
                              shape.Y + point.Y * shape.Height), true));
            source.Figures.Add(fig);
        }
        if (source.GetArea() < 0.000001)
            throw new ArgumentException("Konturen har ingen giltig fylld yta. Kontrollera självskärningar och dubletter.");

        // The widened boundary spans half the tool diameter to either side.
        // Union with the source = dilated region (Outside).
        // Exclude widened boundary from the source = eroded region (Inside).
        // The boundary of the resulting filled region is the preview centerline.
        var pen = new Pen(Brushes.Black, operation.ToolDiameter) { LineJoin = PenLineJoin.Round };
        var widened = source.GetWidenedPathGeometry(pen, FlattenToleranceMm, ToleranceType.Absolute);
        var mode = operation.Kind == CamOperationKind.Outside
            ? GeometryCombineMode.Union : GeometryCombineMode.Exclude;
        var offsetRegion = Geometry.Combine(source, widened, mode, null,
            FlattenToleranceMm, ToleranceType.Absolute);
        if (offsetRegion.IsEmpty() || offsetRegion.GetArea() < 0.000001)
            throw new ArgumentException("Ingen verktygsbana återstår. Fräsen kan vara för stor för invändig bearbetning.");

        var flattened = offsetRegion.GetFlattenedPathGeometry(FlattenToleranceMm, ToleranceType.Absolute);
        var paths = new List<IReadOnlyList<CamPoint>>();
        int total = 0;
        foreach (var figure in flattened.Figures)
        {
            var points = new List<CamPoint>
            {
                new(figure.StartPoint.X, figure.StartPoint.Y)
            };
            foreach (var segment in figure.Segments)
            {
                if (segment is PolyLineSegment many)
                    points.AddRange(many.Points.Select(p => new CamPoint(p.X, p.Y)));
                else if (segment is LineSegment line)
                    points.Add(new CamPoint(line.Point.X, line.Point.Y));
                else
                    throw new NotSupportedException("Oförväntat kurvsegment i verktygsbanan.");
            }
            if (points.Count > 1 && Distance(points[0], points[^1]) < 0.00001)
                points.RemoveAt(points.Count - 1);
            if (points.Count < 3) continue;
            if (!figure.IsClosed && Distance(points[0], points[^1]) > 0.001)
                throw new ArgumentException("En beräknad offsetkontur är öppen. Granska vektorgeometrin.");
            double twiceArea = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Count];
                twiceArea += a.X * b.Y - b.X * a.Y;
            }
            if (Math.Abs(twiceArea) < 0.000002) continue;
            if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
                throw new ArgumentException("Offsetberäkningen gav ogiltiga koordinater.");
            total += points.Count;
            if (total > MaxPreviewPoints)
                throw new NotSupportedException("För komplex kontur för denna förhandsvisning (över 100 000 punkter).");
            points.Add(points[0]); // closed preview for each island/hole
            paths.Add(points);
        }
        if (paths.Count == 0)
            throw new ArgumentException("Det gick inte att beräkna en sluten fräscentrumbana.");

        var depths = new List<double>();
        for (double d = operation.DepthPerPass; d < operation.TotalDepth - 1e-8; d += operation.DepthPerPass)
            depths.Add(Math.Round(d, 6));
        depths.Add(operation.TotalDepth);
        string warning = $"XY-förhandsvisning av {paths.Count} sluten bana/banor (inklusive eventuella hål/öar). " +
            "Offsetkurvor approximeras med 0,05 mm tolerans. Kontrollera importerad geometri. " +
            (operation.UseTabs ? "Hållflikar är aktiverade men tillämpas INTE. " : "") +
            "Ingen ordningsplanering, kollisionstest, säker Z-rörelse eller G-code finns. EJ KÖRBART.";
        return new ContourPreview(paths[0], depths, warning) { Centerlines = paths };
    }

    private static double Distance(CamPoint a, CamPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
