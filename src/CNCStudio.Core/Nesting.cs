using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace CNCStudio.Core;

public sealed class NestOptions
{
    /// <summary>Free space between parts (mm) – at least the cutter's diameter, so the cut between them fits.</summary>
    public double SpacingMm { get; set; } = 8;
    /// <summary>Free space to the material's edges (mm).</summary>
    public double MarginMm { get; set; } = 5;
    /// <summary>Parts may be turned 90°, 180° or 270° to fit better.</summary>
    public bool AllowRotation { get; set; } = true;
}

public sealed record NestResult(int Placed, IReadOnlyList<string> NotPlaced, double UsedLengthMm);

/// <summary>
/// Nesting (as in Easel and UGS): the parts are packed tightly on the material by their real shape, not just their
/// boxes – largest first, each put as far to the back-left as it goes (and turned when allowed), then pushed up and
/// left as far as it slides. A group is one part. Text that is turned is no longer editable as text (its shape stays).
/// </summary>
public static class Nesting
{
    private const int Precision = 3;

    public static NestResult Nest(IReadOnlyList<DesignShape> shapes, double materialW, double materialH, NestOptions o)
    {
        if (!double.IsFinite(o.SpacingMm) || o.SpacingMm < 0 || o.SpacingMm > 200) throw new ArgumentException("Avståndet mellan delarna måste vara 0–200 mm.");
        if (!double.IsFinite(o.MarginMm) || o.MarginMm < 0 || o.MarginMm > 200) throw new ArgumentException("Kantavståndet måste vara 0–200 mm.");
        var pieces = Arrange.Pieces(shapes)
            .Select(p => (Piece: p, Area: Math.Abs(Clipper.Area(Region(p)))))
            .OrderByDescending(p => { var b = Arrange.Of(p.Piece); return b.W * b.H; }).ToList();
        var placed = new List<(PathsD Grown, Arrange.Bounds B)>();
        var failed = new List<string>();
        int count = 0;
        double x0 = o.MarginMm, y0 = o.MarginMm, x1 = materialW - o.MarginMm, y1 = materialH - o.MarginMm;

        foreach (var (piece, _) in pieces)
        {
            (int Quarter, double X, double Y, double Score)? best = null;
            foreach (int q in o.AllowRotation ? new[] { 0, 1, 2, 3 } : new[] { 0 })
            {
                var region = Rotated(Region(piece), q);
                var rb = BoundsOf(region);
                double w = rb.W, h = rb.H;
                if (w > x1 - x0 + 1e-9 || h > y1 - y0 + 1e-9) continue;
                // The region with its top-left at the origin; grown by the spacing for the overlap test.
                var local = Translate(region, -rb.X0, -rb.Y0);
                var grown = Clipper.InflatePaths(local, o.SpacingMm, JoinType.Round, EndType.Polygon, 2, Precision, 0.1);
                bool Fits(double x, double y)
                {
                    if (x < x0 - 1e-9 || y < y0 - 1e-9 || x + w > x1 + 1e-9 || y + h > y1 + 1e-9) return false;
                    var me = new Arrange.Bounds(x - o.SpacingMm, y - o.SpacingMm, x + w + o.SpacingMm, y + h + o.SpacingMm);
                    var moved = (PathsD?)null;
                    foreach (var (other, ob) in placed)
                    {
                        if (ob.X1 <= me.X0 || ob.X0 >= me.X1 || ob.Y1 <= me.Y0 || ob.Y0 >= me.Y1) continue;
                        moved ??= Translate(grown, x, y);
                        if (Math.Abs(Clipper.Area(Clipper.Intersect(moved, other, FillRule.NonZero, Precision))) > 1e-3) return false;
                    }
                    return true;
                }
                // Candidate corners: the material's, and beside/below every part already placed.
                // Also the far edges: a part can then slide back into a gap (two triangles making a rectangle).
                var xs = new List<double> { x0, x1 - w }; var ys = new List<double> { y0, y1 - h };
                foreach (var (_, b) in placed) { xs.Add(b.X1 + o.SpacingMm); xs.Add(b.X0); ys.Add(b.Y1 + o.SpacingMm); ys.Add(b.Y0); }
                foreach (double y in ys.Distinct().OrderBy(v => v))
                    foreach (double x in xs.Distinct().OrderBy(v => v))
                    {
                        if (!Fits(x, y)) continue;
                        var (sx, sy) = Slide(x, y, Fits, x0, y0);
                        double score = (sy + h) * (x1 - x0) + (sx + w);      // back first, then left
                        if (best is null || score < best.Value.Score - 1e-9) best = (q, sx, sy, score);
                        break;                                                  // the leftmost fit on this row is enough
                    }
            }
            if (best is not { } b2)
            {
                failed.Add(PieceName(piece));
                continue;
            }
            Place(piece, b2.Quarter, b2.X, b2.Y);
            var final = Region(piece);
            placed.Add((final, BoundsOf(final)));
            count++;
        }
        double used = placed.Count == 0 ? 0 : placed.Max(p => p.B.Y1) + o.MarginMm;
        return new NestResult(count, failed, used);
    }

