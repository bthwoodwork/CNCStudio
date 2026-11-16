using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CNCStudio.Core.GCode;

namespace CNCStudio.Core;

/// <summary>
/// Riggblad: one page per job to print or save as PDF (from the browser) – the material and where it lies, the
/// zero point, a top view with the shapes, the toolpaths and the fixtures, the programs in run order with their
/// cutter, operations, depths and time, a checklist before the run, and what the CAM check flagged.
/// Self-contained HTML (inline SVG and CSS), laid out for A4.
/// </summary>
public static class SetupSheet
{
    public static string Html(CncDocument doc, CamReport report, DateTime? now = null)
    {
        var tree = CamTree.Build(doc, report);
        double total = report.Operations.Count == 0 ? 0 : RunTimeEstimator.Estimate(doc, report).TotalMinutes;
        bool manual = doc.Machine.Spindle == SpindleControl.Manual;
        var anchor = StockAnchor.NearestHome(doc.Machine.HomeCorner);
        var (tx, ty) = anchor.ToTable(doc.Machine, doc.Stock.X, doc.Stock.Y, doc.MaterialWidth, doc.MaterialHeight);
        string zero = doc.WorkZero == WorkZero.Center ? "materialets mitt" : "materialets främre vänstra hörn";

        var h = new StringBuilder();
        h.Append("<!DOCTYPE html><html lang=\"sv\"><head><meta charset=\"utf-8\"><title>Riggblad – ").Append(E(doc.Name)).Append("</title><style>")
         .Append(Css).Append("</style></head><body>");
        h.Append("<header><div><h1>").Append(E(doc.Name)).Append("</h1><div class=\"sub\">Riggblad · ")
         .Append(E((now ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))).Append(" · ").Append(E(GCodeGenerator.Generator))
         .Append("</div></div><div class=\"time\">").Append(total > 0 ? E(RunTimeEstimator.Format(total)) : "–").Append("<span>beräknad körtid</span></div></header>");

        // Material and set-up facts
        h.Append("<section class=\"facts\">");
        Fact(h, "Material", doc.HasMaterial ? $"{F(doc.MaterialWidth)} × {F(doc.MaterialHeight)} × {F(doc.MaterialThickness)} mm" : "inget material");
        Fact(h, "Materialtyp", doc.MaterialName ?? "–");
        Fact(h, "Nollpunkt X0 Y0", zero);
        Fact(h, "Z0", "materialets ovansida");
        Fact(h, "Placering på bordet", $"{anchor.Name}: X {F(tx)} · Y {F(ty)} mm från hemhörnet");
        Fact(h, "Maskin", doc.Machine.Name);
        Fact(h, "Fräs", manual ? "manuell – programmet pausar (M0) för start" : "styrs av programmet (M3/M5)");
        Fact(h, "Program", tree.Programs.Count == 0 ? "inga" : tree.Programs.Count == 1 ? "1 (en fräs)" : $"{tree.Programs.Count} – ett per fräs, byt fräs och nolla Z mellan dem");
        h.Append("</section>");

        h.Append("<section class=\"drawing\">").Append(Drawing(doc, report)).Append("</section>");

        // Programs
        foreach (var p in tree.Programs)
        {
            var first = p.Operations.FirstOrDefault()?.Operation;
            h.Append("<section class=\"program\"><h2><span class=\"box\"></span>Program ").Append(p.Number).Append(" av ").Append(tree.Programs.Count)
             .Append(" – ").Append(E(p.ToolLabel)).Append("</h2><div class=\"tool\">");
            if (first is not null)
                h.Append("Ø ").Append(F(first.ToolDiameter)).Append(" mm")
                 .Append(first.ToolKind == ToolKind.VBit ? $" · V-fräs {F(first.ToolAngleDeg)}°" : first.ToolKind == ToolKind.BallNose ? " · kulfräs" : "")
                 .Append(" · ").Append(F(first.SpindleRpm, 0)).Append(" varv/min · ").Append(F(first.FeedRate, 0)).Append(" mm/min");
            h.Append(" · ").Append(E(RunTimeEstimator.Format(p.CuttingMinutes))).Append(" skär</div>");
            h.Append("<table><tr><th>Objekt</th><th>Bearbetning</th><th>Djup</th><th>Per pass</th><th>Status</th></tr>");
            foreach (var o in p.Operations)
                h.Append("<tr><td>").Append(E(o.ShapeLabel)).Append("</td><td>").Append(E(o.KindLabel)).Append("</td><td>").Append(E(o.Detail))
                 .Append("</td><td>").Append(F(o.Operation.DepthPerPass)).Append(" mm</td><td class=\"st ").Append(o.Status).Append("\">").Append(StatusText(o.Status)).Append("</td></tr>");
            h.Append("</table>");
            if (p.Number > 1) h.Append("<div class=\"step\">☐ Byt till ").Append(E(p.ToolLabel)).Append(" · ☐ Nolla Z på materialytan med den (X0 Y0 ändras inte)</div>");
            h.Append("</section>");
        }
        if (tree.Unassigned.Count > 0)
            h.Append("<p class=\"note\">Utan fräsning: ").Append(E(string.Join(", ", tree.Unassigned.Select(u => u.Label)))).Append("</p>");

        // Checklist
        h.Append("<section class=\"check\"><h2>Före körning</h2><ul>")
         .Append("<li>☐ Materialet fastspänt där det är ritat, fastspänningarna där de är ritade</li>")
         .Append("<li>☐ Homing ($H)</li>")
         .Append("<li>☐ X0 Y0 i ").Append(E(zero)).Append("</li>")
         .Append("<li>☐ Z0 på materialets ovansida med fräsen för program 1</li>")
         .Append(manual ? "<li>☐ Fräsen startas för hand när programmet pausar</li>" : "<li>☐ Händerna borta – spindeln startas av programmet</li>")
         .Append("<li>☐ Hörselskydd, skyddsglasögon, dammsugning</li></ul></section>");

        var findings = report.AllFindings.Where(f => f.Severity is CamSeverity.Critical or CamSeverity.Warning)
            .Select(f => (f.Severity, f.Message)).Distinct().ToList();
        if (findings.Count > 0)
        {
            h.Append("<section class=\"warn\"><h2>CAM-kontrollen</h2><ul>");
            foreach (var (sev, msg) in findings.Take(20))
                h.Append("<li class=\"").Append(sev == CamSeverity.Critical ? "crit" : "w").Append("\">").Append(sev == CamSeverity.Critical ? "Kritiskt: " : "Varning: ").Append(E(msg)).Append("</li>");
            h.Append("</ul></section>");
        }
        h.Append("<footer>Kontrollera alltid G-coden och kör gärna i luften första gången.</footer></body></html>");
        return h.ToString();
    }

