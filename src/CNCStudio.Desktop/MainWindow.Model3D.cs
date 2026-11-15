using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>Rita → 3D-modell från STL…: the model is placed on the material as a box, carved with CAM → 3D-fräsning.</summary>
public partial class MainWindow
{
    private void ImportStl_Click(object sender, RoutedEventArgs e)
    {
        if (StartScreenVisible) return;
        if (!_document.HasMaterial) { Error("Lägg till materialet först – modellen fräses ned i det."); return; }
        var dialog = new OpenFileDialog { Title = "Välj en 3D-modell", Filter = "STL|*.stl|Alla filer|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        Model3D model;
        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            if (bytes.Length > 60_000_000) { Error("Filen är för stor (högst 60 MB). Förenkla modellen i 3D-programmet."); return; }
            model = Model3D.FromStl(bytes, Path.GetFileNameWithoutExtension(dialog.FileName), 10);
        }
        catch (Exception ex) { Error("STL-filen kunde inte läsas: " + ex.Message); return; }
        var (sx, sy, sz) = model.Size();
        string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        double width = Math.Round(Math.Min(_document.MaterialWidth * 0.8, _document.MaterialHeight * 0.8 * sx / Math.Max(sy, 1e-6)));
        double depth = Math.Round(Math.Min(sz * width / Math.Max(sx, 1e-6), _document.MaterialThickness - 1), 1);
        var text = AppDialog.Prompt(this,
            $"\"{model.Name}\": {model.Triangles().Count} trianglar, {F(sx)} × {F(sy)} × {F(sz)} i filens enheter.\n\n" +
            $"Bredd och djup i mm, t.ex. \"{F(width)}, {F(depth)}\". Längden följer bredden. Modellens högsta punkt hamnar på materialets yta och den fräses ned så djupt " +
            $"(materialet är {F(_document.MaterialThickness)} mm).", "3D-modell från STL", $"{F(width)}, {F(depth)}");
        if (text is null) return;
        var nums = System.Text.RegularExpressions.Regex.Matches(text.Replace(',', ' '), @"\d+(\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        if (nums.Count < 2 || nums[0] < 1 || nums[1] <= 0) { Error("Skriv bredd och djup i mm, t.ex. 100, 8."); return; }
        if (nums[1] > _document.MaterialThickness) { Error($"Djupet får inte vara större än materialet ({F(_document.MaterialThickness)} mm)."); return; }
        model.DepthMm = nums[1];
        var shape = new DesignShape { Kind = ShapeKind.Rectangle, Width = nums[0], Height = nums[0] * sy / Math.Max(sx, 1e-6), Model = model };
        shape.X = Math.Max(0, (_document.MaterialWidth - shape.Width) / 2);
        shape.Y = Math.Max(0, (_document.MaterialHeight - shape.Height) / 2);
        RememberChange();
        _document.Shapes.Add(shape);
        _selection.Clear(); _selectionOrder.Clear();
        _selection.Add(shape); _selectionOrder.Add(shape.Id); _selected = shape;
        _tool = Tool.Select;
        Render();
        RefreshCamSelection();
        StatusText.Text = $"3D-modell {F(shape.Width)} × {F(shape.Height)} mm, {F(model.DepthMm)} mm djup. Välj fräs under Smart CAM (3D-fräsning), gärna en rak för grovfräsning och en kulfräs som andra fräs.";
    }
}
