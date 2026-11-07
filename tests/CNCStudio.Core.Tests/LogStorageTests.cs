namespace CNCStudio.Core.Tests;

/// <summary>Logs live in CNC Studio\Loggar\&lt;dator&gt; so every computer's logs can be read from any computer.</summary>
public class LogStorageTests
{
    private static string Temp() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cncstudio-logs-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Logs_live_in_a_folder_per_computer_inside_the_data_folder()
    {
        var root = Temp();
        var storage = new AppStorage(Path.Combine(root, "local"), Path.Combine(root, "CNC Studio"));
        Assert.Equal(Path.Combine(root, "CNC Studio", "Loggar", Environment.MachineName), storage.LogsFolder);
        storage.EnsureFolders();
        Assert.True(Directory.Exists(storage.LogsFolder));
    }

    [Fact]
    public void Old_local_logs_are_copied_once_and_existing_files_are_kept()
    {
        var root = Temp();
        var oldLogs = Directory.CreateDirectory(Path.Combine(root, "local", "logs")).FullName;
        File.WriteAllText(Path.Combine(oldLogs, "fel.txt"), "gammal");
        File.WriteAllText(Path.Combine(oldLogs, "forsta-test-20261107-120629.txt"), "test");
        var target = Directory.CreateDirectory(Path.Combine(root, "CNC Studio", "Loggar", Environment.MachineName)).FullName;
        File.WriteAllText(Path.Combine(target, "fel.txt"), "ny");

        var storage = new AppStorage(Path.Combine(root, "local"), Path.Combine(root, "CNC Studio"));
        Assert.Equal("ny", File.ReadAllText(Path.Combine(storage.LogsFolder, "fel.txt")));
        Assert.Equal("test", File.ReadAllText(Path.Combine(storage.LogsFolder, "forsta-test-20261107-120629.txt")));
        Assert.True(File.Exists(Path.Combine(oldLogs, "fel.txt")));             // originals are left alone
    }

    [Fact]
    public void Choosing_a_sync_folder_brings_this_computers_logs_along()
    {
        var root = Temp();
        var storage = new AppStorage(Path.Combine(root, "local"), Path.Combine(root, "CNC Studio"));
        Directory.CreateDirectory(storage.LogsFolder);
        File.WriteAllText(Path.Combine(storage.LogsFolder, "maskin-20261107-130000.txt"), "session");
        var drive = Directory.CreateDirectory(Path.Combine(root, "Drive", "CNC Studio")).FullName;

        var copied = storage.SetSyncFolder(drive);
        Assert.Equal(Path.Combine(drive, "Loggar", Environment.MachineName), storage.LogsFolder);
        Assert.True(File.Exists(Path.Combine(storage.LogsFolder, "maskin-20261107-130000.txt")));
        Assert.Contains(Path.Combine("Loggar", Environment.MachineName), copied);
    }

    [Fact]
    public void The_catalog_lists_this_computer_first_and_every_other_computer()
    {
        var root = Temp();
        foreach (var pc in new[] { "VERKSTAD", "KONTOR" }) Directory.CreateDirectory(Path.Combine(root, pc));
        var computers = LogCatalog.Computers(root, "KONTOR");
        Assert.Equal(new[] { "KONTOR", "VERKSTAD" }, computers);
        Assert.Equal(new[] { "HEMMA", "KONTOR", "VERKSTAD" }, LogCatalog.Computers(root, "HEMMA"));
        Assert.Equal(new[] { "HEMMA" }, LogCatalog.Computers(Path.Combine(root, "saknas"), "HEMMA"));
    }

    [Fact]
    public void The_catalog_groups_a_computers_logs_newest_first()
    {
        var root = Temp();
        var pc = Directory.CreateDirectory(Path.Combine(root, "VERKSTAD")).FullName;
        foreach (var name in new[] { "maskin-20261107-090000.txt", "maskin-20261107-140000.txt", "forsta-test-20261107-120629.txt",
                     "$$-backup-20261107-120700.txt", "annat.txt" })
            File.WriteAllText(Path.Combine(pc, name), "");
        var files = LogCatalog.Files(root, "VERKSTAD");
        Assert.Equal(new[] { LogKind.Error, LogKind.Machine, LogKind.Machine, LogKind.FirstTest, LogKind.SettingsBackup }, files.Select(f => f.Kind));
        Assert.EndsWith("maskin-20261107-140000.txt", files[1].Path);
        Assert.Equal(new DateTime(2026, 11, 7, 12, 6, 29), files[3].Time);
    }

    [Fact]
    public void A_session_log_is_written_in_batches_and_finished_on_dispose()
    {
        var folder = Temp();
        string path;
        using (var log = new SessionLogFile(folder, new DateTime(2026, 11, 7, 13, 5, 9), "CNC Studio – maskinstyrning"))
        {
            path = log.FilePath;
            Assert.EndsWith("maskin-20261107-130509.txt", path);
            log.Append("13:05:10  → $H");
            Assert.DoesNotContain("$H", File.ReadAllText(path));                // not yet flushed
            log.Flush();
            Assert.Contains("→ $H", File.ReadAllText(path));
            log.Append("13:05:20  ← ok");
        }
        var text = File.ReadAllText(path);
        Assert.StartsWith("CNC Studio – maskinstyrning", text);
        Assert.Contains("← ok", text);
    }
}
