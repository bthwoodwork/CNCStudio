using System.IO;
using System.Windows;
using Microsoft.Win32;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// CAM → Skapa inlay (hona + hane): the selected shapes get a V-carving to the pocket depth here, and the plug –
/// mirrored, on its own board – is saved as a project of its own beside this one.
/// </summary>
public partial class MainWindow
{
    private void CreateInlay_Click(object sender, RoutedEventArgs e)
    {
        if (StartScreenVisible) return;
        var shapes = _document.Shapes.Where(s => _selection.Contains(s)).ToList();
        if (shapes.Count == 0) { Error("Markera formen (t.ex. en text) som ska läggas in."); return; }
        if (shapes.Any(s => s.Kind == ShapeKind.VectorPath && s.Contours.Any(c => !c.Closed)))
        { Error("Inlay kräver slutna former – öppna linjer kan inte läggas in."); return; }
        if (!_document.HasMaterial) { Error("Lägg till materialet (honans skiva) först."); return; }

        string what = shapes.Count == 1 ? CamPlanner.Label(_document, shapes[0]) : $"{shapes.Count} former";
        var w = new InlayWindow(_tools.Tools, _document.MaterialThickness, _document.MaterialId, what) { Owner = this };
        if (w.ShowDialog() != true || w.Result is not InlayPlan plan) return;

        CncDocument plugDoc;
        try { plugDoc = Inlay.PlugDocument(_document, shapes, plan); }
        catch (ArgumentException ex) { Error(ex.Message); return; }

        // Where to save the plug project: beside this project.
        string dir = _path is not null && Path.GetDirectoryName(_path) is string d && Directory.Exists(d) ? d : ProjectsFolder();
        var dialog = new SaveFileDialog
        {
            Title = "Spara hanens projekt", Filter = "CNC Studio JSON|*.cncstudio.json",
            FileName = SafeFileName(plugDoc.Name) + ".cncstudio.json", InitialDirectory = dir,
        };
        if (dialog.ShowDialog(this) != true) return;
        try { DocumentStorage.Save(dialog.FileName, plugDoc); }
        catch (Exception ex) { Error("Hanens projekt kunde inte sparas: " + ex.Message); return; }

        RememberChange();
        foreach (var s in shapes)
        {
            _document.CamOperations.RemoveAll(o => o.ShapeId == s.Id);
            _document.CamOperations.Add(Inlay.PocketOperation(s, plan));
        }
        _showContourPreview = true;
        if (ContourVisibilityCheck is not null) ContourVisibilityCheck.IsChecked = true;
        _camReport = _camPlanner.Review(_document, computeShapes: shapes.Select(s => s.Id).ToList());
        RefreshCamSelection();
        Render();

        string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        StatusText.Text = $"Inlay: honan ({F(plan.PocketDepthMm)} mm V-carving) är satt här, hanen sparad som {Path.GetFileName(dialog.FileName)}.";
        var answer = AppDialog.Choose(this,
            $"Honan: V-carving {F(plan.PocketDepthMm)} mm djupt på {what} i det här projektet (spara det).\n\n" +
            $"Hanen: sparad som eget projekt, speglad, på en skiva {F(plugDoc.MaterialWidth)} × {F(plugDoc.MaterialHeight)} × {F(plugDoc.MaterialThickness)} mm:\n{dialog.FileName}\n\n" +
            "SÅ GÖR DU:\n" +
            "1. Fräs honan i den här skivan.\n" +
            "2. Öppna hanens projekt, spänn fast hanens skiva, nolla X/Y och Z på den och fräs hanen.\n" +
            $"3. Limma, vänd hanen och pressa ned den i honan. Det blir {F(plan.BottomGapMm)} mm glipa i botten för limmet.\n" +
            $"4. När limmet har torkat: såga eller plana bort det som sticker upp (cirka {F(plan.ProtrusionMm)} mm plus hanens skiva).\n\n" +
            "Tips: fräs ett litet prov först, och kontrollera att det passar innan du limmar.",
            "Skapa inlay", new[] { ("Öppna hanen nu", MessageBoxResult.Yes), ("Stanna här", MessageBoxResult.No) },
            MessageBoxImage.Information, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        var save = AppDialog.Show(this, "Spara honans projekt innan hanen öppnas?", "Skapa inlay",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (save == MessageBoxResult.Cancel) return;
        if (save == MessageBoxResult.Yes) { Save_Click(this, new RoutedEventArgs()); if (_path is null) return; }
        OpenProject(dialog.FileName);
    }
}
