using System.IO;
using System.Windows;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Docking (AvalonDock): the drawing area is the document in the middle, and Ritverktyg,
/// Egenskaper, Maskinstyrning, Program and Smart CAM are panels that can be dragged by their title bar, docked on any
/// side, put together as tabs or left floating. The workspaces (Design, CAM, Maskin) still decide which
/// panels are shown; a panel that is hidden comes back where the user last had it. The layout is saved per
/// computer when the program closes and can be reset from Visa → Återställ fönsterlayout.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] PanelIds = { "Tools", "Properties", "Machine", "Job", "Run", "MachineExtras", "Macros", "Cam", "CamJob" };
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
        ModernLookMenuItem.IsChecked = AppStorage.Current.Settings.ModernLook != false;
        _dockContents = new Dictionary<string, object>
        {
            ["Tools"] = ToolsPanel, ["Properties"] = PropertiesPanel, ["Machine"] = MachinePanel, ["Job"] = JobPanel, ["Run"] = RunPanel, ["MachineExtras"] = MachineExtrasPanel, ["Macros"] = MacrosPanel, ["Cam"] = CamPanel, ["CamJob"] = CamJobPanel, [CanvasId] = CanvasArea
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
        ApplyPanelRules();
        DockManager.LayoutChanged += (_, _) => { WatchLayout(); ApplyPanelRules(); };
        WatchLayout();
    }

    /// <summary>
    /// The panels may be docked anywhere except in the drawing area's own tab row: a panel there covers
    /// Arbetsyta. Set in code because a loaded layout does not carry the setting.
    /// </summary>
    private void ApplyPanelRules()
    {
        foreach (var a in DockManager.Layout.Descendents().OfType<LayoutAnchorable>().Concat(DockManager.Layout.Hidden))
            a.CanDockAsTabbedDocument = false;
    }

    private LayoutRoot? _watchedLayout;
    private bool _checkQueued, _restoringCanvas;

    /// <summary>Every change of the layout (a panel docked, floated, hidden) is followed by a check that Arbetsyta is still there.</summary>
    private void WatchLayout()
    {
        if (ReferenceEquals(_watchedLayout, DockManager.Layout)) return;
        if (_watchedLayout is not null) _watchedLayout.Updated -= Layout_Updated;
        _watchedLayout = DockManager.Layout;
        _watchedLayout.Updated += Layout_Updated;
    }

    private void Layout_Updated(object? sender, EventArgs e)
    {
        if (_checkQueued || _restoringCanvas) return;
        _checkQueued = true;
        Dispatcher.BeginInvoke(new Action(() => { _checkQueued = false; EnsureCanvasShown(); KeepFloatingPanelsOnScreen(); }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// A panel made floating (from its ▾ menu or a saved layout) can open partly outside the screen and be
    /// hard to grab. Every floating panel is moved fully onto the screen – not while it is being dragged.
    /// </summary>
    private void KeepFloatingPanelsOnScreen()
    {
        if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            // Try again shortly after the drag ends.
            var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            later.Tick += (_, _) => { later.Stop(); KeepFloatingPanelsOnScreen(); };
            later.Start();
            return;
        }
        foreach (var window in DockManager.FloatingWindows)
        {
            if (!window.IsVisible || window.WindowState != WindowState.Normal) continue;
            double w = window.ActualWidth > 0 ? window.ActualWidth : window.Width, h = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
            var (left, top) = WindowPlacement.KeepOnScreen(window.Left, window.Top, w, h,
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (Math.Abs(left - window.Left) > 0.5) window.Left = left;
            if (Math.Abs(top - window.Top) > 0.5) window.Top = top;
        }
    }

    /// <summary>
    /// Arbetsyta must stay in the main window, in front in its own pane. If a docking move took it away
    /// (no pane, floating, hidden, or a panel in front of it), it is put back – the whole layout only as a last resort.
    /// </summary>
    private void EnsureCanvasShown()
    {
        var canvas = DockManager.Layout.Descendents().OfType<LayoutDocument>().FirstOrDefault(d => d.ContentId == CanvasId);
        bool inMainWindow = canvas?.Parent is LayoutDocumentPane pane && pane.FindParent<LayoutFloatingWindow>() is null
                            && ReferenceEquals(pane.Root, DockManager.Layout) && canvas.Content is not null;
        if (inMainWindow)
        {
            var docPane = (LayoutDocumentPane)canvas!.Parent;
            // A panel that got into the drawing area's tab row anyway goes back to being a floating panel.
            foreach (var other in docPane.Children.OfType<LayoutAnchorable>().ToList()) other.Float();
            if (docPane.SelectedContent != canvas) docPane.SelectedContentIndex = docPane.IndexOfChild(canvas);
            // Sizes are only known after the layout pass.
            Dispatcher.BeginInvoke(new Action(() => CheckCanvasSize(canvas)), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        RestoreDefaultLayout();
    }

    /// <summary>The smallest drawing area that still counts as "there" (WPF units).</summary>
    private const double MinCanvasWidth = 200, MinCanvasHeight = 150;
    private bool _canvasSizeRepaired;

    /// <summary>
    /// Docking a panel back (e.g. Maskinstyrning dragged out and then onto a dock icon) can give the new pane all
    /// the width, so Arbetsyta is squeezed to nothing while it is still in the layout. Then Arbetsyta gets the
    /// remaining space again and its neighbours a fixed size; if that does not help, the layout is reset.
    /// </summary>
    private void CheckCanvasSize(LayoutDocument canvas)
    {
        if (_restoringCanvas || !IsLoaded || WindowState == WindowState.Minimized) return;
        if (DockManager.ActualWidth < MinCanvasWidth * 2 || DockManager.ActualHeight < MinCanvasHeight * 2) return;   // the window itself is tiny
        bool fits = CanvasArea.ActualWidth >= MinCanvasWidth && CanvasArea.ActualHeight >= MinCanvasHeight;
        if (fits) { _canvasSizeRepaired = false; return; }
        if (!_canvasSizeRepaired)
        {
            _canvasSizeRepaired = true;
            GiveCanvasTheSpace(canvas);
            Dispatcher.BeginInvoke(new Action(() => CheckCanvasSize(canvas)), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }
        _canvasSizeRepaired = false;
        RestoreDefaultLayout();
    }

    /// <summary>From Arbetsyta's pane up to the root: Arbetsyta's branch takes the rest (1*), panels beside it a fixed size.</summary>
    private void GiveCanvasTheSpace(LayoutDocument canvas)
    {
        DockManager.Layout.CollectGarbage();          // empty panes left behind by a moved panel take no room
        ILayoutElement branch = canvas.Parent;
        while (branch.Parent is ILayoutOrientableGroup group and ILayoutContainer container)
        {
            bool horizontal = group.Orientation == System.Windows.Controls.Orientation.Horizontal;
            foreach (var sibling in container.Children)
            {
                if (DockSize(sibling, horizontal) is not GridLength size) continue;
                if (ReferenceEquals(sibling, branch)) SetDockSize(sibling, horizontal, new GridLength(1, GridUnitType.Star));
                else if (size.IsStar) SetDockSize(sibling, horizontal, new GridLength(horizontal ? 340 : 260));
            }
            branch = (ILayoutElement)group;
        }
    }

    // AvalonDock keeps the common size interface internal; these are the element kinds a layout is made of.
    private static GridLength? DockSize(ILayoutElement e, bool width) => e switch
    {
        LayoutPanel p => width ? p.DockWidth : p.DockHeight,
        LayoutDocumentPaneGroup g => width ? g.DockWidth : g.DockHeight,
        LayoutDocumentPane d => width ? d.DockWidth : d.DockHeight,
        LayoutAnchorablePaneGroup g => width ? g.DockWidth : g.DockHeight,
        LayoutAnchorablePane a => width ? a.DockWidth : a.DockHeight,
        _ => null,
    };

    private static void SetDockSize(ILayoutElement e, bool width, GridLength size)
    {
        switch (e)
        {
            case LayoutPanel p: if (width) p.DockWidth = size; else p.DockHeight = size; break;
            case LayoutDocumentPaneGroup g: if (width) g.DockWidth = size; else g.DockHeight = size; break;
            case LayoutDocumentPane d: if (width) d.DockWidth = size; else d.DockHeight = size; break;
            case LayoutAnchorablePaneGroup g: if (width) g.DockWidth = size; else g.DockHeight = size; break;
            case LayoutAnchorablePane a: if (width) a.DockWidth = size; else a.DockHeight = size; break;
        }
    }

    private void RestoreDefaultLayout()
    {
        if (_defaultDockLayout is null) return;
        _restoringCanvas = true;
        try
        {
            LoadLayout(_defaultDockLayout);
            ApplyPanelRules();
            WatchLayout();
            SetWorkspace(_workspace);
            StatusText.Text = "Arbetsytan försvann vid dockningen – fönsterlayouten är återställd (Visa → Återställ fönsterlayout gör samma sak).";
        }
        catch (Exception ex) { App.LogError(new InvalidOperationException("Arbetsytan kunde inte återställas.", ex)); }
        finally { _restoringCanvas = false; }
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

    /// <summary>Visa → Nytt utseende: saved for the next start (the look is chosen before the first window opens).</summary>
    private void ModernLook_Click(object sender, RoutedEventArgs e)
    {
        AppStorage.Current.Settings.ModernLook = ModernLookMenuItem.IsChecked;
        try { AppStorage.Current.SaveSettings(); } catch (Exception ex) { Error("Inställningen kunde inte sparas: " + ex.Message); return; }
        AppDialog.Show(this, ModernLookMenuItem.IsChecked
                ? "Det nya utseendet används nästa gång CNC Studio startas."
                : "Windows egna knappar och fält används nästa gång CNC Studio startas.",
            "Utseende", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ResetDockLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_defaultDockLayout is null) return;
        try
        {
            LoadLayout(_defaultDockLayout);
            ApplyPanelRules();
            WatchLayout();
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
