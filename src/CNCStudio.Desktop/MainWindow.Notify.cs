using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Avisering: when the program is done, waits at an M0 pause, is stopped by an error or the machine raises an alarm,
/// the computer plays a sound (one per kind, can be turned off), the taskbar button flashes if CNC Studio is not in
/// front, and the phone page (Styr från mobilen) shows a coloured banner, vibrates and beeps.
/// </summary>
public partial class MainWindow
{
    private enum NoticeKind { Done, Pause, Stopped, Alarm }

    private long _noticeId;
    private string? _noticeText;
    private NoticeKind _noticeKind;
    /// <summary>CNC Studio sent a feed hold (Paus, Stopp, phone, gamepad): the hold that follows is not the program's M0.</summary>
    private volatile bool _userPaused;

    private bool NotifySoundOn => AppStorage.Current.Settings.NotifySound != false;

    private void Notify(NoticeKind kind, string text)
    {
        _noticeId++; _noticeText = text; _noticeKind = kind;
        if (NotifySoundOn)
        {
            try
            {
                (kind switch { NoticeKind.Done => SystemSounds.Asterisk, NoticeKind.Pause => SystemSounds.Exclamation, _ => SystemSounds.Hand }).Play();
            }
            catch (Exception) { /* no sound device */ }
        }
        if (!IsActive) FlashTaskbar();
    }

    private string? NoticeKindName => _noticeText is null ? null : _noticeKind switch
    {
        NoticeKind.Done => "done", NoticeKind.Pause => "pause", NoticeKind.Stopped => "stopped", _ => "alarm"
    };

    /// <summary>From the status reports: the running program went into Hold without Paus being pressed – an M0 in the program.</summary>
    private void NoticeHold(string? before, string now)
    {
        if (now != "Hold") { if (now == "Run") _userPaused = false; return; }
        if (before == "Hold" || _runner is null || _userPaused) return;
        Notify(NoticeKind.Pause, _document.Machine.Spindle == SpindleControl.Manual
            ? "Programmet väntar (M0) – starta fräsen och tryck Fortsätt."
            : "Programmet väntar (M0) – tryck Fortsätt när du är klar.");
    }

    private void NotifySound_Click(object sender, RoutedEventArgs e)
    {
        AppStorage.Current.Settings.NotifySound = NotifySoundMenuItem.IsChecked;
        try { AppStorage.Current.SaveSettings(); } catch (Exception) { }
        StatusText.Text = NotifySoundMenuItem.IsChecked ? "Ljud vid klart, paus och larm är på." : "Ljud vid klart, paus och larm är av (mobilen aviseras ändå).";
    }

    // ---- Taskbar flash (FlashWindowEx) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

    private void FlashTaskbar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            // FLASHW_ALL | FLASHW_TIMERNOFG: flash until the window comes to the front.
            var info = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = hwnd, dwFlags = 3 | 12, uCount = uint.MaxValue, dwTimeout = 0 };
            FlashWindowEx(ref info);
        }
        catch (Exception) { }
    }
}
