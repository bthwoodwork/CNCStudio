using System.IO;
using System.Windows;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Where projects and shared settings live: locally, or in a sync folder (Google Drive for desktop)
/// so the same projects, tool library and machine profile are available on every computer.
/// </summary>
public partial class MainWindow
{
    private void SyncFolder_Click(object sender, RoutedEventArgs e)
    {
        var w = new SyncFolderWindow(AppStorage.Current) { Owner = this };
        w.ShowDialog();
        if (!w.Changed) return;
        _tools = LoadTools();
        _materials = LoadMaterials();
        RefreshMaterialPicker();
        var current = SelectedTool?.Name;
        RefreshToolPicker();
        SelectTool(current);
        ShowStorageStatus();
    }

    private void ShowStorageStatus()
    {
        var s = AppStorage.Current;
        try { s.EnsureFolders(); } catch (Exception) { /* shown below if the sync folder is missing */ }
        if (s.SyncFolderMissing)
        {
            StatusText.Text = $"Synkmappen {s.Settings.SyncFolder} hittas inte (är Google Drive igång?) – sparar bara på den här datorn tills den syns.";
            AppDialog.Show(this, $"Synkmappen hittas inte:\n{s.Settings.SyncFolder}\n\nÄr Google Drive för datorn igång och inloggat? " +
                "Tills mappen syns igen läser och sparar CNC Studio bara på den här datorn.\n\nStarta om CNC Studio när Google Drive är igång.",
                "Synkmapp", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (s.ActiveSyncFolder is string f)
            StatusText.Text = "Projekt och inställningar synkas via " + f + ".";
    }

    /// <summary>Projects folder for Open/Save – created if needed; empty string lets Windows choose.</summary>
    private static string ProjectsFolder()
    {
        try { Directory.CreateDirectory(AppStorage.Current.ProjectsFolder); return AppStorage.Current.ProjectsFolder; }
        catch (Exception) { return ""; }
    }

    /// <summary>A new project starts with the saved default machine profile (shared via the sync folder).</summary>
    private static CncDocument NewDocument()
    {
        var doc = new CncDocument();
        if (AppStorage.Current.LoadMachineProfile() is MachineProfile profile) doc.Machine = profile;
        return doc;
    }

    /// <summary>The machine profile also becomes the default for new projects (on every computer when synced).</summary>
    private void SaveDefaultMachineProfile(MachineProfile profile)
    {
        try { AppStorage.Current.SaveMachineProfile(profile); }
        catch (Exception ex) { StatusText.Text = "Maskinprofilen kunde inte sparas som standard: " + ex.Message; }
    }
}
