using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CNCStudio.Core;
using CNCStudio.Core.GCode;
using Microsoft.Win32;

namespace CNCStudio.Desktop;

/// <summary>
/// The operations tree at the top of the CAM panel: one node per program (= tool, in run order) with
/// its operations, and the objects without CAM. Clicking selects in the drawing; right-click offers
/// remove, copy/paste settings and export of one program.
/// </summary>
public partial class MainWindow
{
    private string? _camTreeKey;
    private bool _syncingTree;
    private CamOperation? _copiedCam;

    private static readonly SolidColorBrush TreeOk = Frozen(0x4C, 0xC3, 0x7A), TreeWarn = Frozen(0xF2, 0xB1, 0x3C),
        TreeCrit = Frozen(0xFF, 0x5A, 0x5A), TreeStale = Frozen(0x8C, 0xA3, 0xBA), TreeMuted = Frozen(0x8C, 0xA3, 0xBA),
        TreeInk = Frozen(0xE8, 0xF1, 0xFA);

    private static SolidColorBrush Frozen(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }

    private static Brush StatusBrush(CamTreeStatus s) => s switch
    {
        CamTreeStatus.Ok or CamTreeStatus.Info => TreeOk, CamTreeStatus.Warning => TreeWarn,
        CamTreeStatus.Critical or CamTreeStatus.NoPath => TreeCrit, _ => TreeStale
    };

    private static string StatusName(CamTreeStatus s) => s switch
    {
        CamTreeStatus.Ok or CamTreeStatus.Info => "Kontrollerad", CamTreeStatus.Warning => "Varning",
        CamTreeStatus.Critical => "Kritiskt fel", CamTreeStatus.NoPath => "Ingen körbana", _ => "Körbana inaktuell – klicka Beräkna körbanor"
    };

    /// <summary>Rebuilds the tree when the operations or their status change (not on every redraw).</summary>
    private void UpdateCamTree()
    {
        if (CamTreeView is null) return;
        var tree = CamTree.Build(_document, _camReport);
        var key = string.Join("|", tree.Programs.SelectMany(p => p.Operations.Select(o =>
                      $"{p.Number}:{p.ToolLabel}:{o.Shape.Id}:{o.KindLabel}:{o.Detail}:{o.Status}:{Math.Round(o.CuttingMinutes, 1)}")))
                  + "#" + string.Join(",", tree.Unassigned.Select(u => u.Shape.Id));
        if (key != _camTreeKey)
        {
            _camTreeKey = key;
            BuildCamTree(tree);
        }
        SyncTreeSelection();
    }

    private void BuildCamTree(CamTree tree)
    {
        _syncingTree = true;
        try
        {
            CamTreeView.Items.Clear();
            string F(double v) => v.ToString("0", CultureInfo.InvariantCulture);
            foreach (var program in tree.Programs)
            {
                var header = Row(StatusBrush(program.Status), $"Program {program.Number} – {program.ToolLabel}",
                    program.CuttingMinutes >= 0.5 ? $"~{F(Math.Max(1, program.CuttingMinutes))} min" : "", bold: true);
                var node = new TreeViewItem { Header = header, Tag = program, IsExpanded = true,
                    ToolTip = tree.Programs.Count > 1 ? $"Eget program: sätt i {program.ToolLabel} och nolla Z innan det körs." : null,
                    ContextMenu = ProgramMenu(program) };
                foreach (var op in program.Operations)
                    node.Items.Add(new TreeViewItem
                    {
                        Header = Row(StatusBrush(op.Status), $"{op.KindLabel}  {op.ShapeLabel}", op.Detail, bold: false),
                        Tag = op, ToolTip = OperationTip(op), ContextMenu = OperationMenu(op.Shape, hasCam: true)
                    });
                CamTreeView.Items.Add(node);
            }
            if (tree.Unassigned.Count > 0)
            {
                var node = new TreeViewItem { Header = Row(TreeStale, "Ej tilldelade", $"{tree.Unassigned.Count} objekt", bold: true, hollow: true),
                    IsExpanded = tree.Programs.Count == 0, ToolTip = "Objekt utan bearbetning – de fräses inte." };
                foreach (var (shape, label) in tree.Unassigned)
                    node.Items.Add(new TreeViewItem { Header = Row(TreeStale, label, "ingen CAM", bold: false, hollow: true), Tag = shape,
                        ContextMenu = OperationMenu(shape, hasCam: false) });
                CamTreeView.Items.Add(node);
            }
            if (CamTreeView.Items.Count == 0)
                CamTreeView.Items.Add(new TreeViewItem { Header = new TextBlock { Text = "Inga objekt ännu.", Foreground = TreeMuted }, Focusable = false });
        }
        finally { _syncingTree = false; }
    }

