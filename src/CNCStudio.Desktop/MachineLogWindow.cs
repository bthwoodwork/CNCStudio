using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Maskinlogg och underhåll: running time and job statistics, maintenance
/// that comes due with running time, the latest jobs and alarms, and the connection details – for one machine.
/// Maintenance changes are saved at once.
/// </summary>
public sealed class MachineLogWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        Card = B(0x1C, 0x2A, 0x39), Good = B(0x7C, 0xD9, 0x9C), Warn = B(0xFF, 0xD3, 0x9F), Bad = B(0xFF, 0x7A, 0x86);

    private readonly string _path;
    private MachineLog _log;
    private readonly StackPanel _body = new();

    public MachineLogWindow(MachineProfile machine, string path)
    {
        _path = path;
        _log = MachineLog.Load(path);
        Title = $"Maskinlogg – {machine.Name}";
        Width = 820; Height = 760; MinWidth = 640; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");
        var close = Btn("Stäng", B(0x34, 0x47, 0x5A)); close.IsCancel = true; close.Click += (_, _) => Close();
        var root = new DockPanel { Margin = new Thickness(18) };
        var header = new StackPanel { Children =
        {
            new TextBlock { Text = "MASKINLOGG – " + machine.Name.ToUpperInvariant(), FontSize = 18, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "Körtid, jobb, larm och underhåll för den här maskinen. Allt loggas automatiskt när CNC Studio kör den; underhållet räknas i körtimmar.",
                Foreground = Soft, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) },
        } };
        var footer = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { close } };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body });
        Content = root;
        Build();
    }

    private void Build()
    {
        _body.Children.Clear();
        var log = _log;

        // ---- Statistics ----
        var stats = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
        stats.Children.Add(Stat("Körtid totalt", MachineLog.Format(TimeSpan.FromHours(log.RunningHours))));
        stats.Children.Add(Stat("Jobb", $"{log.FinishedJobs + log.NotFinishedJobs}  ({log.FinishedJobs} klara)"));
        stats.Children.Add(Stat("Genomsnitt", log.AverageJob is { } avg ? MachineLog.Format(avg) : "–"));
        stats.Children.Add(Stat("Längsta", log.LongestJob is { } l ? MachineLog.Format(l.Duration) : "–"));
        var conn = $"Senast ansluten: {(log.LastConnected is { } t ? t.ToString("yyyy-MM-dd HH:mm") : "aldrig")}" +
                   (log.LastComputer is { } c ? $" från {c}" : "") + (log.Firmware is { } f ? $" · {f}" : "");
        _body.Children.Add(Section("STATISTIK", new StackPanel { Children = { stats, new TextBlock { Text = conn, Foreground = Muted, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap } } }));

        // ---- Maintenance ----
        var maint = new StackPanel();
        if (log.Maintenance.Count == 0) maint.Children.Add(new TextBlock { Text = "Inga underhållsuppgifter.", Foreground = Muted });
        foreach (var task in log.Maintenance.OrderByDescending(x => log.HoursSince(x) / x.IntervalHours))
        {
            var state = log.StateOf(task);
            var color = state switch { MaintenanceState.Due => Bad, MaintenanceState.Soon => Warn, _ => Good };
            string since = $"{F(log.HoursSince(task))} av {F(task.IntervalHours)} h" + (task.DoneDate is { } d ? $" · senast {d:yyyy-MM-dd}" : "");
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = Math.Min(1, log.HoursSince(task) / task.IntervalHours), Height = 6, Foreground = color, Background = B(0x2C, 0x3E, 0x52), BorderThickness = new Thickness(0), Margin = new Thickness(0, 3, 0, 3) };
            var done = Btn("Gjort", B(0x2E, 0x7A, 0x4A)); done.ToolTip = "Uppgiften är gjord nu – timmarna räknas från noll igen.";
            done.Click += (_, _) => Change(x => x.MarkDone(task.Id, DateTime.Now));
            var edit = Btn("Ändra…", B(0x34, 0x47, 0x5A)); edit.Click += (_, _) => EditTask(task.Id);
            var remove = Btn("Ta bort", B(0x5A, 0x34, 0x3A));
            remove.Click += (_, _) =>
            {
                if (AppDialog.Show(this, $"Ta bort underhållsuppgiften \"{task.Name}\"?", "Ta bort", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    Change(x => x.Maintenance.RemoveAll(y => y.Id == task.Id));
            };
            var info = new StackPanel { Children =
            {
                new TextBlock { Text = task.Name + (state == MaintenanceState.Due ? "  – DAGS" : state == MaintenanceState.Soon ? "  – snart" : ""), FontWeight = FontWeights.SemiBold, Foreground = state == MaintenanceState.Ok ? Ink : color },
                new TextBlock { Text = since + (task.Description.Length > 0 ? " · " + task.Description : ""), Foreground = Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11.5 },
                bar
            } };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { done, edit, remove } };
            DockPanel.SetDock(buttons, Dock.Right); row.Children.Add(buttons); row.Children.Add(info);
            maint.Children.Add(row);
        }
        var add = Btn("＋ Ny uppgift…", B(0x24, 0x61, 0x8C)); add.Click += (_, _) => EditTask(null);
        maint.Children.Add(new WrapPanel { Children = { add } });
        _body.Children.Add(Section("UNDERHÅLL (räknas i körtimmar)", maint));

        // ---- Jobs ----
        var jobs = new StackPanel();
        if (log.Jobs.Count == 0) jobs.Children.Add(new TextBlock { Text = "Inga jobb körda ännu.", Foreground = Muted });
        foreach (var j in log.Jobs.AsEnumerable().Reverse().Take(20))
        {
            var (text, color) = j.Outcome switch
            {
                LoggedJobOutcome.Finished => ("klart", Good),
                LoggedJobOutcome.Stopped => ("stoppat", Warn),
                _ => ("avbrutet", Bad)
            };
            jobs.Children.Add(Line($"{j.Start:yyyy-MM-dd HH:mm}", j.Name + (j.Resumed ? " (återupptaget)" : ""),
                $"{MachineLog.Format(j.Duration)} · {text}" + (j.Outcome == LoggedJobOutcome.Finished ? "" : $" på rad {j.LinesRun} av {j.Lines}"),
                color, j.Outcome == LoggedJobOutcome.Finished ? null : j.Message));
        }
        _body.Children.Add(Section("SENASTE JOBB", jobs));

        // ---- Alarms ----
        var alarms = new StackPanel();
        if (log.Alarms.Count == 0) alarms.Children.Add(new TextBlock { Text = "Inga larm.", Foreground = Muted });
        foreach (var a in log.Alarms.AsEnumerable().Reverse().Take(20))
            alarms.Children.Add(Line($"{a.Time:yyyy-MM-dd HH:mm}", $"ALARM:{a.Code} – {a.Message}", a.Job is { } job ? "under " + job : "", Bad, null));
        _body.Children.Add(Section("LARM", alarms));
    }

    /// <summary>Changes the log, saves it and shows it again (read from the file first, so nothing recorded meanwhile is lost).</summary>
    private void Change(Action<MachineLog> change)
    {
        try
        {
            _log = MachineLog.Load(_path);
            change(_log);
            _log.Save(_path);
        }
        catch (Exception ex) { AppDialog.Show(this, "Maskinloggen kunde inte sparas: " + ex.Message, "Maskinlogg", MessageBoxButton.OK, MessageBoxImage.Error); }
        Build();
    }

    /// <summary>A new task (null) or a change of name, interval and description.</summary>
    private void EditTask(Guid? id)
    {
        var old = id is Guid g ? _log.Maintenance.FirstOrDefault(t => t.Id == g) : null;
        var name = AppDialog.Prompt(this, "Vad ska göras? T.ex. \"Smörj Z-axelns spindel\".", old is null ? "Ny underhållsuppgift" : "Ändra underhållsuppgift", old?.Name ?? "", okText: "Nästa");
        if (string.IsNullOrWhiteSpace(name)) return;
        var hoursText = AppDialog.Prompt(this, $"Hur ofta, i körtimmar? (\"{name}\")", "Intervall", F(old?.IntervalHours ?? 20), okText: "Nästa");
        if (hoursText is null) return;
        if (!double.TryParse(hoursText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var hours))
        { AppDialog.Show(this, "Intervallet ska vara ett tal i timmar.", "Intervall", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var description = AppDialog.Prompt(this, "Beskrivning (valfri).", "Beskrivning", old?.Description ?? "", okText: "Spara") ?? old?.Description ?? "";
        var candidate = new MaintenanceTask { Name = name, IntervalHours = hours, Description = description };
        if (candidate.Validate() is string error) { AppDialog.Show(this, error, "Underhåll", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Change(log =>
        {
            if (old is not null && log.Maintenance.FirstOrDefault(t => t.Id == old.Id) is { } t) { t.Name = name; t.IntervalHours = hours; t.Description = description; }
            else log.Maintenance.Add(new MaintenanceTask { Name = name, IntervalHours = hours, Description = description, DoneAtHours = log.RunningHours, DoneDate = DateTime.Now });
        });
    }

    private static Border Section(string title, UIElement content) => new()
    {
        Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 10),
        Child = new StackPanel { Children = { new TextBlock { Text = title, FontWeight = FontWeights.Bold, FontSize = 12, Foreground = B(0xA5, 0xC0, 0xD9), Margin = new Thickness(0, 0, 0, 8) }, content } }
    };

    private static StackPanel Stat(string label, string value) => new()
    {
        Margin = new Thickness(0, 0, 8, 0),
        Children = { new TextBlock { Text = label, Foreground = Muted, FontSize = 11 }, new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap } }
    };

    private static DockPanel Line(string when, string what, string detail, Brush color, string? message)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var time = new TextBlock { Text = when, Foreground = Muted, Width = 120, FontFamily = new FontFamily("Consolas"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Top };
        var right = new TextBlock { Text = detail, Foreground = color, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(time, Dock.Left); DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(time); row.Children.Add(right);
        var text = new StackPanel { Children = { new TextBlock { Text = what, TextTrimming = TextTrimming.CharacterEllipsis } } };
        if (message is { Length: > 0 }) text.Children.Add(new TextBlock { Text = message, Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(text);
        return row;
    }

    private static Button Btn(string text, Brush background) => new()
    {
        Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0),
        Foreground = Brushes.White, Background = background, BorderBrush = B(0x50, 0x67, 0x83)
    };

    private static string F(double v) => v.ToString(v < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);
    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
