using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>
/// Rotera 90°: the selected shapes turned a quarter turn as one piece around the middle of their common box
/// (a single shape around its own middle) – e.g. to lay a part along the grain. Contours, the size and the
/// hand-placed tabs turn with it. A text becomes plain outlines (it would come back unturned if edited as text);
/// a 3D model cannot be turned here.
/// </summary>
public static class ShapeRotation
{
    /// <summary>Why the shapes cannot be turned, or null.</summary>
    public static string? Problem(IReadOnlyCollection<DesignShape> shapes) =>
        shapes.Count == 0 ? "Markera det som ska vridas."
        : shapes.Any(s => s.Model is not null) ? "En 3D-modell kan inte vridas här – vrid den i 3D-programmet och läs in den igen."
        : null;

    public static void Rotate90(CncDocument doc, IReadOnlyCollection<DesignShape> shapes, bool clockwise)
    {
        if (Problem(shapes) is string p) throw new InvalidOperationException(p);
        double minX = shapes.Min(s => s.X), minY = shapes.Min(s => s.Y);
        double cx = (minX + shapes.Max(s => s.X + s.Width)) / 2, cy = (minY + shapes.Max(s => s.Y + s.Height)) / 2;
        foreach (var s in shapes)
        {
            double w = s.Width, h = s.Height;
            // Screen coordinates (Y down): clockwise takes right to down.
            (s.X, s.Y) = clockwise ? (cx - (s.Y + h - cy), cy + (s.X - cx)) : (cx + (s.Y - cy), cy - (s.X + w - cx));
            (s.Width, s.Height) = (h, w);
            foreach (var c in s.Contours ?? new List<VectorContour>())
                foreach (var v in c.Points)
                    (v.X, v.Y) = clockwise ? (1 - v.Y, v.X) : (v.Y, 1 - v.X);
            s.Text = null;
            foreach (var op in doc.CamOperations.Where(o => o.ShapeId == s.Id && o.ManualTabs is not null))
                foreach (var t in op.ManualTabs!)
                    (t.X, t.Y) = clockwise ? (h - t.Y, t.X) : (t.Y, w - t.X);
        }
        // Turned near the back or left edge, the piece would stick out past it: move it back in (not if it was already out).
        double dx = minX >= 0 ? Math.Max(0, -shapes.Min(s => s.X)) : 0, dy = minY >= 0 ? Math.Max(0, -shapes.Min(s => s.Y)) : 0;
        foreach (var s in shapes) { s.X += dx; s.Y += dy; }
    }
}