    private static string OperationTip(CamTreeOperation op)
    {
        var sb = new StringBuilder($"{op.KindLabel} · {op.ShapeLabel} · Ø {op.Operation.ToolDiameter:0.###} mm · {op.Detail}\n{StatusName(op.Status)}");
        foreach (var f in op.Findings.Where(f => f.Severity != CamSeverity.Info).Take(4))
            sb.Append($"\n• {f.Message}");
        return sb.ToString();
    }

    private static FrameworkElement Row(Brush status, string text, string detail, bool bold, bool hollow = false)
    {
        var dot = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = hollow ? Brushes.Transparent : status, Stroke = status, StrokeThickness = 1.4 };
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1), Children =
        {
            dot,
            new TextBlock { Text = text, Foreground = TreeInk, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal },
            new TextBlock { Text = detail.Length == 0 ? "" : "   " + detail, Foreground = TreeMuted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }
        } };
    }

    // ---- Selection: tree ⇄ drawing ----

    private void CamTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingTree || (e.NewValue as TreeViewItem)?.Tag is not object tag) return;
        switch (tag)
        {
            case CamTreeOperation op: SelectOnly(new[] { op.Shape }); break;
            case DesignShape shape: SelectOnly(new[] { shape }); break;
            case CamTreeProgram program: SelectOnly(program.Operations.Select(o => o.Shape).ToList()); break;
        }
    }

    private void SelectOnly(IReadOnlyList<DesignShape> shapes)
    {
        _selection.Clear(); _selectionOrder.Clear();
        foreach (var s in shapes.Where(_document.Shapes.Contains)) { _selection.Add(s); _selectionOrder.Add(s.Id); }
        _selected = shapes.FirstOrDefault(_document.Shapes.Contains);
        RefreshCamSelection();
        Render();
    }

    /// <summary>Highlights the tree row of the object selected in the drawing.</summary>
    private void SyncTreeSelection()
    {
        if (CamTreeView is null) return;
        _syncingTree = true;
        try
        {
            foreach (var group in CamTreeView.Items.OfType<TreeViewItem>())
                foreach (var item in group.Items.OfType<TreeViewItem>())
                {
                    var shape = item.Tag switch { CamTreeOperation o => o.Shape, DesignShape s => s, _ => null };
                    bool on = shape is not null && _selected is not null && shape.Id == _selected.Id && _selection.Count == 1;
                    if (item.IsSelected != on && (on || item.IsSelected)) item.IsSelected = on;
                }
        }
        finally { _syncingTree = false; }
    }

    // ---- Right-click ----

    private ContextMenu OperationMenu(DesignShape shape, bool hasCam)
    {
        var menu = new ContextMenu();
        void Add(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        if (hasCam)
        {
            Add("Kopiera CAM-inställningar", () =>
            {
                _copiedCam = _document.CamOperations.FirstOrDefault(o => o.ShapeId == shape.Id);
                StatusText.Text = "CAM-inställningar kopierade – högerklicka på ett annat objekt och välj Klistra in.";
            });
        }
        Add(_copiedCam is null ? "Klistra in CAM-inställningar" : $"Klistra in CAM-inställningar ({CamTree.KindLabel(_copiedCam.Kind)}, Ø {_copiedCam.ToolDiameter:0.###} mm)",
            () => PasteCam(shape), _copiedCam is not null);
        if (hasCam)
            Add("Ta bort CAM", () =>
            {
                RememberChange();
                _document.CamOperations.RemoveAll(o => o.ShapeId == shape.Id);
                StatusText.Text = $"CAM borttagen från {CamPlanner.Label(_document, shape)}.";
                RefreshCamSelection(); Render();
            });
        // Rebuilt when opened so "Klistra in" reflects what was copied last.
        menu.Opened += (_, _) =>
        {
            if (menu.Items.Count == 0) return;
            var paste = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string)?.StartsWith("Klistra in") == true);
            if (paste is null) return;
            paste.IsEnabled = _copiedCam is not null;
            paste.Header = _copiedCam is null ? "Klistra in CAM-inställningar"
                : $"Klistra in CAM-inställningar ({CamTree.KindLabel(_copiedCam.Kind)}, Ø {_copiedCam.ToolDiameter:0.###} mm)";
        };
        return menu;
    }

    private void PasteCam(DesignShape target)
    {
        if (_copiedCam is null) return;
        var op = _copiedCam.CopyFor(target.Id);
        if (CamValidation.GetError(op, _document.MaterialThickness) is string err) { Error(err); return; }
        RememberChange();
        _document.CamOperations.RemoveAll(o => o.ShapeId == target.Id);
        _document.CamOperations.Add(op);
        StatusText.Text = $"CAM-inställningar inklistrade på {CamPlanner.Label(_document, target)}.";
        RefreshCamSelection(); Render();
    }

    /// <summary>
    /// CAM → Planfräs hela materialet: a rectangle over the whole material with a facing operation set up
    /// in the CAM panel (pick the cutter and depth, then Spara CAM). Facing always runs first.
    /// </summary>
    private void FaceStock_Click(object sender, RoutedEventArgs e)
    {
        if (!_document.HasMaterial) { Error("Lägg till material först (Egenskaper → Projekt och material)."); return; }
        RememberChange();
        var rect = new DesignShape { Kind = ShapeKind.Rectangle, X = 0, Y = 0, Width = _document.MaterialWidth, Height = _document.MaterialHeight };
        _document.Shapes.Insert(0, rect);
        SetWorkspace(Workspace.Cam);
        SelectOnly(new[] { rect });
        CamKind.SelectedIndex = (int)CamOperationKind.Face;
        CamDepth.Text = "0.5";
        CamPass.Text = "0.5";
        StatusText.Text = "Planfräsning över hela materialet: välj fräs (gärna en planfräs), kontrollera djup och stegning och klicka Spara CAM.";
    }
    private ContextMenu ProgramMenu(CamTreeProgram program)
    {
        var menu = new ContextMenu();
        var export = new MenuItem { Header = $"Exportera bara program {program.Number} ({program.ToolLabel})…" };
        export.Click += (_, _) => ExportOneProgram(program.ToolLabel);
        menu.Items.Add(export);
        return menu;
    }

    /// <summary>Exports the program of one tool (all checks as for a full export).</summary>
    private void ExportOneProgram(string toolLabel)
    {
        if (GenerateCheckedJobs("Exportera program") is not { } jobs) return;
        var job = jobs.FirstOrDefault(j => j.ToolLabel == toolLabel);
        if (job is null) { Error("Programmet finns inte längre – beräkna körbanorna igen."); return; }
        if (!ConfirmProgram(job, "Exportera program", "Exportera")) return;
        var dialog = new SaveFileDialog
        {
            Filter = "G-code för GRBL (*.nc)|*.nc|G-code (*.gcode)|*.gcode",
            FileName = SafeFileName(_document.Name) + (job.Count > 1 ? $" {job.Number} av {job.Count} - {SafeFileName(job.ToolLabel)}" : "") + ".nc",
            InitialDirectory = ExportFolder()
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, job.Result.Text, Encoding.ASCII);
            StatusText.Text = $"Program {job.Number} sparat: {dialog.FileName} ({job.Result.Lines.Count} rader).";
        }
        catch (Exception ex) { Error("Kunde inte spara G-code: " + ex.Message); }
    }
}
