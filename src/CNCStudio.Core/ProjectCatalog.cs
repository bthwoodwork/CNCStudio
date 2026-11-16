using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>A project file in the Projekt folder.</summary>
public sealed record ProjectFileInfo(string Path, string Name, string? Subfolder, DateTime LastWrite);

/// <summary>The projects in the Projekt folder (also in subfolders), most recently saved first.</summary>
public static class ProjectCatalog
{
    public const string Extension = ".cncstudio.json";

    /// <summary>
    /// The path with the project extension, whatever was typed in the save dialog: "Bricka", "Bricka.json" and
    /// "Bricka.cncstudio.json" all become "Bricka.cncstudio.json" – so the project shows on the start screen and in Öppna.
    /// A name with dots of its own ("Hylla v1.2") keeps them.
    /// </summary>
    public static string WithExtension(string path)
    {
        if (path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return path;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path = path[..^".json".Length];
        return path + Extension;
    }

    public static IReadOnlyList<ProjectFileInfo> Recent(string projectsFolder, int max)
    {
        if (!Directory.Exists(projectsFolder) || max <= 0) return Array.Empty<ProjectFileInfo>();
        var root = System.IO.Path.GetFullPath(projectsFolder);
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*" + Extension, SearchOption.AllDirectories).ToList(); }
        catch (Exception) { return Array.Empty<ProjectFileInfo>(); }        // a folder that cannot be read
        return files
            .Select(f =>
            {
                var name = System.IO.Path.GetFileName(f);
                name = name[..^Extension.Length];
                var dir = System.IO.Path.GetDirectoryName(f)!;
                string? sub = string.Equals(dir.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    ? null : System.IO.Path.GetRelativePath(root, dir);
                return new ProjectFileInfo(f, name, sub, File.GetLastWriteTime(f));
            })
            .OrderByDescending(p => p.LastWrite).Take(max).ToList();
    }
}
