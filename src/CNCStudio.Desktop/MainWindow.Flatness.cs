using System.Globalization;
using System.Windows;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Maskin → Planhetskontroll…: guided probing of 5 or 9 points with the touch plate over the selection (or the
/// material), then how flat the surface is and what that means for the job (see <see cref="Flatness"/>).
/// </summary>
public partial class MainWindow
{
    private async void Flatness_Click(object sender, RoutedEventArgs e)
    {
        if (_runner is not null || _macroRunner is not null) { Error("Ett program eller makro körs – vänta tills det är klart."); return; }
        if (RequireIdle("Planhetskontroll") is not GrblClient g) return;
        if (!_homedThisSession) { Error("Kör homing först – kontrollen lyfter Z i maskinkoordinater."); return; }
        if (!_document.HasMaterial) { Error("Lägg till materialet först – punkterna läggs ut på det."); return; }

        var selected = SelectedShapes();
        var area = selected.Count > 0 ? CNCStudio.Core.Arrange.Of(selected) : new CNCStudio.Core.Arrange.Bounds(0, 0, _document.MaterialWidth, _document.MaterialHeight);
        string where = selected.Count > 0 ? "över det markerade" : "över hela materialet";
        var probe = AppStorage.Current.LoadProbeSettings();
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        var count = AppDialog.Choose(this,
            $"Fräsen probar materialets yta i flera punkter {where} ({F(area.W)} × {F(area.H)} mm) med probplattan ({F(probe.PlateThicknessMm)} mm). " +
            "Du flyttar plattan till varje punkt när fräsen står där.\n\n" +
            "FÖRE START:\n• Z-nollan är satt på materialets yta (ungefär räcker – fräsen går ned till 10 mm över plattan).\n" +
            "• X/Y-nollan är satt som när du kör programmet.\n• Klämman sitter på fräsen.\n\nHur många punkter?",
            "Planhetskontroll", new[] { ("5 punkter", MessageBoxResult.Yes), ("9 punkter", MessageBoxResult.No), ("Avbryt", MessageBoxResult.Cancel) },
            MessageBoxImage.Question, MessageBoxResult.Yes);
        if (count is MessageBoxResult.Cancel or MessageBoxResult.None) return;
        var points = Flatness.Points(area.X0, area.Y0, area.X1, area.Y1, count == MessageBoxResult.Yes ? 5 : 9);

        double above = probe.PlateThicknessMm + 10;               // work Z over the plate while moving and waiting
        var cycle = new ProbeCycle(g, probe);
        var measured = new List<SurfacePoint>();
        var labels = new List<string>();
        try
        {
            for (int i = 0; i < points.Count; i++)
            {
                var (x, y) = points[i];
                var (wx, wy) = _document.ToWork(x, y);
                StatusText.Text = $"Planhetskontroll: till punkt {i + 1} av {points.Count} …";
                foreach (var line in new[] { $"G53 G0 Z{F(-_document.Machine.LimitMarginMm)}", $"G90 G0 X{F(wx)} Y{F(wy)}", $"G90 G0 Z{F(above)}" })
                    if (!await SendAsync(g, line)) return;
                string label = Describe(x, y, area);
                var go = AppDialog.Choose(this, $"Punkt {i + 1} av {points.Count} ({label}, X {F(wx)} Y {F(wy)}).\n\nLägg plattan på materialet under fräsen och klicka Proba.",
                    "Planhetskontroll", new[] { ("Proba", MessageBoxResult.Yes), ("Hoppa över", MessageBoxResult.No), ("Avbryt", MessageBoxResult.Cancel) },
                    MessageBoxImage.Information, MessageBoxResult.Yes);
                if (go is MessageBoxResult.Cancel or MessageBoxResult.None) break;
                if (go == MessageBoxResult.No) continue;
                double z = await cycle.ProbeSurfaceHereAsync(above + probe.MaxTravelZMm);
                measured.Add(new SurfacePoint(x, y, z));
                labels.Add(label);
                if (!await SendAsync(g, $"G90 G0 Z{F(above)}")) return;
            }
        }
        catch (InvalidOperationException ex) { Error("Probningen stoppades: " + ex.Message); return; }
        finally { try { await SendAsync(g, $"G53 G0 Z{F(-_document.Machine.LimitMarginMm)}"); } catch (Exception) { } }

        if (measured.Count < 3) { StatusText.Text = "Planhetskontrollen avbröts – minst tre punkter behövs."; return; }
        double? vAngle = _document.CamOperations.Where(o => o.ToolKind == ToolKind.VBit).Select(o => (double?)o.ToolAngleDeg).FirstOrDefault();
        var report = Flatness.Analyze(measured, vAngle);
        double mean = measured.Average(p => p.Z);
        string table = string.Join("\n", measured.Select((p, i) => $"• {labels[i]}: {(p.Z - mean >= 0 ? "+" : "")}{F(p.Z - mean)} mm"));
        StatusText.Text = "Planhetskontroll: " + report.Verdict;
        AppDialog.Show(this,
            report.Verdict + $" Lutning {F(report.TiltPer100Mm)} mm per 100 mm, buktighet {F(report.WarpMm)} mm.\n\n" +
            "Höjd jämfört med medel (+ = högre):\n" + table + "\n\n" + string.Join("\n\n", report.Advice.Select(a => "→ " + a)) +
            "\n\nZ-nollan är inte ändrad. Lägg tillbaka plattan och nolla Z vid den punkt du vill (helst den högsta, så att inget fräses djupare än tänkt).",
            "Planhetskontroll", MessageBoxButton.OK, report.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    /// <summary>"bak vänster", "mitt", "fram höger" … (design Y down: small Y is the back).</summary>
    private static string Describe(double x, double y, CNCStudio.Core.Arrange.Bounds a)
    {
        string h = x < a.X0 + a.W / 3 ? "vänster" : x > a.X1 - a.W / 3 ? "höger" : "";
        string v = y < a.Y0 + a.H / 3 ? "bak" : y > a.Y1 - a.H / 3 ? "fram" : "";
        return (v, h) switch { ("", "") => "mitt", ("", _) => "mitt " + h, (_, "") => v + " mitt", _ => v + " " + h };
    }
}
