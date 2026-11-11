using System.Windows;
using System.Windows.Controls;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Maskinkontroll → Maskin: one profile per machine (the demo machine is one of them). The chosen
/// machine's profile is the project's machine profile; connecting on a port picks the machine that
/// last used that port, and the demo port always picks the demo machine.
/// </summary>
public partial class MainWindow
{
    private bool _syncingMachinePicker;

    private void RefreshMachinePicker()
    {
        if (MachinePicker is null) return;
        _syncingMachinePicker = true;
        try
        {
            var all = AppStorage.Current.Machines.All();
            MachinePicker.ItemsSource = all;
            // A project from before the machine library has no machine Id: show the active machine.
            var id = _document.Machine.Id ?? AppStorage.Current.Machines.Active.Id;
            MachinePicker.SelectedItem = all.FirstOrDefault(p => p.Id == id);
            bool connected = _grbl is not null;
            MachinePicker.IsEnabled = NewMachineButton.IsEnabled = DeleteMachineButton.IsEnabled = !connected;
            MachinePicker.ToolTip = connected
                ? "Koppla från för att byta maskin."
                : "Vilken maskin du arbetar mot. Varje maskin har en egen profil – demomaskinen också. Ansluter du på en port väljs maskinen som senast använde den.";
        }
        catch (Exception ex) { StatusText.Text = "Maskinerna kunde inte läsas: " + ex.Message; }
        finally { _syncingMachinePicker = false; }
    }

    private void MachinePicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingMachinePicker || MachinePicker.SelectedItem is not MachineProfile p) return;
        if (_grbl is not null) { RefreshMachinePicker(); return; }
        UseMachine(p);
        StatusText.Text = $"Maskin: {p.Name} ({p.WorkWidthMm:0.#} × {p.WorkLengthMm:0.#} mm).";
    }

    /// <summary>Makes <paramref name="p"/> the active machine and the project's machine profile, and picks its port.</summary>
    private void UseMachine(MachineProfile p)
    {
        AppStorage.Current.Machines.SetActive(p.Id!);
        if (_document.Machine.Id != p.Id)
        {
            RememberChange();
            _document.Machine = MachineLibrary.Clone(p);
        }
        if (PortPicker.ItemsSource is IEnumerable<string> ports)
        {
            if (p.Port is string port && ports.Contains(port)) PortPicker.SelectedItem = port;
            // A real machine never used yet: leave the demo port for the first real port, if there is one.
            else if (!MachineLibrary.IsDemo(p) && PortPicker.SelectedItem as string == DemoPort && ports.FirstOrDefault(x => x != DemoPort) is string real)
                PortPicker.SelectedItem = real;
        }
        RefreshMachineSummary(); RefreshMotionFields(); ShowStockFields(); Render();
    }

    /// <summary>
    /// Before connecting on <paramref name="port"/>: the demo port is always the demo machine; another port
    /// uses the machine last connected there, otherwise the chosen (real) machine, which then remembers the port.
    /// </summary>
    private void UseMachineForPort(string port)
    {
        var lib = AppStorage.Current.Machines;
        var machine = lib.ForPort(port);
        if (machine is null)
        {
            machine = lib.Find(_document.Machine.Id) is { } current && !MachineLibrary.IsDemo(current)
                ? current
                : lib.All().First(p => !MachineLibrary.IsDemo(p));
            machine.Port = port;
            lib.Save(machine);
            if (_document.Machine.Id == machine.Id) _document.Machine.Port = port;
        }
        if (machine.Id != _document.Machine.Id) Log($"Maskin för {port}: {machine.Name}");
        UseMachine(machine);
    }

    private void NewMachine_Click(object sender, RoutedEventArgs e) => CreateMachine();

    private void DeleteMachine_Click(object sender, RoutedEventArgs e)
    {
        if (MachinePicker.SelectedItem is MachineProfile p) DeleteMachine(p);
    }

    /// <summary>Maskin → Maskiner…: the list of all machines.</summary>
    private void Machines_Click(object sender, RoutedEventArgs e)
    {
        new MachinesWindow(
            chosenId: () => _document.Machine.Id ?? AppStorage.Current.Machines.Active.Id,
            connectedId: () => _grbl is null ? null : _document.Machine.Id,   // connecting picks the machine, so it is the chosen one
            use: p => { UseMachine(p); StatusText.Text = $"Maskin: {p.Name}."; },
            edit: EditMachine,
            delete: DeleteMachine,
            create: CreateMachine) { Owner = this }.ShowDialog();
    }

    /// <summary>A new machine as a copy of the chosen one, then Maskininställningar to name and size it.</summary>
    private void CreateMachine()
    {
        if (_grbl is not null) { Error("Koppla från innan du lägger till en maskin."); return; }
        var lib = AppStorage.Current.Machines;
        var p = lib.CreateNew(lib.UnusedName("Ny maskin"), _document.Machine);
        UseMachine(p);
        StatusText.Text = $"{p.Name} skapad som kopia av den förra – ge den namn och mått i Maskininställningar.";
        MachineSettings_Click(this, new RoutedEventArgs());
    }

    /// <summary>Maskininställningar for <paramref name="p"/>; choosing it first if it is not the chosen machine (only when not connected).</summary>
    private void EditMachine(MachineProfile p)
    {
        if (p.Id != _document.Machine.Id)
        {
            if (_grbl is not null) { Error("Koppla från för att ändra en annan maskin än den anslutna."); return; }
            UseMachine(p);
        }
        MachineSettings_Click(this, new RoutedEventArgs());
    }

    private bool DeleteMachine(MachineProfile p)
    {
        if (_grbl is not null) { Error("Koppla från innan du tar bort en maskin."); return false; }
        if (MachineLibrary.IsDemo(p)) { Error("Demomaskinen kan inte tas bort."); return false; }
        if (AppDialog.Show(this, $"Ta bort maskinen \"{p.Name}\" och dess profil? Projekt som redan är sparade behåller sina värden.",
                "Ta bort maskin", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return false;
        if (AppStorage.Current.Machines.Delete(p.Id!) is string error) { Error(error); return false; }
        if (p.Id == _document.Machine.Id) UseMachine(AppStorage.Current.Machines.Active);
        else RefreshMachinePicker();
        StatusText.Text = $"Maskinen {p.Name} är borttagen.";
        return true;
    }
}
