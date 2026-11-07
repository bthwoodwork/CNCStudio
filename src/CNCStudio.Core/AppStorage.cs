using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CNCStudio.Core;

/// <summary>Per-computer settings. Always stored locally: it says where everything else lives.</summary>
public sealed class AppSettings
{
    /// <summary>A folder that a sync client (Google Drive for desktop, OneDrive, Dropbox) keeps in step
    /// between computers. Null = everything stays on this computer.</summary>
    public string? SyncFolder { get; set; }
}

/// <summary>
/// Where CNC Studio keeps projects and shared settings. With a sync folder (e.g. "G:\Min enhet\CNC Studio",
/// synced by Google Drive for desktop) the projects, the tool library and the default machine profile are
/// shared between computers. Machine-specific things stay local on purpose: interrupted jobs, logs and
/// $$ backups belong to the computer connected to the machine.
/// </summary>
public sealed class AppStorage
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public const string SettingsFolderName = "Inställningar";
    public const string ProjectsFolderName = "Projekt";

    private readonly string _local;
    private readonly string _defaultProjects;

    public AppStorage(string localFolder, string defaultProjectsFolder)
    {
        _local = localFolder;
        _defaultProjects = defaultProjectsFolder;
        Settings = LoadSettings();
    }

    /// <summary>The storage the app uses: %APPDATA%\CNCStudio and Dokument\CNC Studio.</summary>
    public static AppStorage Current { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CNCStudio"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CNC Studio"));

    public AppSettings Settings { get; private set; }
    public string LocalFolder => _local;
    public string SettingsFile => Path.Combine(_local, "settings.json");

    /// <summary>The sync folder if one is chosen AND reachable now (Drive may not be running).</summary>
    public string? ActiveSyncFolder => Settings.SyncFolder is string f && Directory.Exists(f) ? f : null;
    public bool SyncFolderMissing => Settings.SyncFolder is not null && ActiveSyncFolder is null;

    public string SharedSettingsFolder => ActiveSyncFolder is string f ? Path.Combine(f, SettingsFolderName) : _local;
    public string ProjectsFolder => ActiveSyncFolder is string f ? Path.Combine(f, ProjectsFolderName) : _defaultProjects;
    public string ToolLibraryPath => Path.Combine(SharedSettingsFolder, "tools.json");
    public string MachineProfilePath => Path.Combine(SharedSettingsFolder, "maskinprofil.json");

    /// <summary>
    /// Uses <paramref name="folder"/> as the sync folder (null = stop syncing). Creates its subfolders and
    /// copies this computer's tool library and machine profile there if the folder has none yet – files
    /// already in the sync folder (from another computer) are never overwritten.
    /// </summary>
    public IReadOnlyList<string> SetSyncFolder(string? folder)
    {
        var copied = new List<string>();
        if (folder is not null)
        {
            folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(Path.Combine(folder, SettingsFolderName));
            Directory.CreateDirectory(Path.Combine(folder, ProjectsFolderName));
            foreach (var name in new[] { "tools.json", "maskinprofil.json" })
            {
                var from = Path.Combine(SharedSettingsFolder, name);
                var to = Path.Combine(folder, SettingsFolderName, name);
                if (File.Exists(from) && !File.Exists(to)) { File.Copy(from, to); copied.Add(name); }
            }
        }
        Settings = new AppSettings { SyncFolder = folder };
        Directory.CreateDirectory(_local);
        WriteAtomic(SettingsFile, JsonSerializer.Serialize(Settings, Json));
        return copied;
    }

    public MachineProfile? LoadMachineProfile()
    {
        try { return File.Exists(MachineProfilePath) ? JsonSerializer.Deserialize<MachineProfile>(File.ReadAllText(MachineProfilePath), Json) : null; }
        catch (Exception) { return null; }
    }

    public void SaveMachineProfile(MachineProfile profile)
    {
        Directory.CreateDirectory(SharedSettingsFolder);
        WriteAtomic(MachineProfilePath, JsonSerializer.Serialize(profile, Json));
    }

    /// <summary>
    /// Folders where Google Drive for desktop usually puts "My Drive": a drive letter with "Min enhet" /
    /// "My Drive" (the default streaming mode) or a "Google Drive" folder in the user profile (mirror mode).
    /// </summary>
    public static IReadOnlyList<string> FindGoogleDriveFolders()
    {
        var found = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                foreach (var name in new[] { "Min enhet", "My Drive" })
                {
                    var p = Path.Combine(d.RootDirectory.FullName, name);
                    if (Directory.Exists(p)) found.Add(p);
                }
            }
            catch (Exception) { /* drive not accessible */ }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in new[] { "Google Drive", Path.Combine("Google Drive", "Min enhet"), Path.Combine("Google Drive", "My Drive"), "My Drive" })
        {
            var p = Path.Combine(home, name);
            if (Directory.Exists(p)) found.Add(p);
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Writes to a temporary file and then replaces the target, so a sync client never picks up half a file.</summary>
    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    private AppSettings LoadSettings()
    {
        try { return File.Exists(SettingsFile) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), Json) ?? new() : new(); }
        catch (Exception) { return new(); }
    }
}
