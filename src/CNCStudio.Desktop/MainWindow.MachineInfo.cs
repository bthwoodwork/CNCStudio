using System.Windows;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>Maskin → Maskininformation…: one window, kept open beside the work if wanted.</summary>
public partial class MainWindow
{
    private MachineInfoWindow? _machineInfo;

    private void MachineInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_machineInfo is { IsLoaded: true }) { _machineInfo.Activate(); return; }
        _machineInfo = new MachineInfoWindow(this) { Owner = this };
        _machineInfo.Closed += (_, _) => _machineInfo = null;
        _machineInfo.Show();
    }

    internal string MachineName => _document.Machine.Name;
    internal string? ConnectedFirmware => _connectedFirmware;
    /// <summary>Nothing else is talking to the machine: no program, macro or jog.</summary>
    internal bool CanQueryMachine => _runner is null && _macroRunner is null && !_jogHeld;

    internal void SaveIdleDelayBeforeLock(int value) => UpdateMachineLog(log => log.IdleDelayBeforeLock = value);

    internal int? IdleDelayBeforeLock()
    {
        var id = _document.Machine.Id;
        return id is null ? null : MachineLog.Load(AppStorage.Current.Machines.MachineLogPath(id)).IdleDelayBeforeLock;
    }
}
