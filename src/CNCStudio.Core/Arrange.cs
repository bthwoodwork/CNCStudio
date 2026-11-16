using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace CNCStudio.Core;

public enum AlignMode { Left, CenterX, Right, Top, CenterY, Bottom }

/// <summary>
/// Justera, fördela, offset och kopior. A group (same GroupId) moves as one
/// piece. Design coordinates: X to the right, Y down from the back edge (Top = smallest Y).
/// </summary>
public static class Arrange
{
    public sealed record Bounds(double X0, double Y0, double X1, double Y1)
    {
        public double W => X1 - X0;
        public double H => Y1 - Y0;
    }

    /// <summary>The selection as pieces: each group is one piece, each ungrouped shape one.</summary>
    public static List<List<DesignShape>> Pieces(IEnumerable<DesignShape> shapes) =>
        shapes.GroupBy(s => s.GroupId ?? s.Id).Select(g => g.ToList()).ToList();

    public static Bounds Of(IEnumerable<DesignShape> shapes)
    {
        var list = shapes.ToList();
        if (list.Count == 0) throw new ArgumentException("Inga objekt.");
        return new Bounds(list.Min(s => s.X), list.Min(s => s.Y), list.Max(s => s.X + s.Width), list.Max(s => s.Y + s.Height));
    }

    private static void Move(IEnumerable<DesignShape> piece, double dx, double dy)
    {
        foreach (var s in piece) { s.X += dx; s.Y += dy; }
    }

    /// <summary>
    /// Lines the pieces up along one edge or centre line – of the selection, or, for a single piece, of
    /// <paramref name="material"/> (e.g. centre a sign on the board).
    /// </summary>
    public static void Align(IReadOnlyList<DesignShape> shapes, AlignMode mode, Bounds? material)
    {
        var pieces = Pieces(shapes);
        if (pieces.Count == 0) return;
        var reference = pieces.Count == 1 ? material ?? Of(shapes) : Of(shapes);
        foreach (var p in pieces)
        {
            var b = Of(p);
            double dx = mode switch
            {
                AlignMode.Left => reference.X0 - b.X0,
                AlignMode.Right => reference.X1 - b.X1,
                AlignMode.CenterX => (reference.X0 + reference.X1 - b.X0 - b.X1) / 2,
                _ => 0,
            };
            double dy = mode switch
            {
                AlignMode.Top => reference.Y0 - b.Y0,
                AlignMode.Bottom => reference.Y1 - b.Y1,
                AlignMode.CenterY => (reference.Y0 + reference.Y1 - b.Y0 - b.Y1) / 2,
                _ => 0,
            };
            Move(p, dx, dy);
        }
    }

    /// <summary>Equal gaps between the pieces, the outermost two staying put. Needs three pieces or more.</summary>
    public static void Distribute(IReadOnlyList<DesignShape> shapes, bool horizontal)
    {
        var pieces = Pieces(shapes).Select(p => (Piece: p, B: Of(p))).ToList();
        if (pieces.Count < 3) throw new ArgumentException("Markera minst tre objekt att fördela.");
        pieces = horizontal ? pieces.OrderBy(p => p.B.X0).ToList() : pieces.OrderBy(p => p.B.Y0).ToList();
        double span = horizontal ? pieces[^1].B.X1 - pieces[0].B.X0 : pieces[^1].B.Y1 - pieces[0].B.Y0;
        double used = pieces.Sum(p => horizontal ? p.B.W : p.B.H);
        double gap = (span - used) / (pieces.Count - 1);
        double at = horizontal ? pieces[0].B.X0 : pieces[0].B.Y0;
        foreach (var (piece, b) in pieces)
        {
            if (horizontal) Move(piece, at - b.X0, 0); else Move(piece, 0, at - b.Y0);
            at += (horizontal ? b.W : b.H) + gap;
        }
    }

