using System.Runtime.InteropServices;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Håll datorn vaken: while connected Windows may not sleep (the USB connection would drop), and while a program
/// or macro runs the screen stays on too. Always called on the UI thread – the request belongs to the thread.
/// </summary>
public partial class MainWindow
{
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private AwakeLevel _awake = AwakeLevel.None;

    /// <summary>Sets the level from the current state; call after connecting, disconnecting and starting or ending a run.</summary>
    private void UpdateKeepAwake()
    {
        var level = KeepAwake.For(connected: _grbl is not null, running: _runner is not null || _macroRunner is not null);
        if (level == _awake) return;
        try
        {
            if (SetThreadExecutionState(KeepAwake.Flags(level)) == 0) return;   // 0 = refused: keep the old level
            _awake = level;
            Log(level switch
            {
                AwakeLevel.SystemAndDisplay => "Datorn och skärmen hålls vakna medan programmet körs.",
                AwakeLevel.System => "Datorn hålls vaken medan maskinen är ansluten (skärmen får släckas).",
                _ => "Datorn får gå i viloläge igen."
            });
        }
        catch (Exception) { /* not Windows, or the call is not there: nothing to keep awake */ }
    }
}
