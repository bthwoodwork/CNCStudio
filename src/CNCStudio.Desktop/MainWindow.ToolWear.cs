using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>Körtid per fräs: each run adds its time to the cutter of the program, and reminds when it is time to change it.</summary>
public partial class MainWindow
{
    /// <summary>Adds the run's time to the tools of the loaded program (G-code from elsewhere names no tool, so it counts nowhere);
    /// returns a reminder text for tools that are now due, or "".</summary>
    private string RecordToolWear(DateTime started)
    {
        if (_loadedJob is not { } job) return "";
        double minutes = (DateTime.Now - started).TotalMinutes;
        if (minutes < 0.05) return "";
        try
        {
            // Read the file fresh so a library window's latest save is not overwritten by an older copy.
            var library = LoadTools();
            var due = ToolWear.Record(library, job.Operations.Select(o => o.Operation.ToolName), minutes);
            library.Save(AppStorage.Current.ToolLibraryPath);
            var current = SelectedTool?.Name;
            _tools = library;
            RefreshToolPicker();
            SelectTool(current);
            return due.Count == 0 ? "" : "\n\nDAGS ATT BYTA ELLER SLIPA:\n" + string.Join("\n", due.Select(t =>
                $"• {t.Name} har kört {ToolWear.Hours(t.RunMinutesSinceChange)} sedan bytet (påminnelse efter {t.ChangeIntervalHours:0.#} h)")) +
                "\nNollställ under Verktygsbibliotek → Ny/slipad när den är bytt.";
        }
        catch (Exception ex) { App.LogError(ex); return ""; }
    }
}
