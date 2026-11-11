# Sync_SQLDBs

Coditium one-way SQL Server synchronisation (.NET 8 Worker + Admin UI).

The worker sends **differences** from the nominated Main database to every other enabled database. Main always wins. The whole table is not copied when only some rows changed.

```
DatabaseSync.sln
├── DatabaseSync.Worker          # Windows Service / BackgroundService
├── DatabaseSync.Core            # Models, differential and schedule rules
├── DatabaseSync.Infrastructure  # SQL sync engine (Dapper / ADO.NET)
├── DatabaseSync.Admin           # Admin Web UI
└── DatabaseSync.Core.Tests      # Diff, conflict, and schedule tests
```

Copy `DatabaseSync.Worker/sync-config.example.json` to `sync-config.json` and set real credentials. Never commit passwords. `sync-config.json` is gitignored.

## Prerequisites

- .NET 8 SDK
- Windows, for Windows Service hosting (the engine itself is ordinary .NET)
- Network access to the SQL Server instances
- SQL Server 2016 or later, including SQL Server Express

Row fingerprints use `HASHBYTES('SHA2_256', …)`. SQL Server 2016 removed the old 8,000-byte input limit for `MAX` types, which is what lets a wide column hash in full. No SQL Agent, Change Tracking, or CDC is required, and none of those are turned on.

## Configure connections

Edit `sync-config.json` next to the worker (Admin reads the same file):

```json
"Connections": [
  {
    "Id": "main-server",
    "Name": "Main Cloud",
    "IsMain": true,
    "IsEnabled": true,
    "Server": "MAIN_PRIVATE_IP",
    "Database": "YourMainDb",
    "IntegratedSecurity": false,
    "UserId": "sync_user",
    "Password": "",
    "Encrypt": true,
    "TrustServerCertificate": true
  }
]
```

Leave `IntegratedSecurity` false for a SQL login. For Windows authentication set it true, leave the password empty, and run the service as an account that can log in to each instance.

Do not put production passwords in the file that you commit. For local development prefer environment variables or a private `sync-config.json`. The example file uses `YOUR_PASSWORD` placeholders only.

## When a cycle runs

Three triggers share one differential engine and one lock. A second trigger that fires while a cycle is running does **not** start another cycle.

| Trigger | How it starts | Default |
| --- | --- | --- |
| Manual | **Sync now** on the Admin dashboard. Always available. | On |
| Scheduled | Each saved time fires once per occurrence, in the host's local time zone. | Off |
| Interval | Every N minutes, hours, or days. | Off |

Overlap rules:

- Manual, scheduled, and interval never run together. The in-process guard plus a `sync-run.lock` file next to the config cover the worker and the Admin site.
- A manual click made during a running cycle stays queued as a single request and runs once when the lock is free.
- A scheduled occurrence that falls during a running cycle stays due until its grace window ends (`ScheduleGraceMinutes`, default 15). It is not stacked. If the window expires first, that occurrence is missed.
- An interval tick that falls during a running cycle is skipped. The next tick is one full interval later. Missed interval ticks are not replayed.
- If a manual or scheduled cycle starts while an interval tick is already due, that tick is consumed so interval sync does not start again immediately.
- The worker must be running for scheduled and interval sync. **Sync now** runs in the Admin process when it can take the lock, so it still works when the worker is stopped.

The dashboard shows which trigger started the current or last cycle, plus the last and next run times.

### Schedules

Each entry is either:

- a local `Time` (`HH:mm`) and optional `DaysOfWeek` (empty means every day), or
- a 5-field cron expression in local time: `minute hour day-of-month month day-of-week`.

Cron supports `*`, lists, ranges, steps, and month or day names. `0 9 * * 1-5` is 09:00 on Monday–Friday. When both day-of-month and day-of-week are restricted, either one matching is enough (standard cron). `0` and `7` are Sunday. `FRI-SUN` includes Sunday.

### Legacy settings

`EnableSynchronization`, `SyncIntervalSeconds`, `AutoSyncEveryMinutes`, and the old daily window are no longer what starts a cycle.

