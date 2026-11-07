using System.Windows;

namespace CNCStudio.Desktop;

/// <summary>2D/3D switch. The 3D view follows the current CAM report and is not rebuilt while dragging.</summary>
public partial class MainWindow
{
    private bool _show3D;

    private void Toggle3D_Click(object sender, RoutedEventArgs e) => Set3D(!_show3D);

    private void Set3D(bool on)
    {
        _show3D = on;
        View3DPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        View3DToggle.Content = on ? "2D" : "3D";
        View3DToggle.ToolTip = on ? "Tillbaka till 2D-ritningen (F4)" : "Visa 3D-vy med simulerat material och banor (F4)";
        View3DMenuItem.IsChecked = on;
        if (on) Update3D();
        StatusText.Text = on
            ? "3D-vy: simulerat material och banor. Höger musknapp roterar, hjulet zoomar."
            : "2D-ritning.";
    }

    private void Update3D()
    {
        if (!_show3D || View3DPanel is null) return;
        if (!_document.HasMaterial) { View3DPanel.ShowEmpty(); return; }   // nothing to show without material
        View3DPanel.Show(_document, _camReport);
        Update3DJob();
    }
}
