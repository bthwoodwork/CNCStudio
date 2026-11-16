using System.IO;
using System.Windows;
using System.Windows.Threading;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Autospara och återställning: every change counts (RememberChange, undo, redo); with unsaved changes a copy is
/// written every minute to the Autospar folder (<see cref="Autosave"/>). Saving, or choosing not to save, removes it.
/// At start a copy left behind is offered back. Closing, Nytt and Öppna ask before unsaved changes are lost.
/// </summary>
public partial class MainWindow
{
    private readonly Autosave _autosave = new(AppStorage.Current.AutosaveFolder);
    private DispatcherTimer? _autosaveTimer;
    private long _changeCount, _savedChangeCount, _autosavedChangeCount;

    private bool HasUnsavedChanges => _changeCount != _savedChangeCount;

    /// <summary>Called for every change of the document (RememberChange, undo, redo).</summary>
    private void NoteChange() => _changeCount++;

    /// <summary>The document now matches its file (saved, opened or new): nothing to autosave or ask about.</summary>
    private void MarkSaved()
    {
        _savedChangeCount = _autosavedChangeCount = _changeCount;
        _autosave.Clear();
    }

    private void StartAutosave()
    {
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _autosaveTimer.Tick += (_, _) => AutosaveNow();
        _autosaveTimer.Start();
    }

    private void AutosaveNow()
    {
        if (!HasUnsavedChanges || _changeCount == _autosavedChangeCount) return;
        try
        {
            _autosave.Write(_document, _path);
            _autosavedChangeCount = _changeCount;
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    /// <summary>
    /// Before the open project is replaced or the program closes: Spara / Spara inte / Avbryt. True when it is fine
    /// to go on (saved, or the user chose not to).
    /// </summary>
    private bool ConfirmDiscardChanges(string action)
    {
        if (!HasUnsavedChanges) return true;
        string name = string.IsNullOrWhiteSpace(_document.Name) ? "projektet" : $"\"{_document.Name}\"";
        var answer = MessageBox.Show(this, $"Ändringarna i {name} är inte sparade.\n\nSpara dem innan du {action}?",
            "Osparade ändringar", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) { MarkSaved(); return true; }     // thrown away on purpose: no copy to offer later
        Save_Click(this, new RoutedEventArgs());
        return !HasUnsavedChanges;                                          // the save was cancelled or failed
    }

    /// <summary>At start: a copy left behind means the last session ended with unsaved work – offer it back.</summary>
    private bool OfferRestore()
    {
        if (_autosave.TryLoad() is not { } copy) return false;
        var info = copy.Info;
        string where = info.OriginalPath is string p ? $"\n\nProjektfil: {p}" : "\n\nProjektet hade inte sparats som fil.";
        var answer = MessageBox.Show(this,
            $"CNC Studio stängdes utan att ändringarna i \"{info.Name}\" sparades (senast autosparat {info.SavedAt:yyyy-MM-dd HH:mm})." + where +
            "\n\nVill du återställa projektet? Det öppnas med ändringarna, och du sparar det själv när du vill behålla det.",
            "Återställ projekt", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) { _autosave.Clear(); return false; }
        _document = copy.Document; _camPlanner.Clear(); View3DPanel?.ResetView();
        // Saving goes back to the project file if its folder is still there; otherwise Spara asks for a name.
        _path = info.OriginalPath is string file && Directory.Exists(Path.GetDirectoryName(file) ?? "") ? file : null;
        _selected = null; _selection.Clear(); _selectionOrder.Clear(); _undo.Clear(); _redo.Clear(); _polyPoints.Clear();
        SyncMetadata(); Render(); FitToMaterial(); HideStartScreen();
        // Restored but not saved: still counts as unsaved, and the copy stays until it is saved or thrown away.
        _savedChangeCount = 0; _changeCount = 1; _autosavedChangeCount = 1;
        StatusText.Text = $"Återställt från autosparad kopia ({info.SavedAt:HH:mm}). Spara (Ctrl+S) för att behålla ändringarna.";
        return true;
    }
}
