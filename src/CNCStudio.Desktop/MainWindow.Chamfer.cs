using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Fasning med V-fräs in the Fräsning panel: the bearbetning Fasning (the V-bit's tip along the edge, the width in
/// the depth field), and "Fasa övre kanten" on contours and pockets – a V-bit pass first (<see cref="CamSecondPass.ChamferFor"/>).
/// </summary>
public partial class MainWindow
{
    private bool _loadingChamferTools;

    /// <summary>The V-bits in the tool library, for the chamfer pass.</summary>
    private void RefreshChamferTools(string? select = null)
    {
        if (CamChamferTool is null) return;
        _loadingChamferTools = true;
        CamChamferTool.Items.Clear();
        foreach (var t in _tools.Tools.Where(t => t.Kind == ToolKind.VBit)) CamChamferTool.Items.Add(t);
        CamChamferTool.SelectedItem = CamChamferTool.Items.Cast<ToolDefinition>().FirstOrDefault(t => t.Name == select);
        _loadingChamferTools = false;
    }

    private ToolDefinition? ChamferTool => CamChamferTool?.SelectedItem as ToolDefinition;

    private void CamChamferTool_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingChamferTools || ChamferTool is not { } t) return;
        CamChamferDiameter.Text = F(t.Diameter);
        CamChamferAngle.Text = F(t.AngleDeg);
        CamInput_Changed(sender, e);
    }

    /// <summary>The chamfer fields from an operation (null = defaults).</summary>
    private void FillChamferFields(CamOperation? op)
    {
        if (CamChamferEnabled is null) return;
        var d = op ?? new CamOperation();
        CamChamferEnabled.IsChecked = op?.ChamferEnabled == true;
        CamChamferWidth.Text = F(d.ChamferWidthMm);
        CamChamferDiameter.Text = F(d.ChamferToolDiameter);
        CamChamferAngle.Text = F(d.ChamferAngleDeg);
        RefreshChamferTools(d.ChamferToolName);
    }

    /// <summary>Puts the chamfer settings on an operation read from the panel; false if a field is not a number.</summary>
    private bool ReadChamfer(CamOperation op)
    {
        if (CamChamferEnabled is null) return true;
        op.ChamferEnabled = op.Kind is CamOperationKind.Outside or CamOperationKind.Inside or CamOperationKind.Pocket && CamChamferEnabled.IsChecked == true;
        if (!op.ChamferEnabled) return true;
        if (!Parse(CamChamferWidth, out var w) || !Parse(CamChamferDiameter, out var d) || !Parse(CamChamferAngle, out var a)) return false;
        op.ChamferWidthMm = w; op.ChamferToolDiameter = d; op.ChamferAngleDeg = a;
        var tool = ChamferTool is { } t && Math.Abs(t.Diameter - d) < 1e-9 && Math.Abs(t.AngleDeg - a) < 1e-9 ? t : null;
        op.ChamferToolName = tool?.Name;
        var cut = tool?.CuttingFor(_document.MaterialId);
        op.ChamferFeedRate = cut?.FeedRate ?? op.FeedRate;
        op.ChamferPlungeRate = cut?.PlungeRate ?? op.PlungeRate;
        op.ChamferSpindleRpm = cut?.SpindleRpm ?? op.SpindleRpm;
        return true;
    }

    private string ChamferSuffix(CamOperation op) => !op.ChamferEnabled ? "" :
        $" · fasning {F(op.ChamferWidthMm)} mm med {op.ChamferToolName ?? $"V-fräs Ø {F(op.ChamferToolDiameter)} mm {F(op.ChamferAngleDeg)}°"} först " +
        $"({F(Math.Round(ChamferToolpaths.Depth(op.ChamferWidthMm, op.ChamferAngleDeg), 2))} mm djupt, eget program)";
}
