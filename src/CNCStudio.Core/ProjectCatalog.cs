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
