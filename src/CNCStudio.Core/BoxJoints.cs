using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace CNCStudio.Core;

/// <summary>What the box generator is asked for.</summary>
public sealed class BoxOptions
{
    /// <summary>Length along X (front and back), width along Y (the two ends), height – in mm.</summary>
    public double LengthMm { get; set; } = 200;
    public double WidthMm { get; set; } = 120;
    public double HeightMm { get; set; } = 80;
    /// <summary>True: the measures are inside the box; the panels are then made larger by the board's thickness.</summary>
    public bool InsideMeasures { get; set; }
    /// <summary>About how wide each finger is; the edge is divided into an odd number of equal fingers near this.</summary>
    public double FingerWidthMm { get; set; } = 20;
    /// <summary>Each slot is made this much wider than its finger, for glue and an easy fit.</summary>
    public double ClearanceMm { get; set; } = 0.1;
    public bool Bottom { get; set; } = true;
    /// <summary>Board thickness = the project's material thickness = the depth of every slot.</summary>
    public double ThicknessMm { get; set; } = 12;
    /// <summary>The cutter that cuts the panels: a slot must be wider than it.</summary>
    public double ToolDiameterMm { get; set; } = 6;
}

/// <summary>One panel: its name, outer size, and outline (Y down, from its top-left corner).</summary>
public sealed record BoxPanel(string Name, double Width, double Height, IReadOnlyList<IReadOnlyList<(double X, double Y)>> Outline);

/// <summary>
/// Lådfogar med fingrar: a box of four walls (front, back and two ends) and optionally a bottom, finger-jointed at
/// every meeting edge. Fingers are laid out between the corners (one board thickness in from each), in the same odd
/// number on both panels of a joint; one keeps the even fingers and the other the odd ones. The corner cubes all
/// belong to the front and back, and are cut away from the ends and the bottom – so no thin slivers are left. Slots are the board's thickness deep
/// and <see cref="BoxOptions.ClearanceMm"/> wider than their fingers. The outer measures of the finished box are the
/// panels' outer measures; the top edges are straight (an open box).
/// </summary>
public static class BoxJoints
{
    public static string? Validate(BoxOptions o)
    {
        bool Pos(double v) => double.IsFinite(v) && v > 0;
        if (!Pos(o.LengthMm) || !Pos(o.WidthMm) || !Pos(o.HeightMm)) return "Ange lådans längd, bredd och höjd.";
        if (!Pos(o.ThicknessMm)) return "Lägg till material först – skivans tjocklek blir urtagens djup.";
        if (!Pos(o.FingerWidthMm)) return "Ange fingerbredden.";
        if (!double.IsFinite(o.ClearanceMm) || o.ClearanceMm < 0 || o.ClearanceMm > 1) return "Spelet ska vara 0–1 mm.";
        var (l, w, h) = Outer(o);
        if (Math.Min(Math.Min(l, w), h) < 4 * o.ThicknessMm) return "Lådan är för liten för skivans tjocklek – varje mått måste vara minst 4 × tjockleken.";
        double narrowest = new[] { l, w, h }.Min(e => e / Fingers(e, o.FingerWidthMm));
        if (narrowest + o.ClearanceMm < o.ToolDiameterMm + 0.5)
            return $"Fingrarna blir {narrowest:0.#} mm breda – urtagen måste vara bredare än fräsen ({o.ToolDiameterMm:0.###} mm). Öka fingerbredden eller välj en mindre fräs.";
        return null;
    }

    /// <summary>The outer measures (length, width, height).</summary>
    public static (double L, double W, double H) Outer(BoxOptions o) => o.InsideMeasures
        ? (o.LengthMm + 2 * o.ThicknessMm, o.WidthMm + 2 * o.ThicknessMm, o.HeightMm + (o.Bottom ? o.ThicknessMm : 0))
        : (o.LengthMm, o.WidthMm, o.HeightMm);

    /// <summary>The odd number of fingers nearest edge / width (at least 3).</summary>
    public static int Fingers(double edge, double fingerWidth)
    {
        int n = Math.Max(3, (int)Math.Round(edge / fingerWidth));
        if (n % 2 == 0)
        {
            // The odd neighbour whose finger is nearest the wanted width.
            int up = n + 1, down = Math.Max(3, n - 1);
            n = Math.Abs(edge / up - fingerWidth) <= Math.Abs(edge / down - fingerWidth) ? up : down;
        }
        return n;
    }

