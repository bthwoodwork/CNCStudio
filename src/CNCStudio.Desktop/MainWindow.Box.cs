using System.Windows;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Rita → Låda med fingerskarvar…: the panels from <see cref="BoxJoints"/> as one group of shapes on the material,
/// each with a through cut (outside contour) with tabs and dogbones, with the chosen cutter's data for the material.
/// </summary>
public partial class MainWindow
{
    private void Box_Click(object sender, RoutedEventArgs e)
    {
        if (StartScreenVisible) return;
        if (!_document.HasMaterial) { Error("Lägg till materialet först – skivans tjocklek blir fingrarnas djup."); return; }
        var w = new BoxWindow(_document.MaterialThickness, _tools.Tools, SelectedTool) { Owner = this };
        if (w.ShowDialog() != true || w.Result is not BoxOptions o) return;
        IReadOnlyList<BoxPanel> panels;
        try { panels = BoxJoints.Panels(o); }
        catch (ArgumentException ex) { Error(ex.Message); return; }
        double d = o.ToolDiameterMm;
        var (placed, fits) = BoxJoints.Layout(panels, _document.MaterialWidth, _document.MaterialHeight, gap: d + 4, margin: d / 2 + 3);
        var tool = w.Tool is { } t && Math.Abs(t.Diameter - d) < 1e-9 ? t : null;
        var cut = tool?.CuttingFor(_document.MaterialId);
        var group = Guid.NewGuid();
        RememberChange();
        _selection.Clear(); _selectionOrder.Clear();
        foreach (var (panel, x, y) in placed)
        {
            var shape = BoxJoints.ToShape(panel, x, y, group);
            _document.Shapes.Add(shape);
            _document.CamOperations.Add(new CamOperation
            {
                ShapeId = shape.Id, Kind = CamOperationKind.Outside, ToolName = tool?.Name, ToolKind = ToolKind.FlatEndMill, ToolDiameter = d,
                TotalDepth = _document.MaterialThickness, DepthPerPass = Math.Min(cut?.DepthPerPass ?? d / 2, _document.MaterialThickness),
                FeedRate = cut?.FeedRate ?? 1000, PlungeRate = cut?.PlungeRate ?? 300, SpindleRpm = cut?.SpindleRpm ?? 18000,
                UseTabs = true, TabCount = 4, Dogbones = true,
            });
            _selection.Add(shape); _selectionOrder.Add(shape.Id);
        }
        _selected = _document.Shapes.LastOrDefault();
        _tool = Tool.Select;
        _camReport = _camPlanner.Review(_document, computeAll: true);
        Render();
        StatusText.Text = $"Låda: {placed.Count} delar med fingerskarvar, utskärning, hållflikar och dogbones." +
            (fits ? "" : " De ryms inte alla på materialet – använd Nesta eller ett större material.");
        if (!fits) Error("Alla delar ryms inte på materialet. De är utlagda ändå – använd Nesta (Objekt → Packa på materialet) eller gör materialet större.");
    }
}