    /// <summary>The top view as SVG: material, shapes, toolpaths, fixtures and the zero point (design coordinates: Y down = towards the front).</summary>
    public static string Drawing(CncDocument doc, CamReport report)
    {
        double w = doc.HasMaterial ? doc.MaterialWidth : 100, l = doc.HasMaterial ? doc.MaterialHeight : 100;
        var fixtures = doc.Fixtures.Select(f => (f.Name, X: f.X - doc.Stock.X, Y: f.Y - doc.Stock.Y, f.Width, f.Length)).ToList();
        double minX = Math.Min(0, fixtures.Select(f => f.X).DefaultIfEmpty(0).Min()), minY = Math.Min(0, fixtures.Select(f => f.Y).DefaultIfEmpty(0).Min());
        double maxX = Math.Max(w, fixtures.Select(f => f.X + f.Width).DefaultIfEmpty(w).Max()), maxY = Math.Max(l, fixtures.Select(f => f.Y + f.Length).DefaultIfEmpty(l).Max());
        double pad = Math.Max(maxX - minX, maxY - minY) * 0.08 + 8;
        double vx = minX - pad, vy = minY - pad, vw = maxX - minX + 2 * pad, vh = maxY - minY + 2 * pad;
        double stroke = Math.Max(vw, vh) / 600;
        var s = new StringBuilder();
        s.Append($"<svg viewBox=\"{F(vx)} {F(vy)} {F(vw)} {F(vh)}\" xmlns=\"http://www.w3.org/2000/svg\" preserveAspectRatio=\"xMidYMid meet\">");
        s.Append($"<rect x=\"0\" y=\"0\" width=\"{F(w)}\" height=\"{F(l)}\" fill=\"#f3e6d3\" stroke=\"#8a6a44\" stroke-width=\"{F(stroke * 2)}\"/>");
        foreach (var f in fixtures)
            s.Append($"<rect x=\"{F(f.X)}\" y=\"{F(f.Y)}\" width=\"{F(f.Width)}\" height=\"{F(f.Length)}\" fill=\"#e8a477\" fill-opacity=\".6\" stroke=\"#b5582a\" stroke-width=\"{F(stroke)}\"><title>{E(f.Name)}</title></rect>");
        foreach (var shape in doc.Shapes) s.Append(ShapeSvg(shape, stroke));
        foreach (var r in report.Operations)
        {
            if (r.Preview is not { } p) continue;
            var paths = p.Centerlines.Count > 0 ? p.Centerlines : new[] { p.Centerline };
            foreach (var path in paths.Take(400))
            {
                if (path.Count < 2) continue;
                s.Append($"<polyline fill=\"none\" stroke=\"#1f8f4e\" stroke-width=\"{F(stroke)}\" stroke-dasharray=\"{F(stroke * 4)} {F(stroke * 3)}\" points=\"");
                foreach (var q in Thin(path)) s.Append(F(q.X)).Append(',').Append(F(q.Y)).Append(' ');
                s.Append("\"/>");
            }
        }
        // Zero point: red X arrow right, green Y arrow towards the back (up in the view).
        var (zx, zy) = doc.WorkZero == WorkZero.Center ? (w / 2, l / 2) : (0, l);
        double a = Math.Max(w, l) * 0.08;
        s.Append($"<line x1=\"{F(zx)}\" y1=\"{F(zy)}\" x2=\"{F(zx + a)}\" y2=\"{F(zy)}\" stroke=\"#d33\" stroke-width=\"{F(stroke * 3)}\"/>");
        s.Append($"<line x1=\"{F(zx)}\" y1=\"{F(zy)}\" x2=\"{F(zx)}\" y2=\"{F(zy - a)}\" stroke=\"#2a2\" stroke-width=\"{F(stroke * 3)}\"/>");
        s.Append($"<circle cx=\"{F(zx)}\" cy=\"{F(zy)}\" r=\"{F(stroke * 5)}\" fill=\"#fff\" stroke=\"#000\" stroke-width=\"{F(stroke)}\"/>");
        double fs = Math.Max(vw, vh) / 45;
        s.Append($"<text x=\"{F(zx + stroke * 6)}\" y=\"{F(zy + fs * 1.2)}\" font-size=\"{F(fs)}\" font-family=\"sans-serif\">X0 Y0</text>");
        s.Append($"<text x=\"{F(w / 2)}\" y=\"{F(-fs * 0.6)}\" font-size=\"{F(fs)}\" text-anchor=\"middle\" font-family=\"sans-serif\" fill=\"#555\">BAK · {F(w)} mm</text>");
        s.Append($"<text x=\"{F(w / 2)}\" y=\"{F(l + fs * 1.4)}\" font-size=\"{F(fs)}\" text-anchor=\"middle\" font-family=\"sans-serif\" fill=\"#555\">FRAM (operatören)</text>");
        double side = minX - fs * 0.6;                              // left of the material and of any fixture there
        s.Append($"<text x=\"{F(side)}\" y=\"{F(l / 2)}\" font-size=\"{F(fs)}\" text-anchor=\"middle\" font-family=\"sans-serif\" fill=\"#555\" transform=\"rotate(-90 {F(side)} {F(l / 2)})\">{F(l)} mm</text>");
        s.Append("</svg>");
        return s.ToString();
    }

