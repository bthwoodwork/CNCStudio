using System.IO;
using System.Windows;
using System.Windows.Controls;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;
using Microsoft.Win32;

namespace CNCStudio.Desktop;

/// <summary>
/// The Makron panel: the user's own pieces of G-code (Inställningar\makron.json, synced), run on the connected
/// machine one line at a time with the same runner as programs (send-response; Stop = feed hold, wait, reset).
/// A macro is checked before it runs (MacroCode.Prepare) and one that can move the machine asks first.
/// </summary>
public partial class MainWindow
{
    private JobRunner? _macroRunner;

    private static string MacroPath => AppStorage.Current.MacroLibraryPath;

    private void RefreshMacroList(Guid? select = null)
    {
        if (MacroList is null) return;
        var keep = select ?? SelectedMacro?.Id;
        MacroList.Items.Clear();
        foreach (var m in MacroLibrary.Load(MacroPath).Macros)
        {
            var item = new ListBoxItem
            {
                Tag = m, Padding = new Thickness(4, 3, 4, 3), ToolTip = m.Code,
                Content = new StackPanel { Children =
                {
                    new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = m.Description, FontSize = 10.5, Foreground = System.Windows.Media.Brushes.LightSteelBlue, TextTrimming = TextTrimming.CharacterEllipsis }
                } }
            };
            MacroList.Items.Add(item);
            if (m.Id == keep) MacroList.SelectedItem = item;
        }
        if (MacroList.SelectedItem is null && MacroList.Items.Count > 0) MacroList.SelectedIndex = 0;
    }

    private Macro? SelectedMacro => (MacroList?.SelectedItem as ListBoxItem)?.Tag as Macro;

    /// <summary>Changes the library (read from the file first, so another computer's changes are kept) and saves it.</summary>
    private void ChangeMacros(Action<MacroLibrary> change, Guid? select = null)
    {
        try
        {
            var lib = MacroLibrary.Load(MacroPath);
            change(lib);
            lib.Save(MacroPath);
        }
        catch (Exception ex) { Error("Makrona kunde inte sparas: " + ex.Message); }
        RefreshMacroList(select);
    }

    private void NewMacro_Click(object sender, RoutedEventArgs e) => EditMacro(new Macro(), isNew: true);
    private void EditMacro_Click(object sender, RoutedEventArgs e) { if (SelectedMacro is { } m) EditMacro(m.Clone(), isNew: false); }

    private void EditMacro(Macro macro, bool isNew)
    {
        var w = new MacroEditorWindow(macro, isNew) { Owner = this };
        if (w.ShowDialog() != true || w.Result is not { } result) return;
        ChangeMacros(lib =>
        {
            int i = lib.Macros.FindIndex(x => x.Id == result.Id);
            if (i >= 0) lib.Macros[i] = result; else lib.Macros.Add(result);
        }, result.Id);
    }

    private void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMacro is not { } m) return;
        if (AppDialog.Show(this, $"Ta bort makrot \"{m.Name}\"?", "Ta bort makro", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        ChangeMacros(lib => lib.Macros.RemoveAll(x => x.Id == m.Id));
    }

    /// <summary>A macro from a G-code file: opened in the editor, so it is checked and named before it is saved.</summary>
    private void ImportMacro_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "G-code (*.nc;*.gcode;*.ngc;*.tap;*.txt)|*.nc;*.gcode;*.ngc;*.tap;*.txt|Alla filer|*.*", Title = "Gör ett makro av en G-code-fil" };
        if (dialog.ShowDialog(this) != true) return;
        string text;
        try { text = File.ReadAllText(dialog.FileName); }
        catch (Exception ex) { Error("Filen kunde inte läsas: " + ex.Message); return; }
        EditMacro(new Macro { Name = Path.GetFileNameWithoutExtension(dialog.FileName), Description = "Från " + Path.GetFileName(dialog.FileName), Code = MacroCode.FromFile(text) }, isNew: true);
    }

    private async void RunMacro_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMacro is not { } macro) return;
        if (_macroRunner is not null) return;
        if (_runner is not null) { Error("Ett program körs – makron kan köras när det är klart."); return; }
        var prepared = MacroCode.Prepare(macro.Code);
        if (!prepared.Ok)
        {
            Error($"Makrot \"{macro.Name}\" kan inte köras:\n\n" + string.Join("\n", prepared.Problems.Take(8).Select(p => "• " + p)) + "\n\nÄndra makrot och försök igen.");
            return;
        }
        if (RequireIdle("Makro", dialog: true) is not GrblClient g) return;
        if (MacroCode.MovesMachine(prepared.Lines))
        {
            var shown = string.Join("\n", prepared.Lines.Take(12).Select(l => "  " + l)) + (prepared.Lines.Count > 12 ? $"\n  … och {prepared.Lines.Count - 12} rader till" : "");
            if (AppDialog.Show(this, $"Kör \"{macro.Name}\"? Maskinen rör sig.\n\n{shown}\n\n" +
                    (_homedThisSession ? "" : "Obs: maskinen är inte homad i den här anslutningen – maskinkoordinater (G53) och mjuka gränser stämmer inte förrän efter homing.\n\n") +
                    "Kontrollera att inget är i vägen. ■ Stoppa makrot bromsar in och stoppar.",
                    "Kör makro", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) != MessageBoxResult.OK) return;
        }
        MacroOutput.Clear();
        void Show(string line) { MacroOutput.AppendText(line + "\n"); MacroOutput.ScrollToEnd(); }
        Action<string> onLog = Show;
        var runner = new JobRunner(g, prepared.Lines, store: null);
        _macroRunner = runner;
        RunMacroButton.IsEnabled = false; StopMacroButton.IsEnabled = true;
        LoadJobButton.IsEnabled = StartJobButton.IsEnabled = false;
        GrblLogAdded += onLog;
        StatusText.Text = $"Makrot \"{macro.Name}\" körs.";
        try
        {
            var result = await runner.RunAsync();
            Show(result.Outcome == JobOutcome.Finished ? "— klart —" : "— " + result.Message + " —");
            StatusText.Text = result.Outcome == JobOutcome.Finished ? $"Makrot \"{macro.Name}\" är klart." : result.Message;
            if (result.Outcome == JobOutcome.Failed)
                AppDialog.Show(this, $"Makrot \"{macro.Name}\" stoppades: {result.Message}", "Makro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { Show("— avbrutet: " + ex.Message + " —"); StatusText.Text = "Makrot avbröts: " + ex.Message; }
        finally
        {
            GrblLogAdded -= onLog;
            _macroRunner = null;
            RunMacroButton.IsEnabled = true; StopMacroButton.IsEnabled = false;
            UpdateJobButtons();
        }
    }

    private async void StopMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_macroRunner is not { } r) return;
        StopMacroButton.IsEnabled = false;
        await r.StopAsync();
    }
}
