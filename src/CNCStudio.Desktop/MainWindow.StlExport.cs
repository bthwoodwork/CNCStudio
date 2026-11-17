using System.IO;
using System.Windows;
using CNCStudio.Core;
using Microsoft.Win32;

namespace CNCStudio.Desktop;

/// <summary>Arkiv → Exportera 3D (STL)…: the selected parts (or all) as solids, e.g. a box from Låda for a 3D printer.</summary>
public partial class MainWindow
{
    private void ExportStl_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedShapes();
        var shapes = selected.Count > 0 ? selected : _document.Shapes.ToList();
        if (shapes.Count == 0) { Error("Det finns inget ritat att exportera."); return; }
        // Toolpaths for every shape, so "as milled" has them (and the parts are told apart from the holes).
        var report = _camPlanner.Review(_document, computeAll: true);
        double defaultThickness = _document.HasMaterial ? _document.MaterialThickness : 10;
        var preview = PartExport.Parts(_document, report, shapes, new PartExport.Options(defaultThickness));
        if (preview.Count == 0)
        { Error("Inget av det markerade blir en del: bara former utan bearbetning, med utvändig utskärning eller planfräsning blir solida delar."); return; }
        bool anyDogbones = shapes.Any(s => _document.CamOperations.Any(o => o.ShapeId == s.Id && o.Dogbones));
        var w = new StlExportWindow(shapes.Count, preview.Count, defaultThickness, anyDogbones) { Owner = this };
        if (w.ShowDialog() != true) return;

        IReadOnlyList<PartExport.Part> parts;
        try { parts = PartExport.Parts(_document, report, shapes, new PartExport.Options(w.Thickness, w.AsMilled)); }
        catch (Exception ex) { App.LogError(ex); Error("Delarna kunde inte beräknas: " + ex.Message); return; }

        var dialog = new SaveFileDialog
        {
            Title = w.PerPart && parts.Count > 1 ? "Exportera STL – namnet får löpnummer per del" : "Exportera STL",
            Filter = "STL (*.stl)|*.stl", FileName = SafeFileName(_document.Name) + ".stl", InitialDirectory = ExportFolder()
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var written = new List<string>();
            if (w.PerPart && parts.Count > 1)
            {
                string folder = Path.GetDirectoryName(dialog.FileName)!, stem = Path.GetFileNameWithoutExtension(dialog.FileName);
                for (int i = 0; i < parts.Count; i++)
                {
                    var path = Path.Combine(folder, $"{stem} {i + 1}.stl");
                    File.WriteAllBytes(path, PartExport.Stl(PartExport.Solid(parts[i].Region, w.Thickness), $"{_document.Name} {parts[i].Name}"));
                    written.Add(path);
                }
            }
            else
            {
                var tris = parts.SelectMany(p => PartExport.Solid(p.Region, w.Thickness)).ToList();
                File.WriteAllBytes(dialog.FileName, PartExport.Stl(tris, _document.Name));
                written.Add(dialog.FileName);
            }
            StatusText.Text = $"STL exporterad: {parts.Count} {(parts.Count == 1 ? "del" : "delar")}, {w.Thickness:0.##} mm, " +
                $"{(w.AsMilled ? "som det fräses" : "utan dogbones")} – {(written.Count == 1 ? written[0] : $"{written.Count} filer i {Path.GetDirectoryName(written[0])}")}";
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            AppDialog.Show(this, "STL-filen kunde inte sparas:\n\n" + ex.Message, "Exportera STL", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
