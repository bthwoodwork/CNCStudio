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
/// Where CNC Studio keeps its files. One data folder ("CNC Studio") – the sync folder when one is chosen
/// (e.g. "G:\Min enhet\CNC Studio"), otherwise Dokument\CNC Studio – with these subfolders:
/// <list type="bullet">
/// <item>Projekt – the projects</item>
/// <item>Verktyg – the tool library (cutters in categories, with cutting data per material)</item>
/// <item>Material – the material library</item>
/// <item>Inställningar – the default machine profile for new projects</item>
/// <item>Loggar\&lt;dator&gt; – each computer's logs (error log, machine sessions, first test, $$ backups)</item>
/// </list>
/// Interrupted jobs stay local on purpose: they belong to the computer connected to the machine.
/// </summary>
public sealed class AppStorage
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public const string ProjectsFolderName = "Projekt";
    public const string ToolsFolderName = "Verktyg";
    public const string MaterialsFolderName = "Material";
    public const string SettingsFolderName = "Inställningar";
    public const string LogsFolderName = "Loggar";

    private static readonly string[] SharedFiles =
    {
        Path.Combine(ToolsFolderName, "verktyg.json"),
        Path.Combine(MaterialsFolderName, "material.json"),
        Path.Combine(SettingsFolderName, "maskinprofil.json"),
    };

    private readonly string _local;
    private readonly string _defaultData;

    public AppStorage(string localFolder, string defaultDataFolder)
    {
        _local = localFolder;
        _defaultData = defaultDataFolder;
        Settings = LoadSettings();
        MigrateOldFiles();
    }

    /// <summary>The storage the app uses: settings in %APPDATA%\CNCStudio, data in Dokument\CNC Studio or the sync folder.</summary>
    public static AppStorage Current { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CNCStudio"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CNC Studio"));

    public AppSettings Settings { get; private set; }
    public string LocalFolder => _local;
    public string SettingsFile => Path.Combine(_local, "settings.json");

    /// <summary>The sync folder if one is chosen AND reachable now (Drive may not be running).</summary>
    public string? ActiveSyncFolder => Settings.SyncFolder is string f && Directory.Exists(f) ? f : null;
    public bool SyncFolderMissing => Settings.SyncFolder is not null && ActiveSyncFolder is null;

    /// <summary>The "CNC Studio" folder that holds Projekt, Verktyg, Material and Inställningar.</summary>
    public string DataFolder => ActiveSyncFolder ?? _defaultData;
    public string ProjectsFolder => Path.Combine(DataFolder, ProjectsFolderName);
    public string ToolsFolder => Path.Combine(DataFolder, ToolsFolderName);
    public string MaterialsFolder => Path.Combine(DataFolder, MaterialsFolderName);
    public string ToolLibraryPath => Path.Combine(ToolsFolder, "verktyg.json");
    public string MaterialLibraryPath => Path.Combine(MaterialsFolder, "material.json");
    public string MachineProfilePath => Path.Combine(DataFolder, SettingsFolderName, "maskinprofil.json");

    /// <summary>This computer's name; each computer writes its logs in its own folder, so no two write the same file.</summary>
    public static string ComputerName => Environment.MachineName;
    /// <summary>Loggar – one subfolder per computer, readable from every computer when synced.</summary>
    public string LogsRoot => Path.Combine(DataFolder, LogsFolderName);
    public string LogsFolder => Path.Combine(LogsRoot, ComputerName);

    /// <summary>
    /// Uses <paramref name="folder"/> as the sync folder (null = stop syncing). Creates its subfolders and
    /// copies this computer's tool library, material library and machine profile there if the folder has
    /// none yet – files already in the sync folder (from another computer) are never overwritten.
    /// </summary>
    public IReadOnlyList<string> SetSyncFolder(string? folder)
    {
        var copied = new List<string>();
        if (folder is not null)
        {
            folder = Path.GetFullPath(folder);
            foreach (var sub in new[] { ProjectsFolderName, ToolsFolderName, MaterialsFolderName, SettingsFolderName })
                Directory.CreateDirectory(Path.Combine(folder, sub));
            foreach (var rel in SharedFiles)
            {
                var from = Path.Combine(DataFolder, rel);
                var to = Path.Combine(folder, rel);
                if (File.Exists(from) && !File.Exists(to)) { File.Copy(from, to); copied.Add(rel); }
            }
            // This computer's logs follow along (files already there are kept).
            if (CopyFolderContents(LogsFolder, Path.Combine(folder, LogsFolderName, ComputerName)) > 0) copied.Add(Path.Combine(LogsFolderName, ComputerName));
        }
        Settings = new AppSettings { SyncFolder = folder };
        Directory.CreateDirectory(_local);
        WriteAtomic(SettingsFile, JsonSerializer.Serialize(Settings, Json));
        MigrateOldFiles();
        return copied;
    }

    /// <summary>Creates Projekt, Verktyg, Material and Inställningar in the data folder.</summary>
    public void EnsureFolders()
    {
        foreach (var sub in new[] { ProjectsFolderName, ToolsFolderName, MaterialsFolderName, SettingsFolderName })
            Directory.CreateDirectory(Path.Combine(DataFolder, sub));
        Directory.CreateDirectory(LogsFolder);
    }

    public MachineProfile? LoadMachineProfile()
    {
        try { return File.Exists(MachineProfilePath) ? JsonSerializer.Deserialize<MachineProfile>(File.ReadAllText(MachineProfilePath), Json) : null; }
        catch (Exception) { return null; }
    }

    public void SaveMachineProfile(MachineProfile profile) => WriteAtomic(MachineProfilePath, JsonSerializer.Serialize(profile, Json));

    /// <summary>
    /// Earlier versions kept tools.json and maskinprofil.json directly in %APPDATA%\CNCStudio or in the
    /// sync folder's Inställningar. They are copied to the new places once (the originals are left alone).
    /// </summary>
    private void MigrateOldFiles()
    {
        try
        {
            CopyIfMissing(new[] { Path.Combine(DataFolder, SettingsFolderName, "tools.json"), Path.Combine(_local, "tools.json") }, ToolLibraryPath);
            CopyIfMissing(new[] { Path.Combine(_local, "maskinprofil.json") }, MachineProfilePath);
            // Logs used to live in %APPDATA%\CNCStudio\logs on each computer.
            CopyFolderContents(Path.Combine(_local, "logs"), LogsFolder);
        }
        catch (Exception) { /* a missing or read-only folder must never stop the program */ }
    }

    /// <summary>Copies files that the target does not have yet; returns how many were copied.</summary>
    private static int CopyFolderContents(string from, string to)
    {
        if (!Directory.Exists(from) || Path.GetFullPath(from).TrimEnd('\\') == Path.GetFullPath(to).TrimEnd('\\')) return 0;
        int n = 0;
        foreach (var file in Directory.GetFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(to);
            File.Copy(file, target);
            n++;
        }
        return n;
    }

    private static void CopyIfMissing(IEnumerable<string> oldPaths, string target)
    {
        if (File.Exists(target)) return;
        var source = oldPaths.FirstOrDefault(File.Exists);
        if (source is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
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