On first load of an old file (`TriggerConfigVersion` missing or 0):

- `EnableSynchronization: true` migrates to interval sync, using `AutoSyncEveryMinutes` when it is set, otherwise `SyncIntervalSeconds`.
- `EnableSynchronization: false` leaves scheduled and interval sync off.
- The old start/end window is not converted. Use schedules for time of day.

After migration the legacy fields are mirrors only (`EnableSynchronization` is true when scheduled or interval sync is on). Editing them by hand does not turn sync back on.

## Differential sync

For each user table that has a primary key, ordered parent-first:

1. A single integer primary key is compared in ranges, on the servers. Both sides run one grouped checksum query. Ranges that match are not read again, so a no-change table sends a few dozen bytes and no row payload. A mismatched range wider than 2,000 keys is split, and only the slices that still differ continue.
2. Each mismatched slice is read from both servers in one query: primary key plus an 8-byte `BINARY_CHECKSUM` / `CHECKSUM` pair. The worker classifies that list in memory (missing, equal, different) and bulk-fetches only the different keys. Materializing `SHA-256` for every row in the slice, then bulk-copying it onto the target for a join, was slower on this link than copying the rows and no smaller for these narrow tables. Tables whose primary key is not a single integer still do that staged join: one `SqlBulkCopy` of `(primary key, SHA-256)` into a temp table, one local hash, one join. Unchanged rows are not written. A clustered 10% change therefore moves the checksum list for the dirty slices and about that many full rows, in one or two bulk fetches.
3. The hash is `HASHBYTES('SHA2_256', …)` over the copyable non-key columns in ordinal order. Columns are concatenated with `NCHAR(30)` between them. `NULL` is `NCHAR(0)`. A value is `NCHAR(1)`, its character length, and a canonical text form (ISO dates, hex for binary), so an empty value is not the same hash as `NULL` and adjacent columns cannot blur. Primary keys, computed columns, and `rowversion` are excluded. The hash is computed in the `SELECT`. No persisted hash column is added.
4. Full rows move only for keys the join marked insert or update, then one `MERGE`. Deletes send keys only. Apply batches are about 4,000 rows (`DiffApplyBatchSize`, capped there) so the target does not escalate locks. That chunking is local. It is not how the comparison talks to the network.
5. An empty target is treated as “every Main row is an insert” and skips the fingerprint stream.
6. Interval cycles remember the source's max change timestamp and `rowversion` in the worker process. The next interval cycles hash only source rows newer than that watermark, which keeps a quiet cycle from shipping the whole key list. Deletes are invisible to a watermark, so every 6th interval cycle is a full hash. Manual and scheduled cycles are always a full hash. Restarting the worker forgets the watermark and the next interval cycle is a full hash.

Tables **without a primary key are skipped** with a warning. They are not copied. A table whose rows cannot be identified cannot be updated safely.

Schema must already match. This tool does not create or alter production tables unless you opt in to the conflict backup table or `sync.ConflictLog` below. The `sync` schema and names starting with `ConflictBackup_` are excluded from discovery so backups are not synced back.

`DryRun: true` computes the same diff, writes conflict review files, and does not change target data or create backup tables.

Each table logs inserted, updated, deleted, conflicted, and unchanged rows, rows transferred, an estimated byte count (fingerprint payload plus the rows or keys actually sent), the backup location, and the change column that was used.

## Conflicts

Main's row is always what remains. A conflict only decides whether the target's current row is saved first.

A comparable change column is the first configured name (`ChangeTimestampColumnNames`, or the built-in list when that is empty) whose type is `datetime`, `datetime2`, `datetimeoffset`, or `smalldatetime`. `rowversion` is never used: those counters are not comparable across servers. `datetimeoffset` is compared in UTC. `datetime` / `datetime2` / `smalldatetime` are compared as the clock value stored in the column, so both databases need to store that column the same way.

