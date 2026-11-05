using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

// Import-only geometry. All coordinates returned in millimetres, with Y increasing downwards.
// Intentionally rejects unsupported SVG transforms and unknown DXF units instead of silently mis-scaling.
internal static class VectorFileImporter
{
    internal sealed record Result(List<DesignShape> Shapes, string Notes)
    {
        public Rect Bounds => new(Shapes.Min(s => s.X), Shapes.Min(s => s.Y),
            Shapes.Max(s => s.X + s.Width) - Shapes.Min(s => s.X),
            Shapes.Max(s => s.Y + s.Height) - Shapes.Min(s => s.Y));
    }
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static readonly Regex Number = new(@"[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);
    private static double Num(string? s, double fallback = 0) => s is null ? fallback : double.Parse(s, NumberStyles.Float, Ci);
    private static double Attr(XElement e, string name, double fallback = 0) => Num((string?)e.Attribute(name), fallback);
    private static List<double> Numbers(string? text) => Number.Matches(text ?? "").Select(m => double.Parse(m.Value, Ci)).ToList();

    internal static Result Read(string path)
    {
        if (new FileInfo(path).Length > 8_000_000) throw new InvalidDataException("Filen är för stor (max 8 MB för denna version).");
        var result = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".svg" => ReadSvg(path),
            ".dxf" => ReadDxf(path),
            _ => throw new NotSupportedException("Endast SVG och ASCII-DXF stöds.")
        };
        if (result.Shapes.Count == 0) throw new InvalidDataException("Ingen stödd vektorgeometri hittades.");
        if (result.Shapes.Count > 1500 || result.Shapes.Sum(s => s.Contours.Sum(c => c.Points.Count)) > 150_000)
            throw new InvalidDataException("För många objekt eller vektorpunkter för import i den här versionen.");
        if (result.Shapes.Any(s => !double.IsFinite(s.X) || !double.IsFinite(s.Y) || !double.IsFinite(s.Width) || !double.IsFinite(s.Height)))
            throw new InvalidDataException("Filen innehåller koordinater som inte kan representeras.");
        return result;
    }

