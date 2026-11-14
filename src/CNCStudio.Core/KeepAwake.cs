namespace CNCStudio.Core;

/// <summary>How much of the computer must stay awake (Windows' SetThreadExecutionState).</summary>
public enum AwakeLevel
{
    /// <summary>Windows may sleep as usual.</summary>
    None,
    /// <summary>The computer must not sleep (the USB connection to the machine would drop); the screen may turn off.</summary>
    System,
    /// <summary>A program or macro is running: the computer and the screen stay on, so the run can be watched.</summary>
    SystemAndDisplay
}

/// <summary>
/// Håll datorn vaken (like gSender's sleep management): a computer that goes to sleep while connected drops
/// the USB connection, and one that sleeps mid-run stops the program. While connected the computer is kept
/// awake; while a program or macro runs the screen is kept on too. Released again when idle or disconnected.
/// </summary>
public static class KeepAwake
{
    public static AwakeLevel For(bool connected, bool running) =>
        running ? AwakeLevel.SystemAndDisplay : connected ? AwakeLevel.System : AwakeLevel.None;

    // SetThreadExecutionState flags (winbase.h).
    public const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001, EsDisplayRequired = 0x00000002;

    /// <summary>The flags for a level: ES_CONTINUOUS keeps them until the next call; ES_CONTINUOUS alone releases.</summary>
    public static uint Flags(AwakeLevel level) => level switch
    {
        AwakeLevel.SystemAndDisplay => EsContinuous | EsSystemRequired | EsDisplayRequired,
        AwakeLevel.System => EsContinuous | EsSystemRequired,
        _ => EsContinuous
    };
}
