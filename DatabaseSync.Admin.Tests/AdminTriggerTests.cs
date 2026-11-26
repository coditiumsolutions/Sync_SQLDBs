using DatabaseSync.Admin.Pages;
using DatabaseSync.Admin.Services;
using DatabaseSync.Core.DTOs;
using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Models;

namespace DatabaseSync.Admin.Tests;

public class ConnectivityProbeTests
{
    [Fact]
    public void Apply_stores_a_successful_probe()
    {
        var connection = new SyncConnection { Name = "Main" };
        ConnectivityProbe.Apply(connection, new DatabaseConnectivityResult
        {
            ConnectionId = "1",
            ConnectionName = "Main",
            Server = "db",
            Database = "app",
            Status = ConnectionStatus.Connected,
            Elapsed = TimeSpan.FromMilliseconds(42),
            ServerVersion = "Microsoft SQL Server 2022",
            CheckedAtUtc = DateTimeOffset.Parse("2026-11-26T06:00:00Z")
        });

        Assert.Equal(ConnectionStatus.Connected, connection.LastConnectivityStatus);
        Assert.Equal(42, connection.LastConnectivityElapsedMs);
        Assert.Equal("Microsoft SQL Server 2022", connection.LastConnectivityServerVersion);
        Assert.Null(connection.LastConnectivityError);
        Assert.Equal(DateTimeOffset.Parse("2026-11-26T06:00:00Z"), connection.LastConnectivityCheckedAtUtc);
    }

    [Fact]
    public void Apply_keeps_the_error_and_clear_resets_it()
    {
        var connection = new SyncConnection();
        ConnectivityProbe.Apply(connection, new DatabaseConnectivityResult
        {
            ConnectionId = "1",
            ConnectionName = "Branch",
            Server = "db",
            Database = "app",
            Status = ConnectionStatus.TimedOut,
            Elapsed = TimeSpan.FromSeconds(15),
            ErrorMessage = "Timeout expired.",
            CheckedAtUtc = DateTimeOffset.UtcNow
        });

        Assert.Equal(ConnectionStatus.TimedOut, connection.LastConnectivityStatus);
        Assert.Equal("Timeout expired.", connection.LastConnectivityError);
        Assert.Equal("Timed out", ConnectionStatusDisplay.Label(connection.LastConnectivityStatus));
        Assert.Equal("clock", ConnectionStatusDisplay.Icon(connection.LastConnectivityStatus));

        connection.ClearLastConnectivity();
        Assert.Equal(ConnectionStatus.Unknown, connection.LastConnectivityStatus);
        Assert.Null(connection.LastConnectivityCheckedAtUtc);
        Assert.Null(connection.LastConnectivityError);
        Assert.Equal("Not tested", ConnectionStatusDisplay.Label(connection.LastConnectivityStatus));
    }
}

public class ConflictLogReviewTests
{
    [Fact]
    public void Missing_folder_is_an_empty_review()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sync-conflicts-" + Guid.NewGuid().ToString("N"));
        var review = ConflictLogReview.Read(folder);

