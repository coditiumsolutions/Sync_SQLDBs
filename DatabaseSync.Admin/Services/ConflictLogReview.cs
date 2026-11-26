using System.Text.Json;

namespace DatabaseSync.Admin.Services;

/// <summary>
/// Reads the worker's conflict-log.jsonl and the JSON / JSONL backup files next to it.
/// The admin site does not open backup tables on the target.
/// </summary>
public static class ConflictLogReview
{
    public const string LogFileName = "conflict-log.jsonl";

    public static string ResolveFolder(string configFilePath, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = "Conflicts";
        }

        if (Path.IsPathRooted(folder))
        {
            return Path.GetFullPath(folder);
        }

        var directory = Path.GetDirectoryName(configFilePath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = AppContext.BaseDirectory;
        }

        return Path.GetFullPath(Path.Combine(directory, folder));
    }

    public static ConflictReview Read(string folder, int maxEntries = 200)
    {
        maxEntries = Math.Clamp(maxEntries, 1, 2000);
        if (!Directory.Exists(folder))
        {
            return new ConflictReview { Folder = folder };
        }

        var entries = new List<ConflictLogEntry>();
        var truncated = false;
        string? warning = null;
        var logPath = Path.Combine(folder, LogFileName);
        var logFound = File.Exists(logPath);
        if (logFound)
        {
            try
            {
                var tail = ReadTail(logPath, maxEntries, 512 * 1024, out var cut);
                truncated = cut;
                foreach (var line in tail)
                {
                    if (TryParse(line, out var entry))
                    {
                        entries.Add(entry);
                    }
                }

                entries.Reverse();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warning = "The conflict log could not be read.";
            }
        }

        return new ConflictReview
        {
            Folder = folder,
            LogFound = logFound,
            Truncated = truncated,
            Warning = warning,
            Entries = entries,
            Files = ListBackups(folder)
        };
    }

    private static bool TryParse(string line, out ConflictLogEntry entry)
    {
        entry = new ConflictLogEntry();
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            entry = new ConflictLogEntry
            {
                LoggedAtUtc = ReadTime(root, "loggedAtUtc"),
                Table = ReadString(root, "table"),
                Target = ReadString(root, "target"),
                Trigger = ReadString(root, "trigger"),
                Operation = ReadString(root, "operation"),
                Key = FormatKey(root),
                MainChangeTimestamp = ReadScalar(root, "mainChangeTimestamp"),
                TargetChangeTimestamp = ReadScalar(root, "targetChangeTimestamp"),
                BackupFile = BackupName(ReadString(root, "backupLocation")),
                ReviewStatus = string.IsNullOrWhiteSpace(ReadString(root, "reviewStatus"))
                    ? "Pending"
                    : ReadString(root, "reviewStatus"),
                DryRun = root.TryGetProperty("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True,
                Preview = BuildPreview(root)
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildPreview(JsonElement root)
    {
        if (!root.TryGetProperty("targetValues", out var target) || target.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        root.TryGetProperty("mainValues", out var main);
        var parts = new List<string>();
        foreach (var property in target.EnumerateObject())
        {
            var targetText = Short(property.Value);
            var mainText = main.ValueKind == JsonValueKind.Object && main.TryGetProperty(property.Name, out var mainValue)
                ? Short(mainValue)
                : "—";
            if (string.Equals(targetText, mainText, StringComparison.Ordinal))
            {
                continue;
            }

            parts.Add($"{property.Name}: {targetText} → {mainText}");
            if (parts.Count == 4)
            {
                break;
            }
        }

        return string.Join(" · ", parts);
    }

    private static string FormatKey(JsonElement root)
    {
        if (!root.TryGetProperty("key", out var key))
        {
            return string.Empty;
        }

        if (key.ValueKind != JsonValueKind.Object)
        {
            return Short(key);
        }

        return string.Join(", ", key.EnumerateObject().Select(property => $"{property.Name}={Short(property.Value)}"));
    }

    private static string Short(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "null",
            _ => value.ToString()
        };
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 48 ? text : text[..45] + "...";
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private static string? ReadScalar(JsonElement root, string name)
    {
        var text = ReadString(root, name);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static DateTimeOffset? ReadTime(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string BackupName(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(location.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? location : name;
    }

    private static IReadOnlyList<ConflictBackupFile> ListBackups(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(IsBackupFile)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(80)
                .Select(file => new ConflictBackupFile
                {
                    RelativePath = Path.GetRelativePath(folder, file.FullName).Replace('\\', '/'),
                    Kind = file.Extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ? "Bulk JSONL" : "Review JSON",
                    Bytes = file.Length,
                    ModifiedUtc = file.LastWriteTimeUtc
                })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsBackupFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals(LogFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ReadTail(string path, int maxLines, int maxBytes, out bool truncated)
    {
        var info = new FileInfo(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var cut = info.Length > maxBytes;
        if (cut)
        {
            stream.Seek(-maxBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream);
        if (cut)
        {
            reader.ReadLine();
        }

        var lines = new Queue<string>();
        var overflow = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            lines.Enqueue(line);
            if (lines.Count > maxLines)
            {
                lines.Dequeue();
                overflow = true;
            }
        }

        truncated = cut || overflow;
        return lines.ToList();
    }
}

public sealed class ConflictReview
{
    public string Folder { get; init; } = string.Empty;

    public bool LogFound { get; init; }

    public bool Truncated { get; init; }

    public string? Warning { get; init; }

    public IReadOnlyList<ConflictLogEntry> Entries { get; init; } = [];

    public IReadOnlyList<ConflictBackupFile> Files { get; init; } = [];

    public int PendingCount =>
        Entries.Count(entry => entry.ReviewStatus.Equals("Pending", StringComparison.OrdinalIgnoreCase));
}

public sealed class ConflictLogEntry
{
    public DateTimeOffset? LoggedAtUtc { get; init; }

    public string Table { get; init; } = string.Empty;

    public string Target { get; init; } = string.Empty;

    public string Trigger { get; init; } = string.Empty;

    public string Operation { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;

    public string? MainChangeTimestamp { get; init; }

    public string? TargetChangeTimestamp { get; init; }

    public string BackupFile { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = "Pending";

    public bool DryRun { get; init; }

    /// <summary>A few target → main field differences. Full rows stay in the backup file.</summary>
    public string Preview { get; init; } = string.Empty;
}

public sealed class ConflictBackupFile
{
    public string RelativePath { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public long Bytes { get; init; }

    public DateTimeOffset ModifiedUtc { get; init; }
}