    public static IReadOnlyList<BoxPanel> Panels(BoxOptions o)
    {
        if (Validate(o) is string problem) throw new ArgumentException(problem);
        var (l, w, h) = Outer(o);
        double t = o.ThicknessMm, c = o.ClearanceMm, f = o.FingerWidthMm;
        double tb = o.Bottom ? t : 0;                       // the band at the bottom of the walls where the bottom sits
        var panels = new List<BoxPanel>();
        // Front and back own every corner: their vertical edges have fingers over 0 … h − tb (even ones kept) and keep
        // the bottom corner squares; the bottom edge has fingers between the corners (even ones kept).
        foreach (var name in new[] { "Framsida", "Baksida" })
        {
            var slots = new PathsD();
            Vertical(slots, 0, 0, h - tb, t, c, f, keepEven: true);
            Vertical(slots, l - t, 0, h - tb, t, c, f, keepEven: true);
            if (o.Bottom) Horizontal(slots, h - t, t, l - t, t, c, f, keepEven: true);
            panels.Add(Make(name, l, h, slots));
        }
        // The ends sit between front and back: odd fingers up the corners, the bottom corner squares cut away, even
        // fingers along the bottom between the corners.
        foreach (var name in new[] { "Vänster gavel", "Höger gavel" })
        {
            var slots = new PathsD();
            Vertical(slots, 0, 0, h - tb, t, c, f, keepEven: false);
            Vertical(slots, w - t, 0, h - tb, t, c, f, keepEven: false);
            if (o.Bottom)
            {
                Horizontal(slots, h - t, t, w - t, t, c, f, keepEven: true);
                slots.Add(R(-1, h - t - c / 2, t, h + 1)); slots.Add(R(w - t, h - t - c / 2, w + 1, h + 1));
            }
            panels.Add(Make(name, w, h, slots));
        }
        // The bottom: odd fingers between the corners on every edge, the corner squares cut away (they are the walls').
        if (o.Bottom)
        {
            var slots = new PathsD();
            Horizontal(slots, 0, t, l - t, t, c, f, keepEven: false);
            Horizontal(slots, w - t, t, l - t, t, c, f, keepEven: false);
            Vertical(slots, 0, t, w - t, t, c, f, keepEven: false);
            Vertical(slots, l - t, t, w - t, t, c, f, keepEven: false);
            foreach (var (x, y) in new[] { (0.0, 0.0), (l - t, 0.0), (0.0, w - t), (l - t, w - t) })
                slots.Add(R(x - (x == 0 ? 1 : c / 2), y - (y == 0 ? 1 : c / 2), x + t + (x == 0 ? c / 2 : 1), y + t + (y == 0 ? c / 2 : 1)));
            panels.Add(Make("Botten", l, w, slots));
        }
        return panels;
    }

    /// <summary>
    /// Slots on a vertical edge band (x … x + t), fingers over y = from … to: the odd number of fingers nearest the
    /// wanted width; the ones not kept become slots, the clearance wider and running out past the band's open ends.
    /// </summary>
    private static void Vertical(PathsD slots, double x, double from, double to, double t, double c, double f, bool keepEven)
    {
        foreach (var (a, b) in SlotSpans(from, to, c, f, keepEven)) slots.Add(R(x - (x == 0 ? 1 : 0), a, x + t + (x == 0 ? 0 : 1), b));
    }

    private static void Horizontal(PathsD slots, double y, double from, double to, double t, double c, double f, bool keepEven)
    {
        foreach (var (a, b) in SlotSpans(from, to, c, f, keepEven)) slots.Add(R(a, y - (y == 0 ? 1 : 0), b, y + t + (y == 0 ? 0 : 1)));
    }

    private static IEnumerable<(double A, double B)> SlotSpans(double from, double to, double c, double f, bool keepEven)
    {
        double length = to - from;
        int n = Fingers(length, f);
        double s = length / n;
        for (int i = 0; i < n; i++)
        {
            if (keepEven ? i % 2 == 0 : i % 2 == 1) continue;
            double a = from + i * s - c / 2, b = from + (i + 1) * s + c / 2;
            if (i == 0 && from <= 0) a = -1;                // the open end of an edge: past the panel's edge
            yield return (a, b);
        }
    }

    private static BoxPanel Make(string name, double pw, double ph, PathsD slots)
    {
        var panel = new PathsD { R(0, 0, pw, ph) };
        var result = slots.Count == 0 ? panel : Clipper.Difference(panel, slots, FillRule.NonZero, 4);
        var outline = result.Where(p => p.Count >= 3)
            .Select(p => (IReadOnlyList<(double, double)>)Clipper.SimplifyPath(p, 0.001, true).Select(v => (Math.Round(v.x, 4), Math.Round(v.y, 4))).ToList()).ToList();
        return new BoxPanel(name, pw, ph, outline);
    }

    private static PathD R(double x0, double y0, double x1, double y1) =>
        new() { new PointD(x0, y0), new PointD(x1, y0), new PointD(x1, y1), new PointD(x0, y1) };

    /// <summary>A panel as a vector shape at (x, y) on the material.</summary>
    public static DesignShape ToShape(BoxPanel p, double x, double y, Guid? groupId = null) => new()
    {
        Kind = ShapeKind.VectorPath, X = x, Y = y, Width = p.Width, Height = p.Height, GroupId = groupId,
        Contours = p.Outline.Select(c => new VectorContour { Closed = true, Points = c.Select(v => new VectorPoint(v.X / p.Width, v.Y / p.Height)).ToList() }).ToList(),
    };

    /// <summary>
    /// The panels laid out in rows from the material's back-left corner with a gap between them; false if they do not
    /// all fit (they are then placed anyway, past the edge, for Nesta or a larger board).
    /// </summary>
    public static (IReadOnlyList<(BoxPanel Panel, double X, double Y)> Placed, bool Fits) Layout(IReadOnlyList<BoxPanel> panels, double materialW, double materialH, double gap, double margin)
    {
        var placed = new List<(BoxPanel, double, double)>();
        double x = margin, y = margin, row = 0;
        bool fits = true;
        foreach (var p in panels.OrderByDescending(p => p.Height))
        {
            if (x > margin && x + p.Width > materialW - margin) { x = margin; y += row + gap; row = 0; }
            placed.Add((p, x, y));
            if (x + p.Width > materialW - margin + 1e-9 || y + p.Height > materialH - margin + 1e-9) fits = false;
            x += p.Width + gap; row = Math.Max(row, p.Height);
        }
        return (placed, fits);
    }
}
