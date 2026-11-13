using System.IO;
using System.Windows;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Docking as in Visual Studio (AvalonDock): the drawing area is the document in the middle, and Ritverktyg,
/// Egenskaper, Maskinkontroll and Smart CAM are panels that can be dragged by their title bar, docked on any
/// side, put together as tabs or left floating. The workspaces (Design, CAM, Maskin) still decide which
/// panels are shown; a panel that is hidden comes back where the user last had it. The layout is saved per
/// computer when the program closes and can be reset from Visa → Återställ fönsterlayout.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] PanelIds = { "Tools", "Properties", "Machine", "Cam" };
    private const string CanvasId = "Canvas";
    /// <summary>The layout as built from the XAML, to fall back to and to reset to.</summary>
    private string? _defaultDockLayout;
    private Dictionary<string, object>? _dockContents;

    private bool _propertiesVisible => Anchorable("Properties")?.IsVisible == true;
    private bool _camVisible => Anchorable("Cam")?.IsVisible == true;

    /// <summary>The panel with this id, wherever it is now (docked, floating or hidden).</summary>
    private LayoutAnchorable? Anchorable(string id) =>
        DockManager?.Layout?.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == id);

    private void InitDocking()
    {
        _dockContents = new Dictionary<string, object>
        {
            ["Tools"] = ToolsPanel, ["Properties"] = PropertiesPanel, ["Machine"] = MachinePanel, ["Cam"] = CamPanel, [CanvasId] = CanvasArea
        };
        try { _defaultDockLayout = SerializeLayout(); } catch (Exception) { _defaultDockLayout = null; }
        var path = AppStorage.Current.DockLayoutPath;
        try
        {
            if (File.Exists(path) && !LoadLayout(File.ReadAllText(path)) && _defaultDockLayout is not null)
                LoadLayout(_defaultDockLayout);      // an old or damaged file: start from the original layout
        }
        catch (Exception ex)
        {
            App.LogError(new InvalidOperationException("Fönsterlayouten kunde inte läsas.", ex));
            if (_defaultDockLayout is not null) try { LoadLayout(_defaultDockLayout); } catch (Exception) { }
        }
    }

    private string SerializeLayout()
    {
        using var writer = new StringWriter();
        new XmlLayoutSerializer(DockManager).Serialize(writer);
        return writer.ToString();
    }

    /// <summary>Loads a saved layout, giving each panel back its own controls. False if a panel or the drawing area would be missing.</summary>
    private bool LoadLayout(string xml)
    {
        // The controls are moved into the new layout: let go of them in the old one first.
        foreach (var content in DockManager.Layout.Descendents().OfType<LayoutContent>()) content.Content = null;
        var serializer = new XmlLayoutSerializer(DockManager);
        serializer.LayoutSerializationCallback += (_, e) =>
        {
            if (e.Model.ContentId is string id && _dockContents!.TryGetValue(id, out var content)) e.Content = content;
            else e.Cancel = true;                    // something this version does not know
        };
        using (var reader = new StringReader(xml)) serializer.Deserialize(reader);
        var ids = DockManager.Layout.Descendents().OfType<LayoutContent>().Where(c => c.Content is not null).Select(c => c.ContentId).ToHashSet();
        return PanelIds.Append(CanvasId).All(ids.Contains);
    }

    private void SaveDockLayout()
    {
        try
        {
            Directory.CreateDirectory(AppStorage.Current.LocalFolder);
            AppStorage.WriteAtomic(AppStorage.Current.DockLayoutPath, SerializeLayout());
        }
        catch (Exception ex) { App.LogError(new InvalidOperationException("Fönsterlayouten kunde inte sparas.", ex)); }   // never stop the program from closing
    }

    private void ResetDockLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_defaultDockLayout is null) return;
        try
        {
            LoadLayout(_defaultDockLayout);
            try { File.Delete(AppStorage.Current.DockLayoutPath); } catch (Exception) { }
            SetWorkspace(_workspace);                // the workspace decides again which panels are shown
            StatusText.Text = "Fönsterlayouten är återställd.";
        }
        catch (Exception ex) { Error("Fönsterlayouten kunde inte återställas: " + ex.Message); }
    }

    /// <summary>Shows or hides a panel. A shown panel comes back where it was – docked, as a tab or floating.</summary>
    private void SetPanelVisible(string id, bool visible)
    {
        if (Anchorable(id) is not { } a) return;
        if (visible && !a.IsVisible) a.Show();
        else if (!visible && a.IsVisible) a.Hide();
        if (visible && a.IsFloating == false && a.Parent is LayoutAnchorablePane pane) pane.SelectedContentIndex = pane.IndexOfChild(a);
        if (id == "Cam" && visible) RefreshCamSelection();
        Dispatcher.BeginInvoke(new Action(FitToMaterial), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ShowPanel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string id)
        {
            SetPanelVisible(id, true);
            if (Anchorable(id) is { } a) a.IsActive = true;
        }
    }
}