        Assert.False(review.LogFound);
        Assert.Empty(review.Entries);
        Assert.Empty(review.Files);
    }

    [Fact]
    public void Reads_newest_conflicts_and_backup_files()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sync-conflicts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "cycle", "east"));
        try
        {
            var older = Line("2026-11-25T10:00:00Z", "dbo.Orders", 7, "Old", "New");
            var newer = Line("2026-11-25T22:15:40Z", "dbo.Customers", 1042, "East", "Central");
            File.WriteAllText(Path.Combine(folder, "conflict-log.jsonl"), older + "\nnot-json\n" + newer + "\n");
            File.WriteAllText(Path.Combine(folder, "cycle", "east", "Customers.json"), "{}");
            File.WriteAllText(Path.Combine(folder, "cycle", "east", "Orders.jsonl"), "{}\n");

            var review = ConflictLogReview.Read(folder);

            Assert.True(review.LogFound);
            Assert.Equal(2, review.Entries.Count);
            Assert.Equal("dbo.Customers", review.Entries[0].Table);
            Assert.Equal("CustomerId=1042", review.Entries[0].Key);
            Assert.Equal("Pending", review.Entries[0].ReviewStatus);
            Assert.Equal("dbo.Customers.json", review.Entries[0].BackupFile);
            Assert.Contains("East", review.Entries[0].Preview);
            Assert.Contains("Central", review.Entries[0].Preview);
            Assert.Equal(2, review.PendingCount);
            Assert.Equal(2, review.Files.Count);
            Assert.DoesNotContain(review.Files, file => file.RelativePath.EndsWith("conflict-log.jsonl", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(review.Files, file => file.Kind == "Review JSON");
            Assert.Contains(review.Files, file => file.Kind == "Bulk JSONL");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Caps_the_log_at_the_requested_row_count()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sync-conflicts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var lines = Enumerable.Range(1, 5)
                .Select(i => Line($"2026-11-25T10:0{i}:00Z", "dbo.T", i, "a", "b"));
            File.WriteAllLines(Path.Combine(folder, "conflict-log.jsonl"), lines);

            var review = ConflictLogReview.Read(folder, maxEntries: 2);

            Assert.True(review.Truncated);
            Assert.Equal(2, review.Entries.Count);
            Assert.Equal("CustomerId=5", review.Entries[0].Key);
            Assert.Equal("CustomerId=4", review.Entries[1].Key);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string Line(string when, string table, int id, string targetName, string mainName) =>
        "{\"loggedAtUtc\":\"" + when + "\",\"table\":\"" + table + "\",\"target\":\"Branch east\",\"trigger\":\"Manual\",\"operation\":\"Update\",\"key\":{\"CustomerId\":" + id + "},\"targetValues\":{\"Name\":\"" + targetName + "\"},\"mainValues\":{\"Name\":\"" + mainName + "\"},\"mainChangeTimestamp\":\"2026-11-20T08:00:00\",\"targetChangeTimestamp\":\"2026-11-25T18:02:00\",\"reviewStatus\":\"Pending\",\"dryRun\":false,\"backupLocation\":\"/var/backups/" + table + ".json\"}";
}

public class TriggerSettingsEditorTests
{
    [Fact]
    public void Removes_a_time_keeps_interval_off_and_does_not_flip_one_way_sync()
    {
        var input = BaseInput();
        input.ScheduledSyncEnabled = true;
        input.IntervalSyncEnabled = false;
        input.IntervalValue = 6;
        input.IntervalUnit = "Hours";
        input.Schedules =
        [
            new SettingsModel.ScheduleInput { Id = "keep", Time = "09:00", Enabled = true, DaysOfWeek = ["monday"] },
            new SettingsModel.ScheduleInput { Id = "drop", Time = "18:30", Enabled = true, Remove = true }
        ];

        var errors = TriggerSettingsEditor.Validate(input);
        Assert.Empty(errors);

        var settings = new SyncSettings { SyncMainToOthers = false, IntervalSyncEnabled = true };
        TriggerSettingsEditor.Apply(settings, input);

        Assert.Single(settings.Schedules);
        Assert.Equal("keep", settings.Schedules[0].Id);
        Assert.Equal(["Monday"], settings.Schedules[0].DaysOfWeek);
        Assert.Null(settings.Schedules[0].Cron);
        Assert.True(settings.ScheduledSyncEnabled);
        Assert.False(settings.IntervalSyncEnabled);
        Assert.Equal(6, settings.IntervalValue);
        Assert.Equal(SyncIntervalUnit.Hours, settings.IntervalUnit);
        Assert.False(settings.SyncMainToOthers);
    }

    [Fact]
    public void Restore_flag_turns_one_way_sync_back_on()
    {
        var input = BaseInput();
        input.RestoreOneWaySync = true;
        var settings = new SyncSettings { SyncMainToOthers = false };
        TriggerSettingsEditor.Apply(settings, input);
        Assert.True(settings.SyncMainToOthers);
    }

    [Fact]
    public void Cron_replaces_the_clock_and_a_bad_clock_is_ignored()
    {
        var input = BaseInput();
        input.Schedules =
        [
            new SettingsModel.ScheduleInput { UseCron = true, Time = "not-a-time", Cron = "0 9 * * 1-5", Enabled = true }
        ];

        Assert.Empty(TriggerSettingsEditor.Validate(input));
        var settings = new SyncSettings();
        TriggerSettingsEditor.Apply(settings, input);
        Assert.Equal("0 9 * * 1-5", settings.Schedules[0].Cron);
        Assert.Empty(settings.Schedules[0].DaysOfWeek);
    }

    [Fact]
    public void Clock_mode_ignores_a_leftover_cron_string()
    {
        var input = BaseInput();
        input.Schedules =
        [
            new SettingsModel.ScheduleInput { UseCron = false, Time = "18:30", Cron = "not cron", Enabled = true }
        ];

        Assert.Empty(TriggerSettingsEditor.Validate(input));
        var settings = new SyncSettings();
        TriggerSettingsEditor.Apply(settings, input);
        Assert.Null(settings.Schedules[0].Cron);
        Assert.Equal("18:30", settings.Schedules[0].Time);
    }

    [Fact]
    public void Rejects_a_bad_clock_and_a_bad_cron()
    {
        var clock = BaseInput();
        clock.Schedules = [new SettingsModel.ScheduleInput { Time = "9am" }];
        Assert.Contains(TriggerSettingsEditor.Validate(clock), error => error.Contains("HH:mm", StringComparison.Ordinal));

        var cron = BaseInput();
        cron.Schedules = [new SettingsModel.ScheduleInput { UseCron = true, Cron = "nope" }];
        Assert.Contains(TriggerSettingsEditor.Validate(cron), error => error.StartsWith("Input.Schedules[0].Cron", StringComparison.Ordinal));
    }

    [Fact]
    public void New_interval_input_defaults_to_off()
    {
        var input = new SettingsModel.InputModel();
        Assert.False(input.IntervalSyncEnabled);
        Assert.False(input.ScheduledSyncEnabled);
    }

    private static SettingsModel.InputModel BaseInput() => new()
    {
        ConflictBackupFolder = "Conflicts",
        ConflictFileRowThreshold = 1000,
        ConflictFileMaxMegabytes = 5,
        IntervalValue = 2,
        IntervalUnit = "Minutes",
        RetryIntervalSeconds = 15,
        MaximumRetryAttempts = 2,
        CommandTimeoutSeconds = 180
    };
}
