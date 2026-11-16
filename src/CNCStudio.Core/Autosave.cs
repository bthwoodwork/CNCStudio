using System;
using System.IO;
using System.Text.Json;

namespace CNCStudio.Core;

/// <summary>What an autosaved copy is: which project it was and when it was written.</summary>
public sealed class AutosaveInfo
{
    /// <summary>The project file the work belongs to, or null for a project never saved.</summary>
    public string? OriginalPath { get; set; }
    public string Name { get; set; } = "";
    public DateTime SavedAt { get; set; }
}

/// <summary>
/// Autospara: a copy of the open project with unsaved changes, written every minute to its own folder on this
/// computer (never the sync folder, never over the project file). It is removed when the project is saved or the
/// changes are deliberately thrown away; if it is still there at the next start, the program crashed, the power
/// went or Windows was shut down with unsaved work – and the copy is offered back.
/// </summary>
public sealed class Autosave
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _folder;

    public Autosave(string folder) => _folder = folder;

    public string ProjectPath => Path.Combine(_folder, "projekt.cncstudio.json");
    public string InfoPath => Path.Combine(_folder, "info.json");

    public void Write(CncDocument document, string? originalPath, DateTime? now = null)
    {
        Directory.CreateDirectory(_folder);
        DocumentStorage.Save(ProjectPath, document);
        var info = new AutosaveInfo { OriginalPath = originalPath, Name = document.Name, SavedAt = now ?? DateTime.Now };
        AppStorage.WriteAtomic(InfoPath, JsonSerializer.Serialize(info, Json));
    }

    /// <summary>The copy and what it was, or null when there is none (or it cannot be read).</summary>
    public (CncDocument Document, AutosaveInfo Info)? TryLoad()
    {
        if (!File.Exists(ProjectPath) || !File.Exists(InfoPath)) return null;
        try
        {
            var info = JsonSerializer.Deserialize<AutosaveInfo>(File.ReadAllText(InfoPath), Json);
            if (info is null) return null;
            return (DocumentStorage.Load(ProjectPath), info);
        }
        catch (Exception) { return null; }
    }

    public void Clear()
    {
        try { if (File.Exists(ProjectPath)) File.Delete(ProjectPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { if (File.Exists(InfoPath)) File.Delete(InfoPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