    private static double SvgLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return double.NaN;
        var match = Regex.Match(value.Trim(), @"^\s*([-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?)\s*(mm|cm|in|pt|pc|px)?\s*$", RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidDataException("SVG använder en längdenhet som inte stöds: " + value);
        var n = Num(match.Groups[1].Value);
        var factor = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "mm" => 1d, "cm" => 10d, "in" => 25.4d, "pt" => 25.4d / 72d,
            "pc" => 25.4d / 6d, _ => 25.4d / 96d
        };
        return n * factor;
    }
    private static double SvgCoord(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        if (value.Trim().EndsWith('%')) throw new InvalidDataException("Procentbaserade SVG-koordinater stöds inte ännu.");
        // SVG coordinates are user units, interpreted through viewBox.
        if (!Regex.IsMatch(value.Trim(), @"^[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?$"))
            throw new InvalidDataException("SVG-formen använder koordinater med längdenheter: " + value);
        return Num(value);
    }
    private static double C(XElement e, string key, double fallback = 0) =>
        e.Attribute(key) is null ? fallback : SvgCoord((string?)e.Attribute(key));

    private static Result ReadSvg(string path)
    {
        var xml = XDocument.Load(path, LoadOptions.None);
        var root = xml.Root ?? throw new InvalidDataException("Tom SVG.");
        if (root.Name.LocalName != "svg") throw new InvalidDataException("Filen är inte en SVG.");
        var viewBox = Numbers((string?)root.Attribute("viewBox"));
        if (viewBox.Count != 0 && viewBox.Count != 4) throw new InvalidDataException("SVG viewBox är ogiltig.");
        double px = 25.4 / 96;
        double sx, sy, offsetX = 0, offsetY = 0;
        if (viewBox.Count == 4)
        {
            if (viewBox[2] <= 0 || viewBox[3] <= 0) throw new InvalidDataException("SVG viewBox måste ha positiv storlek.");
            double widthMm = root.Attribute("width") is null ? viewBox[2] * px : SvgLength((string?)root.Attribute("width"));
            double heightMm = root.Attribute("height") is null ? viewBox[3] * px : SvgLength((string?)root.Attribute("height"));
            sx = widthMm / viewBox[2]; sy = heightMm / viewBox[3];
            offsetX = -viewBox[0]; offsetY = -viewBox[1];
            // SVG default preserveAspectRatio xMidYMid meet.
            if ((string?)root.Attribute("preserveAspectRatio") != "none")
            {
                var uniform = Math.Min(sx, sy);
                offsetX += (widthMm / uniform - viewBox[2]) / 2;
                offsetY += (heightMm / uniform - viewBox[3]) / 2;
                sx = sy = uniform;
            }
        }
        else
        {
            sx = sy = px;
            // width and height alone do not change SVG's user-unit size: units are CSS px.
        }
        var output = new List<DesignShape>(); int omitted = 0;
        void Visit(XElement node, Matrix parent)
        {
            if (node.Attribute("display")?.Value == "none" || node.Attribute("visibility")?.Value == "hidden") return;
            var name = node.Name.LocalName;
            if (name is "defs" or "clipPath" or "mask" or "symbol" or "metadata" or "title" or "desc") return;
            var transform = ParseTransform((string?)node.Attribute("transform"));
            // Point -> element transform -> ancestor transform -> root coordinate conversion.
            transform.Append(parent);
            if (name is "svg" or "g" or "a" or "switch")
            {
                foreach (var child in node.Elements()) Visit(child, transform);
                return;
            }
            try
            {
                Geometry? geometry = name switch
                {
                    "rect" => new RectangleGeometry(new Rect(C(node, "x"), C(node, "y"), Math.Max(0, C(node, "width")), Math.Max(0, C(node, "height")))),
                    "circle" => new EllipseGeometry(new Point(C(node, "cx"), C(node, "cy")), C(node, "r"), C(node, "r")),
                    "ellipse" => new EllipseGeometry(new Point(C(node, "cx"), C(node, "cy")), C(node, "rx"), C(node, "ry")),
                    "line" => LineGeometry(node),
                    "polyline" or "polygon" => PolyGeometry(node, name == "polygon"),
                    "path" when node.Attribute("d") is not null => Geometry.Parse((string)node.Attribute("d")!),
                    _ => null
                };
                if (geometry is null) { omitted++; return; }
                var flat = geometry.GetFlattenedPathGeometry(0.08, ToleranceType.Absolute);
                foreach (var fig in flat.Figures)
                {
                    var pts = FigurePoints(fig);
                    if (pts.Count < (fig.IsClosed ? 3 : 2)) continue;
                    var mm = pts.Select(p =>
                    {
                        var q = transform.Transform(p);
                        return new Point((q.X + offsetX) * sx, (q.Y + offsetY) * sy);
                    }).ToList();
                    AddContour(output, mm, fig.IsClosed);
                }
            }
            catch (FormatException ex) { throw new InvalidDataException($"Ogiltig SVG-geometri i <{name}>: {ex.Message}", ex); }
        }
        Visit(root, Matrix.Identity);
        return new Result(output, $"SVG: {output.Count} konturer importerade. {omitted} element hoppades över (t.ex. text, bilder, användning av symboler). SVG-kurvor approximeras till linjesegment. Granska mått.");
    }
    private static Geometry LineGeometry(XElement e) => new PathGeometry(new[] { new PathFigure(
        new Point(C(e, "x1"), C(e, "y1")), new[] { new LineSegment(new Point(C(e, "x2"), C(e, "y2")), true) }, false) });
    private static Geometry PolyGeometry(XElement e, bool closed)
    {
        var coords = Numbers((string?)e.Attribute("points"));
        if (coords.Count < 4 || coords.Count % 2 != 0) throw new InvalidDataException("SVG points har ogiltigt antal koordinater.");
        var pts = Enumerable.Range(0, coords.Count / 2).Select(i => new Point(coords[i * 2], coords[i * 2 + 1])).ToList();
        return new PathGeometry(new[] { new PathFigure(pts[0], pts.Skip(1).Select(p => (PathSegment)new LineSegment(p, true)), closed) });
    }
    private static Matrix ParseTransform(string? text)
    {
        var m = Matrix.Identity;
        if (string.IsNullOrWhiteSpace(text)) return m;
        var matches = Regex.Matches(text, @"([a-zA-Z]+)\s*\(([^)]*)\)");
        if (matches.Count == 0 || Regex.Replace(text, @"([a-zA-Z]+)\s*\(([^)]*)\)", "").Trim().Length > 0)
            throw new InvalidDataException("Okänd SVG-transform: " + text);
        // SVG transform lists operate right-to-left on geometry. WPF Matrix uses row vectors.
        // Prepend keeps the declared SVG transform order correct.
        foreach (Match match in matches)
        {
            var values = Numbers(match.Groups[2].Value);
            var next = Matrix.Identity;
            switch (match.Groups[1].Value)
            {
                case "translate" when values.Count is 1 or 2: next.Translate(values[0], values.Count == 2 ? values[1] : 0); break;
                case "scale" when values.Count is 1 or 2: next.Scale(values[0], values.Count == 2 ? values[1] : values[0]); break;
                case "rotate" when values.Count == 1: next.Rotate(values[0]); break;
                case "rotate" when values.Count == 3: next.RotateAt(values[0], values[1], values[2]); break;
                case "matrix" when values.Count == 6: next = new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]); break;
                default: throw new InvalidDataException("SVG-transformen stöds inte: " + match.Value);
            }
            m.Prepend(next);
        }
        return m;
    }
    private static List<Point> FigurePoints(PathFigure fig)
    {
        var pts = new List<Point> { fig.StartPoint };
        foreach (var seg in fig.Segments)
        {
            if (seg is LineSegment line) pts.Add(line.Point);
            else if (seg is PolyLineSegment poly) pts.AddRange(poly.Points);
        }
        if (pts.Count > 1 && (pts[0] - pts[^1]).Length < 1e-7) pts.RemoveAt(pts.Count - 1);
        return pts;
    }
    private static void AddContour(List<DesignShape> shapes, List<Point> points, bool closed)
    {
        if (points.Count > 1 && (points[0] - points[^1]).Length < 1e-7) points.RemoveAt(points.Count - 1);
        if (points.Count < (closed ? 3 : 2)) return;
        var minX = points.Min(p => p.X); var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y); var maxY = points.Max(p => p.Y);
        var w = Math.Max(maxX - minX, .000001);
        var h = Math.Max(maxY - minY, .000001);
        shapes.Add(new DesignShape { Kind = ShapeKind.VectorPath, X = minX, Y = minY, Width = w, Height = h,
            Contours = new List<VectorContour> { new() { Closed = closed, Points = points.Select(p => new VectorPoint((p.X - minX) / w, (p.Y - minY) / h)).ToList() } } });
    }

    private sealed record Pair(int Code, string Value);
    private static Result ReadDxf(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.Default);
        // ASCII DXF consists of alternating group-code and value lines.
        // Empty value lines are valid and must not be discarded.
        if (lines.Length % 2 != 0)
            throw new InvalidDataException($"DXF-filen är ofullständig: rad {lines.Length} innehåller en gruppkod utan tillhörande värde. Kontrollera att filen avslutas med gruppen 0 / EOF. Endast ASCII-DXF stöds.");
        var pairs = new List<Pair>();
        for (int i = 0; i < lines.Length; i += 2)
        {
            if (!int.TryParse(lines[i].Trim().TrimStart('\uFEFF'), NumberStyles.Integer, Ci, out var code))
                throw new InvalidDataException($"Ogiltig ASCII-DXF-gruppkod på rad {i + 1}: '{lines[i].Trim()}'. Kontrollera DXF-formatet.");
            pairs.Add(new Pair(code, lines[i + 1].Trim()));
        }
        // DXF INSUNITS: 0 unitless, 1 inches, 2 feet, 4 mm, 5 cm, 6 m.
        int units = 0;
        for (int i = 0; i + 1 < pairs.Count; i++)
            if (pairs[i].Code == 9 && pairs[i].Value == "$INSUNITS")
            {
                var found = pairs.Skip(i + 1).Take(5).FirstOrDefault(p => p.Code == 70);
                if (found is not null) int.TryParse(found.Value, out units);
                break;
            }
        double scale = units switch
        {
            0 or 4 => 1, 1 => 25.4, 2 => 304.8, 5 => 10, 6 => 1000,
            _ => throw new InvalidDataException($"DXF använder INSUNITS={units} som inte stöds. Konvertera till millimeter i CAD-programmet.")
        };
        var shapes = new List<DesignShape>(); int skipped = 0;
        bool inEntities = false;
        for (int i = 0; i < pairs.Count;)
        {
            if (pairs[i].Code == 0 && pairs[i].Value == "SECTION" && i + 1 < pairs.Count && pairs[i + 1].Code == 2)
            { inEntities = pairs[i + 1].Value == "ENTITIES"; i += 2; continue; }
            if (pairs[i].Code == 0 && pairs[i].Value == "ENDSEC") { inEntities = false; i++; continue; }
            if (!inEntities || pairs[i].Code != 0) { i++; continue; }
            string type = pairs[i].Value; i++;
            var fields = new List<Pair>();
            while (i < pairs.Count && pairs[i].Code != 0) fields.Add(pairs[i++]);
            double One(int code, double fallback = 0) => Num(fields.FirstOrDefault(p => p.Code == code)?.Value, fallback);
            List<double> All(int code) => fields.Where(p => p.Code == code).Select(p => Num(p.Value)).ToList();
            Point P(double x, double y) => new(x * scale, -y * scale);
            switch (type)
            {
                case "LINE":
                    AddContour(shapes, new() { P(One(10), One(20)), P(One(11), One(21)) }, false); break;
                case "LWPOLYLINE":
                {
                    var xs = All(10); var ys = All(20);
                    if (xs.Count != ys.Count) throw new InvalidDataException("DXF LWPOLYLINE har ofullständiga koordinater.");
                    if (All(42).Any(v => Math.Abs(v) > 1e-9)) throw new InvalidDataException("DXF-polylinje med bulge-bågar stöds inte ännu; exportera bågarna som segment.");
                    var closed = (((int)One(70)) & 1) != 0;
                    AddContour(shapes, Enumerable.Range(0, xs.Count).Select(j => P(xs[j], ys[j])).ToList(), closed);
                    break;
                }
                case "CIRCLE":
                {
                    double r = One(40) * scale, x = One(10) * scale, y = -One(20) * scale;
                    if (r <= 0) break;
                    int n = ArcSteps(r, 360);
                    AddContour(shapes, Enumerable.Range(0, n).Select(j => new Point(x + r * Math.Cos(2 * Math.PI * j / n), y - r * Math.Sin(2 * Math.PI * j / n))).ToList(), true);
                    break;
                }
                case "ARC":
                {
                    double r = One(40) * scale, x = One(10) * scale, y = -One(20) * scale;
                    double start = One(50), end = One(51);
                    if (r <= 0) break;
                    while (end <= start) end += 360;
                    int n = ArcSteps(r, end - start);
                    AddContour(shapes, Enumerable.Range(0, n + 1).Select(j =>
                    { var a = (start + (end - start) * j / n) * Math.PI / 180; return new Point(x + r * Math.Cos(a), y - r * Math.Sin(a)); }).ToList(), false);
                    break;
                }
                case "ENDSEC": break;
                default: skipped++; break;
            }
        }
        if (units == 0) skipped += 0;
        return new Result(shapes, $"ASCII-DXF: {shapes.Count} konturer, {skipped} objekt av typer som inte stöds hoppades över. " +
            (units == 0 ? "OBS: Ingen INSUNITS angiven; antog millimeter. " : $"INSUNITS={units}, omräknat till mm. ") +
            "DXF Y-axeln har vänts till skärmkoordinater. Kontrollera mått och placering.");
    }
    private static int ArcSteps(double radius, double degrees)
    {
        // Chord tolerance approximately 0.1 mm, bounded to prevent excessive geometries.
        var angle = radius <= 0.1 ? Math.PI / 8 : 2 * Math.Acos(Math.Clamp(1 - 0.1 / radius, -1, 1));
        return Math.Clamp((int)Math.Ceiling(degrees * Math.PI / 180 / Math.Max(angle, .001)), 4, 4096);
    }
}
