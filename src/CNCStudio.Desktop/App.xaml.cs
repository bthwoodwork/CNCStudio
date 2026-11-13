using System.IO;
using System.Threading.Tasks;
using System.Windows;
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
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { LogError(args.Exception); args.SetObserved(); };
        // Every window (main, dialogs, tests, logs) gets the CNC Studio icon in its title bar and the taskbar.
        var icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/cncstudio.ico"));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window w && w.Icon is null) w.Icon = icon; }));
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