| Change | Conflict when |
| --- | --- |
| Insert | Never. The target has no row to keep. |
| Update | The target timestamp is strictly greater than Main's. A null target timestamp is not newer. A target timestamp with a null Main timestamp is newer. |
| Delete | The target timestamp is strictly greater than every change timestamp still present on Main. If Main has no timestamps and the target row has one, the target row is kept as a conflict. A null target timestamp is not newer. |
| No change column | Not a conflict. Main is applied and nothing is backed up. |

`AssumeDifferingRowsConflict: true` backs up every update and delete even when there is no change column or the target is not newer. That is off by default because it can be large.

Equal timestamps are not conflicts.

Before the target row is overwritten or deleted, the conflict is written:

- At or below `ConflictFileRowThreshold` (default 1,000) **and** `ConflictFileMaxBytes` (default 5 MB): one JSON review file per table per cycle.
- Above either ceiling: a JSONL bulk file.
- Above either ceiling and `UseConflictBackupTables: true`: a table `sync.ConflictBackup_<table>_<stamp>` on the target (full target row plus operation, cycle id, and time). This is off by default. If creating it fails, the engine falls back to JSONL.

The review file is flushed before that batch's changes are applied. Every conflict is also appended to `Conflicts/conflict-log.jsonl` (or `ConflictBackupFolder`) with table, key, target values, main values, both change timestamps, UTC time, backup location, trigger, and `reviewStatus: "Pending"`.

`WriteConflictLogTable: true` also inserts those fields into `sync.ConflictLog` on the target. Off by default. The JSONL log is still written. A failure to create the SQL log does not stop the data sync.

## Other settings

`SyncMainToOthers` must stay true for a cycle to do anything. `CommandTimeoutSeconds` applies to fingerprint and apply commands; raise it for large tables. `TriggerPollSeconds` (default 5) is how often the worker looks for a due trigger.

Foreign keys on the target are set to `NOCHECK` for the duration of that target's cycle so parent-first inserts and deletes can land, then turned back on. Existing triggers on the target still fire for the rows that change.

## Run locally

```powershell
dotnet restore
dotnet build
dotnet test DatabaseSync.Core.Tests
dotnet run --project DatabaseSync.Worker
dotnet run --project DatabaseSync.Admin
```

The Admin UI listens on `http://0.0.0.0:5088` unless `Urls` is set. Logs from the worker go to the console and to `DatabaseSync.Worker/Logs/DatabaseSync-YYYYMMDD.log`.

## Publish and install the Windows Service

```powershell
dotnet publish DatabaseSync.Worker\DatabaseSync.Worker.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o .\publish\DatabaseSync
```

Copy `publish\DatabaseSync` to the server, put a real `sync-config.json` beside the executable, then:

```powershell
$publishPath = "C:\Services\DatabaseSync"
$exePath = Join-Path $publishPath "DatabaseSync.Worker.exe"

sc.exe create "Coditium.DatabaseSync" binPath= "`"$exePath`"" start= auto DisplayName= "Coditium Database Sync"
sc.exe description "Coditium.DatabaseSync" "Centralized SQL Server differential synchronisation worker."
sc.exe start "Coditium.DatabaseSync"
```

Stop with `sc.exe stop "Coditium.DatabaseSync"`. Uninstall with `sc.exe delete "Coditium.DatabaseSync"` after stopping it.

Run the service as a dedicated account when using Windows authentication.

## Limitations

- SQL Server 2014 and earlier can fail hashing a value larger than 8,000 bytes.
- Tables without a primary key are skipped, not fully copied.
- A hash collision would hide a change. SHA-256 makes that negligible; it is not a cryptographic audit.
- String primary keys are joined in SQL, so collation is honored there. Conflict timestamp lookup uses the key values returned to .NET.
- There is no delete tombstone on Main. A target-only row is "newer" only by the rule in the table above.
- The first interval cycle is due on the next worker poll after the mode is enabled. Later cycles wait one full interval after the previous slot.
- Schedules use the clock of the machine running the worker.
- Estimated byte counts are based on typical column widths, not bytes on the wire.
- Opt-in backup tables and `sync.ConflictLog` are the only schema changes, and both default to off.
