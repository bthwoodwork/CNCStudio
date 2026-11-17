using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CNCStudio.Core;

/// <summary>
/// Exportera ritningen: the design's shapes as SVG or DXF in millimetres, placed as on the material
/// (from its corner), one layer/group per machining kind so the receiver can see what was what.
/// Text and figures are already outlines, so no fonts are needed. SVG has Y down like the design;
/// DXF has Y up with 0,0 at the material's front-left corner.
/// </summary>
public static class DrawingExport
{
    public const string MaterialLayer = "Material", NoneLayer = "Ingen bearbetning";

    /// <summary>One closed or open line in design millimetres (Y down from the back edge).</summary>
    public sealed record Line(IReadOnlyList<CamPoint> Points, bool Closed);
    /// <summary>A layer: name, colour (#rrggbb) for SVG and colour number for DXF, and its lines; Circles are (cx, cy, r).</summary>
    public sealed record Layer(string Name, string Color, int Aci, List<Line> Lines, List<(double X, double Y, double R)> Circles);

    public static string LayerName(CamOperationKind? kind) => kind switch
    {
        CamOperationKind.Outside => "Utskärning utvändig",
        CamOperationKind.Inside => "Utskärning invändig",
        CamOperationKind.Pocket => "Ficka",
        CamOperationKind.Drill => "Borrning",
        CamOperationKind.Face => "Planfräsning",
        CamOperationKind.VCarve => "V-carve",
        CamOperationKind.VCarvePlug => "V-carve inlay hane",
        CamOperationKind.Carve3D => "3D-fräsning",
        CamOperationKind.Chamfer => "Fas",
        null => NoneLayer,
        _ => kind.ToString()!
    };

    private static (string Color, int Aci) LayerColor(string name) => name switch
    {
        MaterialLayer => ("#8a6a44", 8),
        "Utskärning utvändig" => ("#d32f2f", 1),
        "Utskärning invändig" => ("#ef6c00", 30),
        "Ficka" => ("#1565c0", 5),
        "Borrning" => ("#6a1b9a", 6),
        "V-carve" or "V-carve inlay hane" => ("#2e7d32", 3),
        "Fas" => ("#00838f", 4),
        _ => ("#000000", 7)
    };

    /// <summary>The layers of the drawing: shapes (all, or only <paramref name="only"/>), and the material's outline when wanted.</summary>
    public static IReadOnlyList<Layer> Layers(CncDocument doc, IEnumerable<DesignShape>? only = null, bool includeMaterial = true)
    {
        var layers = new List<Layer>();
        Layer Get(string name)
        {
            var l = layers.FirstOrDefault(x => x.Name == name);
            if (l is null) { var (c, a) = LayerColor(name); layers.Add(l = new Layer(name, c, a, new(), new())); }
            return l;
        }
        if (includeMaterial && doc.HasMaterial)
            Get(MaterialLayer).Lines.Add(new Line(Rect(0, 0, doc.MaterialWidth, doc.MaterialHeight), true));
        var wanted = only?.ToHashSet();
        foreach (var s in doc.Shapes)
        {
            if (wanted is not null && !wanted.Contains(s)) continue;
            var kind = doc.CamOperations.FirstOrDefault(o => o.ShapeId == s.Id)?.Kind;
            var layer = Get(LayerName(kind));
            switch (s.Kind)
            {
                case ShapeKind.Rectangle:
                    layer.Lines.Add(new Line(Rect(s.X, s.Y, s.Width, s.Height), true)); break;
                case ShapeKind.Ellipse when Math.Abs(s.Width - s.Height) < 1e-6:
                    layer.Circles.Add((s.X + s.Width / 2, s.Y + s.Height / 2, s.Width / 2)); break;
                case ShapeKind.Ellipse:
                {
                    const int n = 144;
                    double a = s.Width / 2, b = s.Height / 2, cx = s.X + a, cy = s.Y + b;
                    layer.Lines.Add(new Line(Enumerable.Range(0, n).Select(i => new CamPoint(cx + a * Math.Cos(2 * Math.PI * i / n), cy + b * Math.Sin(2 * Math.PI * i / n))).ToList(), true));
                    break;
                }
                default:
                    foreach (var c in s.Contours ?? new List<VectorContour>())
                        if (c.Points is { Count: >= 2 })
                            layer.Lines.Add(new Line(c.Points.Select(p => new CamPoint(s.X + p.X * s.Width, s.Y + p.Y * s.Height)).ToList(), c.Closed));
                    break;
            }
        }
        return layers;
    }

