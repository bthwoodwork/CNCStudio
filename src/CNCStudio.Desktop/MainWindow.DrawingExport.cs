using System.IO;
using System.Text;
using System.Windows;
using CNCStudio.Core;
using Microsoft.Win32;

namespace CNCStudio.Desktop;

/// <summary>Arkiv → Exportera ritning (SVG / DXF)…: the design as a vector file in millimetres, a layer per machining kind.</summary>
public partial class MainWindow
{
    private void ExportDrawing_Click(object sender, RoutedEventArgs e)
    {
        if (_document.Shapes.Count == 0) { AppDialog.Show(this, "Det finns inget ritat att exportera.", "Exportera ritning", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var selected = SelectedShapes();
        IReadOnlyList<DesignShape>? only = null;
        if (selected.Count > 0 && selected.Count < _document.Shapes.Count)
        {
            var choice = AppDialog.Choose(this,
                $"{selected.Count} av {_document.Shapes.Count} objekt är markerade. Vad ska exporteras?",
                "Exportera ritning",
                new[] { ("Hela ritningen", MessageBoxResult.Yes), ("Bara de markerade", MessageBoxResult.No), ("Avbryt", MessageBoxResult.Cancel) },
                MessageBoxImage.Question, MessageBoxResult.Yes);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.No) only = selected;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Exportera ritning",
            Filter = "SVG (*.svg)|*.svg|DXF (*.dxf)|*.dxf",
            FileName = SafeFileName(_document.Name) + ".svg",
            InitialDirectory = ExportFolder()
        };
        if (dialog.ShowDialog(this) != true) return;
        bool dxf = string.Equals(Path.GetExtension(dialog.FileName), ".dxf", StringComparison.OrdinalIgnoreCase) ||
                   (dialog.FilterIndex == 2 && !string.Equals(Path.GetExtension(dialog.FileName), ".svg", StringComparison.OrdinalIgnoreCase));
        var path = dxf && !dialog.FileName.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase) ? Path.ChangeExtension(dialog.FileName, ".dxf") : dialog.FileName;
        try
        {
            // The material's outline only with the whole drawing: a part on its own is just the part.
            bool material = only is null;
            var text = dxf ? DrawingExport.Dxf(_document, only, material) : DrawingExport.Svg(_document, only, material);
            File.WriteAllText(path, text, dxf ? Encoding.ASCII : new UTF8Encoding(false));
            int count = only?.Count ?? _document.Shapes.Count;
            StatusText.Text = $"Ritningen exporterad ({count} objekt, {(dxf ? "DXF" : "SVG")} i mm): {path}";
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            AppDialog.Show(this, "Ritningen kunde inte sparas:\n\n" + ex.Message, "Exportera ritning", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
