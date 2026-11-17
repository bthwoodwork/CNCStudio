using System.Windows;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>Egen G-code vid start och stopp: the macros set to run by themselves before a program, after it, or after Stoppa.</summary>
public partial class MainWindow
{
    /// <summary>
    /// Runs the macro set for <paramref name="trigger"/>, if any. True when there is none or it ran to the end;
    /// false (with the reason shown) when it could not be run or was stopped – before a program, the program then does not start.
    /// </summary>
    private async Task<bool> RunTriggeredMacro(GrblClient g, MacroTrigger trigger)
    {
        Macro? macro;
        try { macro = MacroLibrary.Load(MacroPath).For(trigger); }
        catch (Exception ex) { App.LogError(ex); return true; }
        if (macro is null) return true;
        var prepared = MacroCode.Prepare(macro.Code);
        string when = Macro.TriggerText(trigger);
        if (!prepared.Ok)
        {
            Error($"Makrot \"{macro.Name}\", som körs {when}, kan inte köras:\n\n" + string.Join("\n", prepared.Problems.Take(8).Select(p => "• " + p)) + "\n\nÄndra makrot under Makron.");
            return false;
        }
        if (g.LastStatus?.State is "Alarm" or "Door")
        {
            StatusText.Text = $"Makrot \"{macro.Name}\" kördes inte: maskinen är i läge {g.LastStatus.State}.";
            return false;
        }
        var runner = new JobRunner(g, prepared.Lines, store: null);
        _macroRunner = runner;
        UpdateKeepAwake();
        StopJobButton.IsEnabled = true;
        LoadJobButton.IsEnabled = StartJobButton.IsEnabled = false;
        if (RunMacroButton is not null) RunMacroButton.IsEnabled = false;
        StatusText.Text = $"Makrot \"{macro.Name}\" körs ({when}).";
        try
        {
            var result = await runner.RunAsync();
            if (result.Outcome == JobOutcome.Finished) { StatusText.Text = $"Makrot \"{macro.Name}\" är klart."; return true; }
            StatusText.Text = $"Makrot \"{macro.Name}\": {result.Message}";
            if (result.Outcome == JobOutcome.Failed)
                AppDialog.Show(this, $"Makrot \"{macro.Name}\", som körs {when}, stoppades: {result.Message}", "Makro", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex) { App.LogError(ex); StatusText.Text = $"Makrot \"{macro.Name}\" avbröts: {ex.Message}"; return false; }
        finally
        {
            _macroRunner = null;
            UpdateKeepAwake();
            if (RunMacroButton is not null) RunMacroButton.IsEnabled = true;
            UpdateJobButtons();
        }
    }
}
