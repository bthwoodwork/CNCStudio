using System.Globalization;
using System.Windows;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>Objekt → Justera, Fördela, Offset… och Kopior i rutnät… (see <see cref="Arrange"/>).</summary>
public partial class MainWindow
{
    private List<DesignShape> SelectedShapes() => _document.Shapes.Where(s => _selection.Contains(s)).ToList();

    private void Align_Click(object sender, RoutedEventArgs e)
    {
        var shapes = SelectedShapes();
        if (shapes.Count == 0) { Error("Markera det som ska justeras."); return; }
        var mode = Enum.Parse<AlignMode>((string)((FrameworkElement)sender).Tag);
        var material = _document.HasMaterial ? new CNCStudio.Core.Arrange.Bounds(0, 0, _document.MaterialWidth, _document.MaterialHeight) : null;
        bool toMaterial = CNCStudio.Core.Arrange.Pieces(shapes).Count == 1;
        if (toMaterial && material is null) { Error("Ett enda objekt justeras mot materialet – lägg till material först, eller markera fler objekt."); return; }
        RememberChange();
        CNCStudio.Core.Arrange.Align(shapes, mode, material);
        Render();
        StatusText.Text = toMaterial ? "Justerat mot materialet." : $"{shapes.Count} objekt justerade mot markeringen.";
    }

    private void Distribute_Click(object sender, RoutedEventArgs e)
    {
        var shapes = SelectedShapes();
        bool horizontal = (string)((FrameworkElement)sender).Tag == "H";
        try
        {
            if (CNCStudio.Core.Arrange.Pieces(shapes).Count < 3) throw new ArgumentException("Markera minst tre objekt att fördela.");
            RememberChange();
            CNCStudio.Core.Arrange.Distribute(shapes, horizontal);
        }
        catch (ArgumentException ex) { Error(ex.Message); return; }
        Render();
        StatusText.Text = horizontal ? "Fördelade med lika mellanrum i sidled." : "Fördelade med lika mellanrum i höjdled.";
    }

    private void Offset_Click(object sender, RoutedEventArgs e)
    {
        var shapes = SelectedShapes();
        if (shapes.Count == 0) { Error("Markera formen att göra offset av (t.ex. en text för en ram runt den)."); return; }
        var text = AppDialog.Prompt(this, "Avstånd i mm: plus = en ny kontur utanför formen (ram, kant runt text), minus = innanför.\n\nOriginalet ändras inte – offseten blir en ny form.",
            "Offset", "3");
        if (text is null) return;
        if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { Error("Skriv ett tal, t.ex. 3 eller -1,5."); return; }
        var corners = AppDialog.Choose(this, "Hörnen på den nya konturen:", "Offset",
            new[] { ("Rundade", MessageBoxResult.Yes), ("Skarpa", MessageBoxResult.No), ("Avbryt", MessageBoxResult.Cancel) }, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (corners is MessageBoxResult.Cancel or MessageBoxResult.None) return;
        var made = new List<DesignShape>();
        try { foreach (var s in shapes) made.Add(CNCStudio.Core.Arrange.Offset(s, d, corners == MessageBoxResult.Yes)); }
        catch (ArgumentException ex) { Error(ex.Message); return; }
        RememberChange();
        _document.Shapes.AddRange(made);
        _selection.Clear(); _selectionOrder.Clear();
        foreach (var m in made) { _selection.Add(m); _selectionOrder.Add(m.Id); }
        _selected = made.FirstOrDefault();
        Render();
        StatusText.Text = $"Offset {d:0.##} mm: {made.Count} ny{(made.Count == 1 ? "" : "a")} form{(made.Count == 1 ? "" : "er")} (markerad{(made.Count == 1 ? "" : "e")}).";
    }

    private void GridCopies_Click(object sender, RoutedEventArgs e)
    {
        var shapes = SelectedShapes();
        if (shapes.Count == 0) { Error("Markera det som ska kopieras."); return; }
        var text = AppDialog.Prompt(this, "Kolumner × rader och mellanrum i mm (kant till kant), t.ex. \"3 x 2, 10\" eller \"4 x 1, 5, 0\" (mellanrum i sidled, i höjdled).\n\nCAM-inställningarna följer med varje kopia.",
            "Kopior i rutnät", "3 x 2, 10");
        if (text is null) return;
        var nums = System.Text.RegularExpressions.Regex.Matches(text.Replace(',', ' ').Replace("x", " ").Replace("X", " ").Replace("×", " "), @"-?\d+(\.\d+)?")
            .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        if (nums.Count < 2) { Error("Skriv kolumner och rader, t.ex. 3 x 2, 10."); return; }
        int cols = (int)nums[0], rows = (int)nums[1];
        double gx = nums.Count > 2 ? nums[2] : 10, gy = nums.Count > 3 ? nums[3] : gx;
        List<(DesignShape Copy, DesignShape Source)> copies;
        try { copies = CNCStudio.Core.Arrange.Grid(shapes, cols, rows, gx, gy); }
        catch (ArgumentException ex) { Error(ex.Message); return; }
        RememberChange();
        foreach (var (copy, source) in copies)
        {
            _document.Shapes.Add(copy);
            if (_document.CamOperations.FirstOrDefault(o => o.ShapeId == source.Id) is CamOperation op)
                _document.CamOperations.Add(op.CopyFor(copy.Id));
        }
        Render();
        var all = CNCStudio.Core.Arrange.Of(shapes.Concat(copies.Select(c => c.Copy)));
        StatusText.Text = $"{cols} × {rows} i rutnät: {all.W:0.#} × {all.H:0.#} mm" +
            (_document.HasMaterial && (all.X1 > _document.MaterialWidth || all.Y1 > _document.MaterialHeight) ? " – går utanför materialet!" : ".");
    }
}