    private static IReadOnlyList<CamPoint> Rect(double x, double y, double w, double h) =>
        new[] { new CamPoint(x, y), new CamPoint(x + w, y), new CamPoint(x + w, y + h), new CamPoint(x, y + h) };

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(IReadOnlyList<Layer> layers)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var l in layers)
        {
            foreach (var p in l.Lines.SelectMany(x => x.Points)) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
            foreach (var c in l.Circles) { minX = Math.Min(minX, c.X - c.R); minY = Math.Min(minY, c.Y - c.R); maxX = Math.Max(maxX, c.X + c.R); maxY = Math.Max(maxY, c.Y + c.R); }
        }
        return minX > maxX ? (0, 0, 0, 0) : (minX, minY, maxX, maxY);
    }

    public static string Svg(CncDocument doc, IEnumerable<DesignShape>? only = null, bool includeMaterial = true)
    {
        var layers = Layers(doc, only, includeMaterial);
        var (minX, minY, maxX, maxY) = Bounds(layers);
        double w = Math.Max(maxX - minX, 1), h = Math.Max(maxY - minY, 1);
        var s = new StringBuilder();
        s.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        s.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}mm\" height=\"{F(h)}mm\" viewBox=\"{F(minX)} {F(minY)} {F(w)} {F(h)}\">\n");
        s.Append($"<title>{E(doc.Name)}</title>\n");
        foreach (var l in layers)
        {
            s.Append($"<g id=\"{E(Id(l.Name))}\" data-name=\"{E(l.Name)}\" fill=\"none\" stroke=\"{l.Color}\" stroke-width=\"0.2\">\n");
            foreach (var line in l.Lines)
            {
                s.Append(line.Closed ? "<polygon points=\"" : "<polyline points=\"");
                s.Append(string.Join(" ", line.Points.Select(p => F(p.X) + "," + F(p.Y))));
                s.Append("\"/>\n");
            }
            foreach (var c in l.Circles) s.Append($"<circle cx=\"{F(c.X)}\" cy=\"{F(c.Y)}\" r=\"{F(c.R)}\"/>\n");
            s.Append("</g>\n");
        }
        s.Append("</svg>\n");
        return s.ToString();
    }

    public static string Dxf(CncDocument doc, IEnumerable<DesignShape>? only = null, bool includeMaterial = true)
    {
        var layers = Layers(doc, only, includeMaterial);
        // Y up from the material's front edge (or from the drawing's lowest point without material).
        double baseY = doc.HasMaterial ? doc.MaterialHeight : Bounds(layers).MaxY;
        var s = new StringBuilder();
        void G(int code, string value) => s.Append(code).Append('\n').Append(value).Append('\n');
        G(0, "SECTION"); G(2, "HEADER");
        G(9, "$ACADVER"); G(1, "AC1009");
        G(9, "$INSUNITS"); G(70, "4");
        G(9, "$MEASUREMENT"); G(70, "1");
        G(0, "ENDSEC");
        G(0, "SECTION"); G(2, "TABLES");
        G(0, "TABLE"); G(2, "LAYER"); G(70, layers.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var l in layers) { G(0, "LAYER"); G(2, DxfName(l.Name)); G(70, "0"); G(62, l.Aci.ToString(CultureInfo.InvariantCulture)); G(6, "CONTINUOUS"); }
        G(0, "ENDTAB");
        G(0, "ENDSEC");
        G(0, "SECTION"); G(2, "ENTITIES");
        foreach (var l in layers)
        {
            string name = DxfName(l.Name);
            foreach (var line in l.Lines)
            {
                // R12 POLYLINE/VERTEX: read by every CAD and CAM program.
                G(0, "POLYLINE"); G(8, name); G(66, "1"); G(10, "0"); G(20, "0"); G(30, "0"); G(70, line.Closed ? "1" : "0");
                foreach (var p in line.Points) { G(0, "VERTEX"); G(8, name); G(10, F(p.X)); G(20, F(baseY - p.Y)); G(30, "0"); }
                G(0, "SEQEND"); G(8, name);
            }
            foreach (var c in l.Circles) { G(0, "CIRCLE"); G(8, name); G(10, F(c.X)); G(20, F(baseY - c.Y)); G(30, "0"); G(40, F(c.R)); }
        }
        G(0, "ENDSEC");
        G(0, "EOF");
        return s.ToString();
    }

    /// <summary>R12 layer names: letters, digits, _ and - only (å, ä, ö and spaces replaced).</summary>
    public static string DxfName(string name)
    {
        var t = name.Replace('å', 'a').Replace('ä', 'a').Replace('ö', 'o').Replace('Å', 'A').Replace('Ä', 'A').Replace('Ö', 'O');
        return new string(t.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray()).ToUpperInvariant();
    }

    private static string Id(string name) => DxfName(name).ToLowerInvariant();
    private static string F(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
    private static string E(string t) => t.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
