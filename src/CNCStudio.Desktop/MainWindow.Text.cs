using System.Windows;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Rita → Text: text in any installed font becomes a vector shape (pocket it, or cut it out). The text, font and
/// style are kept in the shape, so double-clicking it (or Redigera text…) changes the words without losing its
/// place, size or CAM.
/// </summary>
public partial class MainWindow
{
    private const double DefaultTextHeightMm = 25;

    private void TextTool_Click(object sender, RoutedEventArgs e)
    {
        if (StartScreenVisible) return;
        var w = new TextWindow(_lastText, DefaultTextHeightMm, editing: false) { Owner = this };
        if (w.ShowDialog() != true || w.ResultInfo is not TextSource info) return;
        DesignShape shape;
        try { shape = TextOutlines.Shape(info, w.ResultHeight, 0, 0); }
        catch (ArgumentException ex) { Error(ex.Message); return; }
        // In the middle of the material (or at 10, 10 without material); move it like any object.
        if (_document.HasMaterial) { shape.X = Math.Max(0, (_document.MaterialWidth - shape.Width) / 2); shape.Y = Math.Max(0, (_document.MaterialHeight - shape.Height) / 2); }
        else { shape.X = 10; shape.Y = 10; }
        _lastText = info.Clone();
        RememberChange();
        _document.Shapes.Add(shape);
        _selection.Clear(); _selectionOrder.Clear();
        _selection.Add(shape); _selectionOrder.Add(shape.Id); _selected = shape;
        _tool = Tool.Select;
        Render();
        StatusText.Text = $"Text tillagd: {shape.Width:0.#} × {shape.Height:0.#} mm. Dubbelklicka på den för att ändra texten.";
    }

    private TextSource? _lastText;

    private void EditText_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.Text is null) { Error("Markera en text först (skapad med Rita → Text)."); return; }
        EditText(_selected);
    }

    private void EditText(DesignShape shape)
    {
        if (shape.Text is not TextSource old) return;
        var w = new TextWindow(old, shape.Height, editing: true) { Owner = this };
        if (w.ShowDialog() != true || w.ResultInfo is not TextSource info) return;
        DesignShape made;
        try { made = TextOutlines.Shape(info, w.ResultHeight, shape.X, shape.Y); }
        catch (ArgumentException ex) { Error(ex.Message); return; }
        RememberChange();
        // Same object: keeps its id, place (top-left corner), group and CAM operation.
        shape.Width = made.Width; shape.Height = made.Height; shape.Contours = made.Contours; shape.Text = made.Text;
        _lastText = info.Clone();
        Render();
        StatusText.Text = $"Texten ändrad: {shape.Width:0.#} × {shape.Height:0.#} mm.";
    }
}