    private static string ShapeSvg(DesignShape s, double stroke)
    {
        string style = $"fill=\"#6fb2f0\" fill-opacity=\".18\" stroke=\"#2a6fb0\" stroke-width=\"{F(stroke * 1.4)}\"";
        if (s.Kind == ShapeKind.Ellipse)
            return $"<ellipse cx=\"{F(s.X + s.Width / 2)}\" cy=\"{F(s.Y + s.Height / 2)}\" rx=\"{F(s.Width / 2)}\" ry=\"{F(s.Height / 2)}\" {style}/>";
        if (s.Kind != ShapeKind.VectorPath || s.Contours is null || s.Contours.Count == 0)
            return $"<rect x=\"{F(s.X)}\" y=\"{F(s.Y)}\" width=\"{F(s.Width)}\" height=\"{F(s.Height)}\" {style}/>";
        var d = new StringBuilder();
        foreach (var c in s.Contours)
        {
            if (c.Points is null || c.Points.Count < 2) continue;
            for (int i = 0; i < c.Points.Count; i++)
                d.Append(i == 0 ? 'M' : 'L').Append(F(s.X + c.Points[i].X * s.Width)).Append(' ').Append(F(s.Y + c.Points[i].Y * s.Height)).Append(' ');
            if (c.Closed) d.Append("Z ");
        }
        return $"<path fill-rule=\"evenodd\" d=\"{d}\" {style}/>";
    }

