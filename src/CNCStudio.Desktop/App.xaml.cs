using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CNCStudio.Desktop;

/// <summary>
/// Safety net: an unexpected error in a button handler (for example a machine that does not answer)
/// is shown and logged instead of closing or freezing the program. Errors are still fixed where they
/// happen; this only makes sure one missed case never takes the program down mid-test.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // The look for every window, before the first one opens (Visa → Nytt utseende turns it off).
        if (CNCStudio.Core.AppStorage.Current.Settings.ModernLook != false)
        {
            try { Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Theme.xaml") }); }
            catch (Exception ex) { LogError(ex); }   // never stop the program over its looks
        }
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { LogError(args.Exception); args.SetObserved(); };
        // Every window (main, dialogs, tests, logs) gets the CNC Studio icon in its title bar and the taskbar.
        var icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/cncstudio.ico"));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window w && w.Icon is null) w.Icon = icon; }));
        // Dark title bar for every window, and the code-built windows' button row as a footer like the dialogs'.
        if (CNCStudio.Core.AppStorage.Current.Settings.ModernLook != false)
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) => { if (sender is Window w) WindowLook.Apply(w); }));
        SelectAllOnEntry();
    }

    /// <summary>
    /// Every one-line field selects its text when it is entered – by Tab or by a click – so a new value can be
    /// typed straight away. A second click in a field that already has focus places the cursor as usual.
    /// Multi-line fields (code, notes, the G-code editor) and read-only ones keep the normal behaviour.
    /// </summary>
    private static void SelectAllOnEntry()
    {
        static bool Applies(TextBox b) => !b.AcceptsReturn && !b.IsReadOnly;
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((sender, _) => { if (sender is TextBox b && Applies(b)) b.SelectAll(); }));
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewMouseLeftButtonDownEvent,
            new System.Windows.Input.MouseButtonEventHandler((sender, e) =>
            {
                // The first click only gives focus (which selects all); otherwise the click would put the cursor there.
                if (sender is TextBox b && Applies(b) && !b.IsKeyboardFocusWithin) { b.Focus(); e.Handled = true; }
            }));
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var path = LogError(e.Exception);
        try
        {
            AppDialog.Show(Current?.MainWindow, "Ett oväntat fel inträffade, men programmet fortsätter:\n\n" + e.Exception.Message +
                "\n\nOm maskinen var igång: kontrollera läget och använd STOPP eller Återställ vid behov." +
                (path is null ? "" : "\n\nDetaljer finns under Hjälp → Loggar och fellogg."), "Oväntat fel", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception) { /* never let the safety net itself crash */ }
    }

    internal static string? LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(CNCStudio.Core.ErrorLog.Folder);
            File.AppendAllText(CNCStudio.Core.ErrorLog.FilePath, CNCStudio.Core.ErrorLog.Format(DateTime.Now, ex));
            return CNCStudio.Core.ErrorLog.FilePath;
        }
        catch (Exception) { return null; }
    }
}