    /// <summary>Pushes the part up, then left, as far as it still fits (in halving steps).</summary>
    private static (double X, double Y) Slide(double x, double y, Func<double, double, bool> fits, double minX, double minY)
    {
        for (int round = 0; round < 2; round++)
        {
            y = Push(y, minY, v => fits(x, v));
            x = Push(x, minX, v => fits(v, y));
        }
        return (x, y);
    }

    private static double Push(double at, double min, Func<double, bool> fits)
    {
        double step = (at - min) / 2;
        while (step > 0.05)
        {
            if (at - step >= min - 1e-9 && fits(at - step)) at -= step;
            else step /= 2;
        }
        return at;
    }

    /// <summary>Turns a part (a shape or a group) a quarter turn <paramref name="quarter"/> times clockwise, then puts its top-left at (x, y).</summary>
    public static void Place(List<DesignShape> piece, int quarter, double x, double y)
    {
        var b = Arrange.Of(piece);
        foreach (var s in piece)
        {
            if (quarter % 4 != 0) Turn(s, b, quarter);
        }
        var nb = Arrange.Of(piece);
        foreach (var s in piece) { s.X += x - nb.X0; s.Y += y - nb.Y0; }
    }

    /// <summary>A quarter turn clockwise (as seen on the screen, Y down) about the box <paramref name="around"/>.</summary>
    private static void Turn(DesignShape s, Arrange.Bounds around, int quarter)
    {
        for (int k = 0; k < quarter % 4; k++)
        {
            // Clockwise with Y down: (x, y) → (H − y, x) relative to the box's corner.
            double ax = around.X0, ay = around.Y0, H = k % 2 == 0 ? around.H : around.W;
            double nx = ax + (H - (s.Y + s.Height - ay)), ny = ay + (s.X - ax);
            if (s.Kind == ShapeKind.VectorPath)
                foreach (var c in s.Contours) foreach (var p in c.Points) { double u = p.X, v = p.Y; p.X = 1 - v; p.Y = u; }
            (s.Width, s.Height) = (s.Height, s.Width);
            s.X = nx; s.Y = ny;
        }
        if (quarter % 4 != 0) s.Text = null;          // a turned text is a shape now (the words cannot be re-made turned)
    }

    private static PathsD Region(IEnumerable<DesignShape> piece)
    {
        var all = new PathsD();
        foreach (var s in piece) all.AddRange(PocketToolpaths.Region(s));
        return Clipper.Union(all, FillRule.NonZero);
    }

    private static PathsD Rotated(PathsD r, int quarter)
    {
        var res = r;
        for (int k = 0; k < quarter; k++) res = new PathsD(res.Select(p => new PathD(p.Select(v => new PointD(-v.y, v.x)))));
        return res;
    }

    private static PathsD Translate(PathsD r, double dx, double dy) => new(r.Select(p => new PathD(p.Select(v => new PointD(v.x + dx, v.y + dy)))));

    private static Arrange.Bounds BoundsOf(PathsD r) =>
        new(r.Min(p => p.Min(v => v.x)), r.Min(p => p.Min(v => v.y)), r.Max(p => p.Max(v => v.x)), r.Max(p => p.Max(v => v.y)));

    private static string PieceName(List<DesignShape> piece)
    {
        var b = Arrange.Of(piece);
        return $"{(piece.Count > 1 ? "grupp" : piece[0].Text is { } t ? "text \"" + t.Content + "\"" : "form")} {b.W:0.#} × {b.H:0.#} mm";
    }
}
