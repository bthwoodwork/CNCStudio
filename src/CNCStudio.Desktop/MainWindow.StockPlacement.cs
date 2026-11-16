using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// The material as an object of its own: click it (2D or 3D) to select it, then drag it on the table, or give
/// the table position of one of its nine points (corners, edge middles, centre) under Material på bordet.
/// </summary>
public partial class MainWindow
{
    private bool _stockSelected;
    private StockAnchor? _stockAnchorChoice;
    private bool _loadingAnchor;

    /// <summary>The point the X/Y fields are for; until chosen, the corner nearest the home corner (as before).</summary>
    private StockAnchor CurrentStockAnchor => _stockAnchorChoice ?? StockAnchor.NearestHome(_document.Machine.HomeCorner);

    private void SetStockSelected(bool on)
    {
        if (on && !_document.HasMaterial) on = false;
        if (on) { _selection.Clear(); _selectionOrder.Clear(); _selected = null; }
        bool changed = _stockSelected != on;
        _stockSelected = on;
        if (on && StockPlacementExpander is not null)
        {
            StockPlacementExpander.IsExpanded = true;
            StockPlacementExpander.BringIntoView();
        }
        if (changed)
        {
            StatusText.Text = on
                ? "Materialet markerat: dra det på bordet (2D eller 3D), eller ange var en punkt på det ska ligga under Material på bordet."
                : StatusText.Text;
            UpdateStockCursor();
        }
        View3DPanel?.SetStockSelection(on, CurrentStockAnchor);
    }

    private void UpdateStockCursor()
    {
        if (DesignCanvas is null) return;
        DesignCanvas.Cursor = _tool != Tool.Select ? Cursors.Cross : _stockSelected ? Cursors.SizeAll : null;
    }

    /// <summary>The selected material in 2D: a dashed frame and the chosen point.</summary>
    private void AddStockSelection()
    {
        double w = _document.MaterialWidth, h = _document.MaterialHeight;
        DesignCanvas.Children.Add(new Rectangle { Width = w, Height = h, Stroke = CanvasTheme.SelectedStroke, StrokeThickness = 2 / _zoom,
            StrokeDashArray = new DoubleCollection { 5, 3 }, IsHitTestVisible = false });
        var (ax, ay) = CurrentStockAnchor.OnMaterial(w, h);
        double r = 5 / _zoom;
        var dot = new Ellipse { Width = 2 * r, Height = 2 * r, Fill = CanvasTheme.SelectedStroke, Stroke = CanvasTheme.GripFill,
            StrokeThickness = 1.5 / _zoom, IsHitTestVisible = false, ToolTip = "Punkten som X/Y gäller" };
        Canvas.SetLeft(dot, ax - r); Canvas.SetTop(dot, ay - r);
        DesignCanvas.Children.Add(dot);
    }

    private void StockAnchor_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingAnchor || sender is not RadioButton { Tag: string tag }) return;
        var parts = tag.Split(',');
        _stockAnchorChoice = new StockAnchor(int.Parse(parts[0]), int.Parse(parts[1]));
        ShowStockFields();
        if (_stockSelected) { View3DPanel?.SetStockSelection(true, CurrentStockAnchor); Render(); }
    }

    private void StockBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveMachineSetup_Click(sender, e);
        e.Handled = true;
    }

    /// <summary>The fields: the chosen point's distance from the home corner, and the picker showing that point.</summary>
    private void ShowStockFields()
    {
        var anchor = CurrentStockAnchor;
        var (x, y) = anchor.ToTable(_document.Machine, _document.Stock.X, _document.Stock.Y, _document.MaterialWidth, _document.MaterialHeight);
        StockXBox.Text = F(x); StockYBox.Text = F(y);
        if (StockAnchorGrid is null) return;
        _loadingAnchor = true;
        foreach (var rb in StockAnchorGrid.Children.OfType<RadioButton>())
            rb.IsChecked = rb.Tag as string == $"{anchor.Column},{anchor.Row}";
        _loadingAnchor = false;
        StockAnchorText.Text = "Punkt: " + anchor.Name;
        StockOriginText.Text = $"X/Y mätt från hemhörnet ({CornerName(_document.Machine.HomeCorner)}).";
    }

    private void SaveMachineSetup_Click(object sender, RoutedEventArgs e)
    {
        if (!Parse(StockXBox, out var tx) || !Parse(StockYBox, out var ty))
        { Error("Ange giltiga materialkoordinater (X och Y i mm).", StockXBox); return; }
        var (x, y) = CurrentStockAnchor.FromTable(_document.Machine, tx, ty, _document.MaterialWidth, _document.MaterialHeight);
        var oldStock = _document.Stock;
        _document.Stock = new StockPlacement { X = x, Y = y };
        var error = MachineSetupValidation.StockProblem(_document);
        _document.Stock = oldStock;
        if (error is not null) { Error(error); return; }
        RememberChange(); _document.Stock = new StockPlacement { X = x, Y = y };
        _selectedFixture = null; Render();
        if (_machineView) FitToMaterial();
        StatusText.Text = $"Materialet flyttat: {CurrentStockAnchor.Name} på X {F(tx)} Y {F(ty)} från hemhörnet.";
    }

    /// <summary>A left click in the 3D view: on the material selects it, elsewhere deselects it.</summary>
    private void StockClickedIn3D(bool onMaterial)
    {
        if (onMaterial == _stockSelected) return;
        SetStockSelected(onMaterial);
        Render();
    }

    /// <summary>From a CAM problem: to Design with the material selected and its placement open.</summary>
    private void GoToStockPlacement()
    {
        SetWorkspace(Workspace.Design);
        SetStockSelected(true); Render();
        FocusField(StockXBox, null, "Ändra X/Y och tryck Enter – eller dra det markerade materialet.");
    }
}
