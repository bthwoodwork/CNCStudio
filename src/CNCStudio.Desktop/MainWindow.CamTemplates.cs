using System.Windows;
using System.Windows.Controls;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// CAM-mallar: saved Fräsning settings with a name (Inställningar\cam-mallar.json, synced like the tool library).
/// Choosing one fills in the settings and saves them on the selected shape at once; Spara som mall… keeps the
/// settings shown; ⋯ removes one.
/// </summary>
public partial class MainWindow
{
    private CamTemplateLibrary _camTemplates = new();
    private bool _loadingTemplates;

    private void RefreshCamTemplates()
    {
        if (CamTemplatePicker is null) return;
        _camTemplates = CamTemplateLibrary.Load(AppStorage.Current.CamTemplatesPath);
        _loadingTemplates = true;
        CamTemplatePicker.Items.Clear();
        CamTemplatePicker.Items.Add(new ComboBoxItem { Content = "Använd mall …", Foreground = System.Windows.Media.Brushes.Gray });
        foreach (var t in _camTemplates.Templates)
            CamTemplatePicker.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t, ToolTip = t.Summary });
        CamTemplatePicker.SelectedIndex = 0;
        _loadingTemplates = false;
    }

    private void CamTemplate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTemplates || (CamTemplatePicker.SelectedItem as ComboBoxItem)?.Tag is not CamTemplate t) return;
        _loadingTemplates = true; CamTemplatePicker.SelectedIndex = 0; _loadingTemplates = false;
        if (_selected is null) { Error("Markera en form först – mallen används på den."); return; }
        if (!_document.HasMaterial) { Error("Lägg till material först (Egenskaper → Projekt och material)."); return; }
        _loadingCam = true;
        try { FillCamFields(t.For(_selected.Id, _document.MaterialThickness)); }
        finally { _loadingCam = false; }
        UpdateCamSummary();
        SaveCam_Click(this, new RoutedEventArgs());
        StatusText.Text = $"Mallen \"{t.Name}\" används på {CamPlanner.Label(_document, _selected)}.";
    }

    private void SaveCamTemplate_Click(object sender, RoutedEventArgs e)
    {
        var op = ReadCam();
        if (op is null) { Error("Markera en form och fyll i giltiga värden – det är de som sparas i mallen."); return; }
        if (CamValidation.GetError(op, _document.MaterialThickness) is string err) { Error(err); return; }
        bool through = _document.HasMaterial && op.TotalDepth >= _document.MaterialThickness - 1e-6;
        string suggestion = $"{CamKindName(op.Kind)} {op.ToolDiameter:0.###} mm" + (through ? ", genom" : $", {op.TotalDepth:0.##} mm");
        var name = AppDialog.Prompt(this,
            "Namn på mallen:" + (through ? "\n\nDjupet är hela materialet – mallen skär genom vilket material den än används på." : ""),
            "Spara som mall", suggestion, _camTemplates.Templates.Select(t => t.Name).ToList());
        if (string.IsNullOrWhiteSpace(name)) return;
        bool replaces = _camTemplates.Templates.Any(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (replaces && AppDialog.Show(this, $"Det finns redan en mall som heter \"{name.Trim()}\". Ersätt den?", "Spara som mall",
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) != MessageBoxResult.OK) return;
        try { _camTemplates.Put(name, op, through); _camTemplates.Save(AppStorage.Current.CamTemplatesPath); }
        catch (Exception ex) { Error("Mallen kunde inte sparas: " + ex.Message); return; }
        RefreshCamTemplates();
        StatusText.Text = $"Mallen \"{name.Trim()}\" är sparad.";
    }

    private void CamTemplateMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (_camTemplates.Templates.Count == 0) menu.Items.Add(new MenuItem { Header = "Inga mallar", IsEnabled = false });
        foreach (var t in _camTemplates.Templates)
        {
            var item = new MenuItem { Header = $"Ta bort \"{t.Name}\"", ToolTip = t.Summary };
            item.Click += (_, _) =>
            {
                if (AppDialog.Show(this, $"Ta bort mallen \"{t.Name}\"? Former som redan använt den behåller sina inställningar.", "CAM-mallar",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
                _camTemplates.Templates.Remove(t);
                try { _camTemplates.Save(AppStorage.Current.CamTemplatesPath); }
                catch (Exception ex) { Error("Mallarna kunde inte sparas: " + ex.Message); return; }
                RefreshCamTemplates();
                StatusText.Text = $"Mallen \"{t.Name}\" är borttagen.";
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private static string CamKindName(CamOperationKind k) => k switch
    {
        CamOperationKind.Outside => "Utskärning", CamOperationKind.Inside => "Invändig", CamOperationKind.Pocket => "Ficka",
        CamOperationKind.Drill => "Borrning", CamOperationKind.Face => "Planfräsning", CamOperationKind.VCarve => "V-carve",
        CamOperationKind.VCarvePlug => "Inlay-hane", CamOperationKind.Chamfer => "Fasning", CamOperationKind.Carve3D => "3D", _ => k.ToString()
    };
}