    /// <summary>
    /// Offset: a new outline <paramref name="distanceMm"/> outside (positive) or inside (negative) the shape – for a
    /// border around a text, a frame, or a shrunk copy. Holes follow (they shrink as the shape grows).
    /// </summary>
    public static DesignShape Offset(DesignShape shape, double distanceMm, bool roundCorners)
    {
        if (!double.IsFinite(distanceMm) || Math.Abs(distanceMm) < 0.01 || Math.Abs(distanceMm) > 500)
            throw new ArgumentException("Avståndet måste vara 0,01–500 mm, plus utåt och minus inåt.");
        if (shape.Kind == ShapeKind.VectorPath && shape.Contours.Any(c => !c.Closed))
            throw new ArgumentException("Offset kräver en sluten form.");
        var region = PocketToolpaths.Region(shape);
        var result = Clipper.InflatePaths(region, distanceMm, roundCorners ? JoinType.Round : JoinType.Miter, EndType.Polygon, 4.0, 4, 0.01);
        result = new PathsD(Clipper.SimplifyPaths(result, 0.005, true).Where(p => p.Count >= 3));
        if (result.Count == 0) throw new ArgumentException("Inget blev kvar – formen är smalare än två gånger avståndet inåt.");
        double x0 = result.Min(p => p.Min(v => v.x)), x1 = result.Max(p => p.Max(v => v.x));
        double y0 = result.Min(p => p.Min(v => v.y)), y1 = result.Max(p => p.Max(v => v.y));
        double w = Math.Max(x1 - x0, 1e-6), h = Math.Max(y1 - y0, 1e-6);
        return new DesignShape
        {
            Kind = ShapeKind.VectorPath, X = x0, Y = y0, Width = w, Height = h,
            Contours = result.Select(p => new VectorContour { Closed = true, Points = p.Select(v => new VectorPoint((v.x - x0) / w, (v.y - y0) / h)).ToList() }).ToList(),
        };
    }

    /// <summary>
    /// Kopior i rutnät: <paramref name="columns"/> × <paramref name="rows"/> of the selection with <paramref name="gapMm"/>
    /// between them (edge to edge). Returns the new shapes, each with its source; the original is the top-left one.
    /// Groups stay groups (a new group per copy).
    /// </summary>
    public static List<(DesignShape Copy, DesignShape Source)> Grid(IReadOnlyList<DesignShape> shapes, int columns, int rows, double gapXMm, double gapYMm)
    {
        if (columns < 1 || rows < 1 || columns * rows < 2 || columns * rows > 500) throw new ArgumentException("Ange 1–500 kopior (kolumner × rader, minst 2).");
        if (!double.IsFinite(gapXMm) || !double.IsFinite(gapYMm) || gapXMm < 0 || gapYMm < 0 || gapXMm > 2000 || gapYMm > 2000)
            throw new ArgumentException("Mellanrummet måste vara 0–2000 mm.");
        var b = Of(shapes);
        var result = new List<(DesignShape, DesignShape)>();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                if (r == 0 && c == 0) continue;
                double dx = c * (b.W + gapXMm), dy = r * (b.H + gapYMm);
                var groups = new Dictionary<Guid, Guid>();
                foreach (var s in shapes)
                    result.Add((Copy(s, dx, dy, s.GroupId is Guid g ? (groups.TryGetValue(g, out var n) ? n : groups[g] = Guid.NewGuid()) : null), s));
            }
        return result;
    }

    public static DesignShape Copy(DesignShape s, double dx, double dy, Guid? groupId) => new()
    {
        Kind = s.Kind, GroupId = groupId, X = s.X + dx, Y = s.Y + dy, Width = s.Width, Height = s.Height, Text = s.Text?.Clone(), Model = s.Model,
        Contours = s.Contours.Select(c => new VectorContour { Closed = c.Closed, Points = c.Points.Select(p => new VectorPoint(p.X, p.Y)).ToList() }).ToList(),
    };
}
