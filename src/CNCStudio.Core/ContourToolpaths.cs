using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>Non-machine-executable XY centerline preview for elementary closed contours.</summary>
public sealed record CamPoint(double X, double Y);
public sealed record ContourPreview(IReadOnlyList<CamPoint> Centerline, IReadOnlyList<double> PassDepths,
    string Warning)
{
    // An imported or Boolean shape can contain several separate contours and holes.
    // Keep Centerline for existing elementary shapes and expose all contours for rendering.
    public IReadOnlyList<IReadOnlyList<CamPoint>> Centerlines { get; init; } =
        Array.Empty<IReadOnlyList<CamPoint>>();

    /// <summary>True for a pocket: Centerlines are loops in cutting order (inside out).</summary>
    public bool IsPocket { get; init; }
    /// <summary>Pocket only: where the tool centre may go (the pocket shrunk by the tool radius).</summary>
    public IReadOnlyList<IReadOnlyList<CamPoint>> PocketCenterRegion { get; init; } =
        Array.Empty<IReadOnlyList<CamPoint>>();
    /// <summary>Pocket only: uncut area left between the loops (mm²) – should be ~0.</summary>
    public double PocketRestArea { get; init; }
    /// <summary>Pocket only: area in inside corners the round tool cannot reach (mm²).</summary>
    public double PocketCornerArea { get; init; }
}

public static class ContourToolpaths
{
    public static ContourPreview Generate(DesignShape shape, CamOperation operation, double materialThickness)
    {
        var problem = CamValidation.GetError(operation, materialThickness);
        if (problem != null) throw new ArgumentException(problem);
        if (operation.Kind == CamOperationKind.Pocket)
            return PocketToolpaths.Generate(shape, operation, materialThickness);
        if (shape.Kind == ShapeKind.VectorPath)
            throw new NotSupportedException("Konturer med importerade/Boolean-former kräver en robust polygon-offsetmotor. Ej implementerat ännu.");
        if (!double.IsFinite(shape.X) || !double.IsFinite(shape.Y) ||
            !double.IsFinite(shape.Width) || !double.IsFinite(shape.Height) ||
            shape.Width <= 0 || shape.Height <= 0)
            throw new ArgumentException("Ogiltig objektgeometri.");
        double r = operation.ToolDiameter / 2;
        double sign = operation.Kind == CamOperationKind.Outside ? 1 : -1;
        List<CamPoint> points;
        if (shape.Kind == ShapeKind.Rectangle)
        {
            double x0 = shape.X - sign * r, y0 = shape.Y - sign * r;
            double x1 = shape.X + shape.Width + sign * r;
            double y1 = shape.Y + shape.Height + sign * r;
            if (x1 <= x0 || y1 <= y0)
                throw new ArgumentException("Fräsen är för stor för invändig konturfräsning.");
            points = new List<CamPoint> { new(x0,y0),new(x1,y0),new(x1,y1),new(x0,y1),new(x0,y0) };
        }
        else if (shape.Kind == ShapeKind.Ellipse)
        {
            double a = shape.Width / 2, b = shape.Height / 2;
            // The minimum radius of curvature is min(a,b)^2 / max(a,b).
            double minCurvatureRadius = Math.Min(a,b) * Math.Min(a,b) / Math.Max(a,b);
            if (sign < 0 && r >= minCurvatureRadius)
                throw new ArgumentException("Fräsen är för stor för en korrekt invändig ellipsoffset.");
            double cx = shape.X+a, cy = shape.Y+b;
            int count = Math.Clamp((int)Math.Ceiling(Math.PI * (3*(a+b)-Math.Sqrt((3*a+b)*(a+3*b))) / 0.6), 96, 4096);
            points = new List<CamPoint>(count+1);
            for (int i=0; i<=count; i++)
            {
                double angle = 2*Math.PI*i/count;
                double cosine = Math.Cos(angle), sine = Math.Sin(angle);
                double nx = cosine/a, ny = sine/b;
                double length = Math.Sqrt(nx*nx+ny*ny);
                points.Add(new CamPoint(cx+a*cosine+sign*r*nx/length,
                                        cy+b*sine+sign*r*ny/length));
            }
        }
        else throw new NotSupportedException("Objekttypen saknar konturstrategi.");
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            throw new ArgumentException("Ogiltiga beräknade koordinater.");
        var depths = new List<double>();
        for (double d=operation.DepthPerPass; d<operation.TotalDepth-1e-8; d+=operation.DepthPerPass)
            depths.Add(Math.Round(d,6));
        depths.Add(operation.TotalDepth);
        return new ContourPreview(points, depths,
            operation.UseTabs
            ? "Hållflikar är aktiverade men visas INTE i denna geometriförhandsvisning. Ej körbart fräsprogram."
            : "Endast XY-centrumlinje och planerade djupsteg. Inga säkra Z-rörelser eller G-code finns.")
        { Centerlines = new IReadOnlyList<CamPoint>[] { points } };
    }
}
