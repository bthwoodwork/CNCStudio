using System.Globalization;
using System.Windows;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>Objekt → Packa på materialet… (nesting) and Rita → Figurbank….</summary>
public partial class MainWindow
{
    private void Nest_Click(object sender, RoutedEventArgs e)
    {
        if (!_document.HasMaterial) { Error("Lägg till materialet först – delarna packas på det."); return; }
        var shapes = SelectedShapes();
        if (shapes.Count == 0)
        {
            if (_document.Shapes.Count == 0) { Error("Det finns inga former att packa."); return; }
            if (AppDialog.Show(this, $"Inget är markerat. Packa alla {_document.Shapes.Count} former på materialet?", "Packa på materialet",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) != MessageBoxResult.OK) return;
            shapes = _document.Shapes.ToList();
        }
        // Spacing: the widest cutter used + 2 mm, so the cut between two parts has room.
        double tool = _document.CamOperations.Where(o => shapes.Any(s => s.Id == o.ShapeId)).Select(o => o.ToolDiameter).DefaultIfEmpty(3.175).Max();
        double suggested = Math.Ceiling(tool + 2);
        var text = AppDialog.Prompt(this,
            $"Avstånd mellan delarna och till kanten i mm, t.ex. \"{suggested:0}, 5\".\n\nAvståndet bör vara minst fräsens diameter (största fräsen här: {tool:0.##} mm), så att spåret mellan två delar ryms.",
            "Packa på materialet", $"{suggested:0}, 5");
        if (text is null) return;
        var nums = System.Text.RegularExpressions.Regex.Matches(text.Replace(',', ' '), @"\d+(\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        if (nums.Count == 0) { Error("Skriv avståndet, t.ex. 6, 5."); return; }
        var rot = AppDialog.Choose(this, "Får delarna vridas (90°, 180°, 270°) för att packas tätare?\n\nEn vriden text blir en vanlig form (den kan inte längre redigeras som text).",
            "Packa på materialet", new[] { ("Ja, vrid", MessageBoxResult.Yes), ("Nej", MessageBoxResult.No), ("Avbryt", MessageBoxResult.Cancel) }, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (rot is MessageBoxResult.Cancel or MessageBoxResult.None) return;
        var opts = new NestOptions { SpacingMm = nums[0], MarginMm = nums.Count > 1 ? nums[1] : 5, AllowRotation = rot == MessageBoxResult.Yes };
        RememberChange();
        NestResult result;
        try { result = Nesting.Nest(shapes, _document.MaterialWidth, _document.MaterialHeight, opts); }
        catch (ArgumentException ex) { Error(ex.Message); return; }
        Render();
        StatusText.Text = $"Packade {result.Placed} delar – de använder {result.UsedLengthMm:0} av {_document.MaterialHeight:0} mm av materialets längd.";
        if (result.NotPlaced.Count > 0)
            AppDialog.Show(this, $"{result.NotPlaced.Count} del(ar) fick inte plats och står kvar där de stod:\n\n• " + string.Join("\n• ", result.NotPlaced) +
                "\n\nTa ett större material, minska avståndet, eller packa dem på en egen skiva.", "Packa på materialet", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Figure_Click(object sender, RoutedEventArgs e)
    {
        if (StartScreenVisible) return;
        var w = new FigureWindow { Owner = this };
        if (w.ShowDialog() != true || w.Result is not FigureOptions o) return;
        DesignShape shape;
        try { shape = Figures.Create(o, 0, 0); }
        catch (ArgumentException ex) { Error(ex.Message); return; }
        if (_document.HasMaterial) { shape.X = Math.Max(0, (_document.MaterialWidth - shape.Width) / 2); shape.Y = Math.Max(0, (_document.MaterialHeight - shape.Height) / 2); }
        else { shape.X = 10; shape.Y = 10; }
        RememberChange();
        _document.Shapes.Add(shape);
        _selection.Clear(); _selectionOrder.Clear();
        _selection.Add(shape); _selectionOrder.Add(shape.Id); _selected = shape;
        _tool = Tool.Select;
        Render();
        StatusText.Text = $"{Figures.Name(o.Kind)} tillagd: {shape.Width:0.#} × {shape.Height:0.#} mm.";
    }
}