    /// <summary>At most ~2000 points per path – enough for a printed drawing, small enough for a big pocket.</summary>
    private static IEnumerable<CamPoint> Thin(IReadOnlyList<CamPoint> path)
    {
        int step = Math.Max(1, path.Count / 2000);
        for (int i = 0; i < path.Count; i += step) yield return path[i];
        if ((path.Count - 1) % step != 0) yield return path[^1];
    }

    private static void Fact(StringBuilder h, string label, string value) =>
        h.Append("<div><span>").Append(E(label)).Append("</span><b>").Append(E(value)).Append("</b></div>");

    private static string StatusText(CamTreeStatus s) => s switch
    {
        CamTreeStatus.Ok or CamTreeStatus.Info => "OK", CamTreeStatus.Warning => "Varning", CamTreeStatus.Critical => "Kritiskt",
        CamTreeStatus.Stale => "Inaktuell", _ => "Ingen bana"
    };

    /// <summary>Only what HTML needs escaped; the page is UTF-8, so å, ä, ö and × stay as they are.</summary>
    private static string E(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    private static string F(double v, int decimals = 2)
    {
        var r = Math.Round(v, decimals);
        if (r == 0) r = 0;
        return r.ToString("0." + new string('#', Math.Max(decimals, 0)), CultureInfo.InvariantCulture).TrimEnd('.');
    }

    private const string Css =
        "@page{size:A4;margin:12mm}*{box-sizing:border-box}body{font:11pt/1.4 'Segoe UI',system-ui,sans-serif;color:#1a1a1a;margin:0 auto;max-width:190mm;padding:8mm 0}" +
        "header{display:flex;justify-content:space-between;align-items:flex-end;border-bottom:2px solid #1a1a1a;padding-bottom:6px;margin-bottom:10px}" +
        "h1{font-size:20pt;margin:0}.sub{color:#555;font-size:9.5pt}.time{text-align:right;font-size:18pt;font-weight:700}.time span{display:block;font-size:8.5pt;font-weight:400;color:#555}" +
        ".facts{display:grid;grid-template-columns:1fr 1fr;gap:4px 18px;margin-bottom:10px}.facts div{display:flex;justify-content:space-between;border-bottom:1px solid #ddd;padding:2px 0}" +
        ".facts span{color:#555;white-space:nowrap;margin-right:12px}.facts b{text-align:right}.drawing{border:1px solid #ccc;border-radius:6px;padding:6px;margin-bottom:12px}.drawing svg{width:100%;max-height:95mm}" +
        ".program{break-inside:avoid;margin:10px 0}h2{font-size:13pt;margin:0 0 3px;display:flex;align-items:center;gap:8px}.box{display:inline-block;width:12px;height:12px;border:1.5px solid #1a1a1a}" +
        ".tool{color:#444;font-size:9.5pt;margin-bottom:4px}table{width:100%;border-collapse:collapse;font-size:10pt}th,td{text-align:left;padding:3px 6px;border-bottom:1px solid #e3e3e3}" +
        "th{color:#555;font-weight:600;font-size:9pt}.st.Warning{color:#a66a00}.st.Critical{color:#c0261b;font-weight:700}.st.Stale,.st.NoPath{color:#777}" +
        ".step{margin-top:4px;font-size:10pt}.check{break-inside:avoid;margin-top:12px}.check ul,.warn ul{margin:4px 0;padding-left:0;list-style:none}.check li{padding:2px 0}" +
        ".warn li.crit{color:#c0261b}.warn li.w{color:#8a5a00}.warn li{padding:2px 0}.note{color:#555;font-size:9.5pt}footer{margin-top:14px;color:#777;font-size:9pt;border-top:1px solid #ddd;padding-top:4px}";
}
