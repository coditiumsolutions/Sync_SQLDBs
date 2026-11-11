using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

var configPath = Environment.GetEnvironmentVariable("SYNC_CONFIG")
    ?? Path.Combine(FindRepoRoot(), "DatabaseSync.Worker", "sync-config.json");
if (!File.Exists(configPath))
{
    Console.Error.WriteLine("BLOCKED: sync-config.json was not found. Not using a local SQL Server.");
    return 2;
}

var secret = SecretConfig.Load(configPath);
var reachable = new List<SecretEndpoint>();
Console.WriteLine("Connectivity (password redacted):");
foreach (var endpoint in secret.Endpoints)
{
    var probe = await ProbeAsync(endpoint);
    Console.WriteLine($"  {probe}");
    if (probe.Ok)
    {
        reachable.Add(endpoint);
    }
}

var realMain = secret.Endpoints.FirstOrDefault(endpoint => endpoint.IsMain);
var targets = secret.Endpoints.Where(endpoint => !endpoint.IsMain).ToList();
var mainReachable = realMain is not null && reachable.Any(endpoint => endpoint.Id == realMain.Id);
SecretEndpoint source;
SecretEndpoint target;
var substitute = false;
if (mainReachable && targets.Any(endpoint => reachable.Any(ok => ok.Id == endpoint.Id)))
{
    source = realMain!;
    target = targets.First(endpoint => reachable.Any(ok => ok.Id == endpoint.Id));
}
else if (!mainReachable && targets.Count(endpoint => reachable.Any(ok => ok.Id == endpoint.Id)) >= 2)
{
    source = targets.First(endpoint => endpoint.Name.Equals("Cloud Server", StringComparison.OrdinalIgnoreCase) && reachable.Any(ok => ok.Id == endpoint.Id));
    target = targets.First(endpoint => endpoint.Name.Equals("Cloud Server 2", StringComparison.OrdinalIgnoreCase) && reachable.Any(ok => ok.Id == endpoint.Id));
    substitute = true;
    Console.WriteLine("MAIN 172.20.228.2 is not reachable. Using Cloud Server as the stand-in main and Cloud Server 2 as the only target.");
}
else
{
    Console.Error.WriteLine("BLOCKED: not enough reachable test servers to run old vs new.");
    return 2;
}

var options = BenchOptions.Parse(args);
var root = FindRepoRoot();
var resultsDir = Path.Combine(root, "benchmarks", "results");
Directory.CreateDirectory(resultsDir);
var newDll = Path.Combine(root, "benchmarks", "NewSyncHost", "bin", "Release", "net8.0", "NewSyncHost.dll");
var oldDll = Path.Combine(root, "benchmarks", ".build", "old-src", "OldSyncHost", "bin", "Release", "net8.0", "OldSyncHost.dll");
if (!File.Exists(newDll) || !File.Exists(oldDll))
{
    throw new InvalidOperationException("Build both sync hosts before running the benchmark.");
}

var runs = new List<RunRecord>();
var cleaned = false;
try
{
    await EnsureSchemaAsync(source);
    await EnsureSchemaAsync(target);
    await EnsureNumbersAsync(source);

    var sizes = new (string Name, int Customers, int Orders, int Lines)[]
    {
        ("10k", 1_000, 2_000, 10_000),
        ("100k", 5_000, 20_000, 100_000),
        ("1m", 20_000, 200_000, 1_000_000)
    };

    foreach (var size in sizes.Where(size => options.Sizes.Contains(size.Name)))
    {
        Console.WriteLine($"Seeding bench tables for {size.Name}");
        await ResetAsync(source);
        await ResetAsync(target);
        await SeedAsync(source, size.Customers, size.Orders, size.Lines);
        var widths = await RowWidthsAsync(source);

        foreach (var scenario in new[] { "initial", "steady", "delta-1pct", "delta-10pct", "conflicts" }.Where(options.Scenarios.Contains))
        {
            foreach (var engine in new[] { "old", "new" })
            {
                for (var repeat = 1; repeat <= options.Repeats; repeat++)
                {
                    Console.WriteLine($"RUN size={size.Name} scenario={scenario} engine={engine} repeat={repeat}");
                    await ResetAsync(source);
                    await ResetAsync(target);
                    await SeedAsync(source, size.Customers, size.Orders, size.Lines);
                    if (scenario != "initial")
                    {
                        var prep = await RunHostAsync(newDll, source, target, secret, Path.Combine(Path.GetTempPath(), "bench-conflicts-prep"));
                        if (!prep.Success)
                        {
                            throw new InvalidOperationException("Prep sync failed: " + prep.Error);
                        }
                    }

                    var expectedConflicts = 0;
                    if (scenario == "delta-1pct")
                    {
                        await ApplyDeltaAsync(source, 1);
                    }
                    else if (scenario == "delta-10pct")
                    {
                        await ApplyDeltaAsync(source, 10);
                    }
                    else if (scenario == "conflicts")
                    {
                        expectedConflicts = await ApplyConflictsAsync(source, target, 20, Math.Min(1500, Math.Max(50, size.Lines / 10)));
                    }

                    var conflictDir = Path.Combine(Path.GetTempPath(), "bench-conflicts", $"{size.Name}-{scenario}-{engine}-{repeat}");
                    if (Directory.Exists(conflictDir))
                    {
                        Directory.Delete(conflictDir, true);
                    }

                    Directory.CreateDirectory(conflictDir);
                    var beforeSource = await SqlCountersAsync(source);
                    var beforeTarget = await SqlCountersAsync(target);
                    var dll = engine == "old" ? oldDll : newDll;
                    var measured = await RunHostAsync(dll, source, target, secret, conflictDir);
                    var afterSource = await SqlCountersAsync(source);
                    var afterTarget = await SqlCountersAsync(target);
                    var match = await SignaturesMatchAsync(source, target);
                    var conflicts = CountConflicts(conflictDir);
                    var payloadBytes = engine == "new"
                        ? measured.EstimatedBytes
                        : EstimateOldBytes(measured.RowsRead, widths, size);
                    var record = new RunRecord
                    {
                        Size = size.Name,
                        Scenario = scenario,
                        Engine = engine,
                        Repeat = repeat,
                        Success = measured.Success && match.Match && (scenario != "conflicts" || engine == "old" || conflicts.LogLines >= expectedConflicts),
                        EngineElapsedMs = measured.EngineElapsedMs,
                        ProcessElapsedMs = measured.ProcessElapsedMs,
                        PeakRssMb = measured.PeakRssMb,
                        AvgCpuPercent = measured.AvgCpuPercent,
                        RowsRead = measured.RowsRead,
                        RowsWritten = measured.RowsWritten,
                        RowsInserted = measured.RowsInserted,
                        RowsUpdated = measured.RowsUpdated,
                        RowsDeleted = measured.RowsDeleted,
                        RowsConflicted = measured.RowsConflicted,
                        PayloadBytes = payloadBytes,
                        SourceNetworkBytes = Math.Max(0, afterSource.BytesSent - beforeSource.BytesSent),
                        TargetNetworkBytes = Math.Max(0, afterTarget.BytesReceived - beforeTarget.BytesReceived),
                        SourceWorkerMs = Math.Max(0, (afterSource.WorkerUs - beforeSource.WorkerUs) / 1000),
                        TargetWorkerMs = Math.Max(0, (afterTarget.WorkerUs - beforeTarget.WorkerUs) / 1000),
                        DataMatch = match.Match,
                        MatchDetail = match.Detail,
                        ConflictLogLines = conflicts.LogLines,
                        ConflictJsonFiles = conflicts.JsonFiles,
                        ConflictJsonlFiles = conflicts.JsonlFiles,
                        ExpectedConflicts = expectedConflicts,
                        AuditHeapTargetRows = match.AuditHeapTarget,
                        Error = measured.Error
                    };
                    runs.Add(record);
                    Console.WriteLine(
                        $"  elapsed={record.EngineElapsedMs}ms bytes={record.PayloadBytes} rowsRead={record.RowsRead} rowsWritten={record.RowsWritten} match={record.DataMatch} conflicts={record.ConflictLogLines} success={record.Success} {record.Error}");
                    if (!record.Success && options.StopOnFailure)
                    {
                        throw new InvalidOperationException($"Run failed: {size.Name} {scenario} {engine} #{repeat}. {record.Error} {record.MatchDetail}");
                    }
                }
            }
        }
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("Benchmark stopped: " + ex.GetType().Name + " " + Redact(ex.Message, secret));
}
finally
{
    try
    {
        await DropBenchAsync(source);
        await DropBenchAsync(target);
        cleaned = true;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Cleanup failed: " + ex.GetType().Name);
    }

    await WriteReportAsync(resultsDir, runs, substitute, cleaned);
}
Console.WriteLine(cleaned
    ? "bench schema dropped on both servers. Report: benchmarks/results/REPORT.md"
    : "Cleanup did not finish. bench objects may remain. Report: benchmarks/results/REPORT.md");
return runs.Count > 0 && runs.All(run => run.Success) ? 0 : 1;

static long EstimateOldBytes(long rowsRead, RowWidths widths, (string Name, int Customers, int Orders, int Lines) size)
{
    var weight = (double)(size.Customers * widths.Customer + size.Orders * widths.Order + size.Lines * widths.Line);
    var count = Math.Max(1, size.Customers + size.Orders + size.Lines);
    return (long)(rowsRead * (weight / count));
}

static async Task<MeasuredRun> RunHostAsync(string dll, SecretEndpoint source, SecretEndpoint target, SecretConfig secret, string conflictDir)
{
    var runConfig = Path.Combine(Path.GetTempPath(), "bench-sync-run.json");
    secret.WriteRunFile(runConfig, source, target, conflictDir);
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        WorkingDirectory = Path.GetDirectoryName(dll)!,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    psi.ArgumentList.Add(dll);
    psi.Environment["SyncConfigStore__FilePath"] = runConfig;
    psi.Environment["BENCH_SCHEMA"] = "bench";
    psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    psi.Environment["DOTNET_NOLOGO"] = "1";
    var homeDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
    var path = Environment.GetEnvironmentVariable("PATH") ?? "";
    if (!path.Contains(homeDotnet, StringComparison.Ordinal))
    {
        psi.Environment["PATH"] = homeDotnet + Path.PathSeparator + path;
    }

    using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start sync host.");
    var stdout = new StringBuilder();
    var stderr = new StringBuilder();
    process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
    process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(Redact(e.Data, secret)); };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    var samples = new List<(double Cpu, long Rss)>();
    var clock = Stopwatch.StartNew();
    var lastCpu = ReadCpuTicks(process.Id);
    var lastStamp = Stopwatch.GetTimestamp();
    while (!process.HasExited)
    {
        await Task.Delay(250);
        var nowCpu = ReadCpuTicks(process.Id);
        var nowStamp = Stopwatch.GetTimestamp();
        var elapsedSec = (nowStamp - lastStamp) / (double)Stopwatch.Frequency;
        if (elapsedSec > 0)
        {
            samples.Add((((nowCpu - lastCpu) / 100.0) / elapsedSec * 100.0, ReadRssKb(process.Id)));
        }

        lastCpu = nowCpu;
        lastStamp = nowStamp;
        if (clock.Elapsed > TimeSpan.FromMinutes(40))
        {
            process.Kill(entireProcessTree: true);
            break;
        }
    }

    await process.WaitForExitAsync();
    try { File.Delete(runConfig); } catch { /* temp config */ }
    var text = stdout.ToString();
    var marker = text.Split('\n').LastOrDefault(line => line.StartsWith("BENCH_JSON:", StringComparison.Ordinal));
    if (marker is null)
    {
        return new MeasuredRun(false, clock.ElapsedMilliseconds, clock.ElapsedMilliseconds, 0, 0, 0, 0, 0, 0, 0, 0, 0, Trim(Redact(stderr.ToString(), secret)));
    }

    using var json = JsonDocument.Parse(marker["BENCH_JSON:".Length..]);
    var rootJson = json.RootElement;
    long LongOf(string name) => rootJson.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
    var success = rootJson.TryGetProperty("success", out var ok) && ok.GetBoolean();
    var error = rootJson.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String ? Redact(err.GetString(), secret) : null;
    return new MeasuredRun(
        success,
        LongOf("engineElapsedMs"),
        clock.ElapsedMilliseconds,
        samples.Count == 0 ? 0 : samples.Max(sample => sample.Rss) / 1024.0,
        samples.Count == 0 ? 0 : samples.Average(sample => sample.Cpu),
        LongOf("rowsRead"),
        LongOf("rowsWritten"),
        LongOf("rowsInserted"),
        LongOf("rowsUpdated"),
        LongOf("rowsDeleted"),
        LongOf("rowsConflicted"),
        LongOf("estimatedBytes"),
        success ? null : error);
}

static async Task EnsureSchemaAsync(SecretEndpoint endpoint)
{
    const string sql = """
        IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'bench') EXEC(N'CREATE SCHEMA bench');
        IF OBJECT_ID(N'bench.OrderLine', N'U') IS NULL
        BEGIN
            CREATE TABLE bench.Customer
            (
                CustomerId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_bench_Customer PRIMARY KEY,
                Code nvarchar(32) NOT NULL,
                Name nvarchar(200) NOT NULL,
                Email nvarchar(256) NULL,
                Balance decimal(18,2) NOT NULL,
                IsActive bit NOT NULL,
                CreatedAt datetime2(3) NOT NULL,
                UpdatedAt datetime2(3) NOT NULL,
                Notes nvarchar(max) NULL,
                RowVer rowversion NOT NULL
            );
            CREATE TABLE bench.OrderHeader
            (
                OrderId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_bench_OrderHeader PRIMARY KEY,
                CustomerId int NOT NULL CONSTRAINT FK_bench_OrderHeader_Customer REFERENCES bench.Customer(CustomerId),
                OrderNumber char(12) NOT NULL,
                OrderDate datetime2(3) NOT NULL,
                Total money NOT NULL,
                Status tinyint NOT NULL,
                UpdatedAt datetime2(3) NOT NULL,
                Payload varbinary(256) NULL
            );
            CREATE TABLE bench.OrderLine
            (
                OrderLineId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_bench_OrderLine PRIMARY KEY,
                OrderId int NOT NULL CONSTRAINT FK_bench_OrderLine_OrderHeader REFERENCES bench.OrderHeader(OrderId),
                Sku varchar(32) NOT NULL,
                Qty int NOT NULL,
                UnitPrice decimal(19,4) NOT NULL,
                UpdatedAt datetime2(3) NULL
            );
            CREATE TABLE bench.AuditHeap
            (
                EventTime datetime2(3) NOT NULL,
                Message nvarchar(200) NOT NULL
            );
        END
        """;
    await ExecAsync(endpoint, sql);
}

static async Task EnsureNumbersAsync(SecretEndpoint source)
{
    const string sql = """
        IF OBJECT_ID(N'bench.Nums', N'U') IS NULL
        BEGIN
            CREATE TABLE bench.Nums (n int NOT NULL);
            CREATE CLUSTERED INDEX IX_bench_Nums ON bench.Nums(n);
            INSERT INTO bench.Nums(n)
            SELECT TOP (1000000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c;
        END
        """;
    await ExecAsync(source, sql);
}

static async Task ResetAsync(SecretEndpoint endpoint)
{
    const string sql = """
        IF OBJECT_ID(N'bench.OrderLine', N'U') IS NOT NULL
        BEGIN
            DECLARE @drop nvarchar(max) = N'';
            SELECT @drop = @drop + N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id))
                + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';'
            FROM sys.foreign_keys
            WHERE OBJECT_SCHEMA_NAME(parent_object_id) = N'bench';
            EXEC(@drop);
            TRUNCATE TABLE bench.OrderLine;
            TRUNCATE TABLE bench.OrderHeader;
            TRUNCATE TABLE bench.Customer;
            TRUNCATE TABLE bench.AuditHeap;
            IF OBJECT_ID(N'FK_bench_OrderHeader_Customer', N'F') IS NULL
                ALTER TABLE bench.OrderHeader ADD CONSTRAINT FK_bench_OrderHeader_Customer FOREIGN KEY (CustomerId) REFERENCES bench.Customer(CustomerId);
            IF OBJECT_ID(N'FK_bench_OrderLine_OrderHeader', N'F') IS NULL
                ALTER TABLE bench.OrderLine ADD CONSTRAINT FK_bench_OrderLine_OrderHeader FOREIGN KEY (OrderId) REFERENCES bench.OrderHeader(OrderId);
        END
        """;
    await ExecAsync(endpoint, sql);
}

static async Task SeedAsync(SecretEndpoint source, int customers, int orders, int lines)
{
    const string sql = """
        DECLARE @minCustomer int;
        INSERT INTO bench.Customer (Code, Name, Email, Balance, IsActive, CreatedAt, UpdatedAt, Notes)
        SELECT CONCAT(N'C', n.n), CONCAT(N'Customer ', n.n),
               CASE WHEN n.n % 10 = 0 THEN NULL ELSE CONCAT(N'c', n.n, N'@example.test') END,
               CAST((n.n % 5000) AS decimal(18,2)) / 100,
               CASE WHEN n.n % 20 = 0 THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END,
               DATEADD(SECOND, n.n % 100000, CONVERT(datetime2(3), '2024-01-01')),
               DATEADD(SECOND, n.n % 100000, CONVERT(datetime2(3), '2024-01-01')),
               CASE WHEN n.n % 15 = 0 THEN NULL ELSE CONCAT(N'note ', n.n) END
        FROM bench.Nums AS n WHERE n.n <= @Customers;
        SET @minCustomer = (SELECT MIN(CustomerId) FROM bench.Customer);
        INSERT INTO bench.OrderHeader (CustomerId, OrderNumber, OrderDate, Total, Status, UpdatedAt, Payload)
        SELECT @minCustomer + (n.n % @Customers),
               RIGHT(CONCAT('000000000000', n.n), 12),
               DATEADD(MINUTE, n.n % 50000, CONVERT(datetime2(3), '2024-01-01')),
               CAST((n.n % 10000) AS money) / 10,
               n.n % 5,
               DATEADD(SECOND, n.n % 100000, CONVERT(datetime2(3), '2024-01-01')),
               CONVERT(varbinary(256), CONCAT('payload-', n.n))
        FROM bench.Nums AS n WHERE n.n <= @Orders;
        DECLARE @minOrder int = (SELECT MIN(OrderId) FROM bench.OrderHeader);
        INSERT INTO bench.OrderLine (OrderId, Sku, Qty, UnitPrice, UpdatedAt)
        SELECT @minOrder + (n.n % @Orders), CONCAT('SKU', n.n % 1000), 1 + (n.n % 9),
               CAST((n.n % 2500) AS decimal(19,4)) / 10,
               DATEADD(SECOND, n.n % 100000, CONVERT(datetime2(3), '2024-01-01'))
        FROM bench.Nums AS n WHERE n.n <= @Lines;
        INSERT INTO bench.AuditHeap (EventTime, Message)
        SELECT TOP (200) DATEADD(SECOND, n.n, CONVERT(datetime2(3), '2024-01-01')), CONCAT(N'heap ', n.n)
        FROM bench.Nums AS n;
        """;
    await using var connection = Open(source);
    await connection.OpenAsync();
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
    command.Parameters.AddWithValue("@Customers", customers);
    command.Parameters.AddWithValue("@Orders", orders);
    command.Parameters.AddWithValue("@Lines", lines);
    await command.ExecuteNonQueryAsync();
}

static async Task ApplyDeltaAsync(SecretEndpoint source, int percent)
{
    const string sql = """
        DECLARE @c int = (SELECT COUNT(*) FROM bench.Customer);
        DECLARE @o int = (SELECT COUNT(*) FROM bench.OrderHeader);
        DECLARE @l int = (SELECT COUNT(*) FROM bench.OrderLine);
        DECLARE @cu int = CASE WHEN @c * @Percent / 100 < 1 THEN 1 ELSE @c * @Percent / 100 END;
        DECLARE @ou int = CASE WHEN @o * @Percent / 100 < 1 THEN 1 ELSE @o * @Percent / 100 END;
        DECLARE @lu int = CASE WHEN @l * @Percent / 100 < 1 THEN 1 ELSE @l * @Percent / 100 END;
        DELETE FROM bench.OrderLine WHERE OrderLineId IN (SELECT TOP (@lu) OrderLineId FROM bench.OrderLine ORDER BY OrderLineId DESC);
        UPDATE bench.Customer SET Name = CONCAT(Name, N'*'), Balance = Balance + 1, UpdatedAt = DATEADD(HOUR, 2, UpdatedAt)
        WHERE CustomerId IN (SELECT TOP (@cu) CustomerId FROM bench.Customer ORDER BY CustomerId);
        UPDATE bench.OrderHeader SET Total = Total + 1, Status = (Status + 1) % 5, UpdatedAt = DATEADD(HOUR, 2, UpdatedAt)
        WHERE OrderId IN (SELECT TOP (@ou) OrderId FROM bench.OrderHeader ORDER BY OrderId);
        UPDATE bench.OrderLine SET Qty = Qty + 1, UpdatedAt = DATEADD(HOUR, 2, UpdatedAt)
        WHERE OrderLineId IN (SELECT TOP (@lu) OrderLineId FROM bench.OrderLine ORDER BY OrderLineId);
        INSERT INTO bench.Customer (Code, Name, Email, Balance, IsActive, CreatedAt, UpdatedAt, Notes)
        SELECT CONCAT(N'N', n.n), CONCAT(N'New ', n.n), CONCAT(N'new', n.n, N'@example.test'), 1.5, 1,
               CONVERT(datetime2(3), '2024-06-01'), CONVERT(datetime2(3), '2024-06-01'), N'inserted'
        FROM bench.Nums AS n WHERE n.n <= @cu;
        DECLARE @maxCustomer int = (SELECT MAX(CustomerId) FROM bench.Customer);
        INSERT INTO bench.OrderHeader (CustomerId, OrderNumber, OrderDate, Total, Status, UpdatedAt, Payload)
        SELECT @maxCustomer, RIGHT(CONCAT('NEW000000000', n.n), 12), CONVERT(datetime2(3), '2024-06-01'), 9.99, 1,
               CONVERT(datetime2(3), '2024-06-01'), 0x01
        FROM bench.Nums AS n WHERE n.n <= @ou;
        DECLARE @maxOrder int = (SELECT MAX(OrderId) FROM bench.OrderHeader);
        INSERT INTO bench.OrderLine (OrderId, Sku, Qty, UnitPrice, UpdatedAt)
        SELECT @maxOrder, CONCAT('NEW', n.n), 2, 3.5, CONVERT(datetime2(3), '2024-06-01')
        FROM bench.Nums AS n WHERE n.n <= @lu;
        """;
    await using var connection = Open(source);
    await connection.OpenAsync();
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
    command.Parameters.AddWithValue("@Percent", percent);
    await command.ExecuteNonQueryAsync();
}

static async Task<int> ApplyConflictsAsync(SecretEndpoint source, SecretEndpoint targetEndpoint, int customers, int lines)
{
    var lineDeletes = Math.Max(1, lines / 8);
    const string pick = """
        IF OBJECT_ID('tempdb..#customers') IS NOT NULL DROP TABLE #customers;
        IF OBJECT_ID('tempdb..#lines') IS NOT NULL DROP TABLE #lines;
        SELECT TOP (@CustomerConflicts) CustomerId INTO #customers FROM bench.Customer ORDER BY CustomerId;
        SELECT TOP (@LineConflicts) OrderLineId,
               CASE WHEN ROW_NUMBER() OVER (ORDER BY OrderLineId) <= @LineDeletes THEN 1 ELSE 0 END AS IsDelete
        INTO #lines FROM bench.OrderLine ORDER BY OrderLineId;
        SELECT CustomerId FROM #customers;
        SELECT OrderLineId, IsDelete FROM #lines;
        """;
    var customerIds = new List<int>();
    var lineIds = new List<(long Id, bool Delete)>();
    await using (var connection = Open(source))
    {
        await connection.OpenAsync();
        await using var command = new SqlCommand(pick, connection) { CommandTimeout = 0 };
        command.Parameters.AddWithValue("@CustomerConflicts", customers);
        command.Parameters.AddWithValue("@LineConflicts", lines);
        command.Parameters.AddWithValue("@LineDeletes", lineDeletes);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) customerIds.Add(reader.GetInt32(0));
        await reader.NextResultAsync();
        while (await reader.ReadAsync()) lineIds.Add((reader.GetInt64(0), reader.GetInt32(1) == 1));
    }

    await UpdateConflictsAsync(targetEndpoint, customerIds, lineIds, targetSide: true);
    await UpdateConflictsAsync(source, customerIds, lineIds, targetSide: false);
    return customers + lines;
}

static async Task UpdateConflictsAsync(SecretEndpoint endpoint, List<int> customers, List<(long Id, bool Delete)> lines, bool targetSide)
{
    await using var connection = Open(endpoint);
    await connection.OpenAsync();
    if (customers.Count > 0)
    {
        var ids = string.Join(",", customers);
        var sql = targetSide
            ? $"UPDATE bench.Customer SET Name = CONCAT(Name, N' (target)'), UpdatedAt = CONVERT(datetime2(3), '2026-02-01') WHERE CustomerId IN ({ids})"
            : $"UPDATE bench.Customer SET Name = CONCAT(Name, N' (main)'), UpdatedAt = CONVERT(datetime2(3), '2024-07-01') WHERE CustomerId IN ({ids})";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync();
    }

    var deleteIds = lines.Where(line => line.Delete).Select(line => line.Id.ToString(CultureInfo.InvariantCulture)).ToList();
    var updateIds = lines.Where(line => !line.Delete).Select(line => line.Id.ToString(CultureInfo.InvariantCulture)).ToList();
    if (targetSide)
    {
        await ExecuteInChunksAsync(connection, updateIds.Concat(deleteIds), ids =>
            $"UPDATE bench.OrderLine SET Qty = Qty + 3, UpdatedAt = CONVERT(datetime2(3), '2026-01-01') WHERE OrderLineId IN ({ids})");
    }
    else
    {
        await ExecuteInChunksAsync(connection, updateIds, ids =>
            $"UPDATE bench.OrderLine SET Qty = Qty + 7, UpdatedAt = CONVERT(datetime2(3), '2024-06-01') WHERE OrderLineId IN ({ids})");
        await ExecuteInChunksAsync(connection, deleteIds, ids =>
            $"DELETE FROM bench.OrderLine WHERE OrderLineId IN ({ids})");
    }
}

static async Task ExecuteInChunksAsync(SqlConnection connection, IEnumerable<string> ids, Func<string, string> sql)
{
    var list = ids.ToList();
    for (var offset = 0; offset < list.Count; offset += 400)
    {
        var chunk = string.Join(",", list.Skip(offset).Take(400));
        await using var command = new SqlCommand(sql(chunk), connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync();
    }
}

static async Task<RowWidths> RowWidthsAsync(SecretEndpoint source)
{
    static async Task<int> Width(SecretEndpoint endpoint, string sql)
    {
        await using var connection = Open(endpoint);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 64 : Convert.ToInt32(value);
    }

    return new RowWidths(
        await Width(source, "SELECT AVG(ISNULL(DATALENGTH(Code),0)+ISNULL(DATALENGTH(Name),0)+ISNULL(DATALENGTH(Email),0)+ISNULL(DATALENGTH(Notes),0)+24) FROM bench.Customer"),
        await Width(source, "SELECT AVG(ISNULL(DATALENGTH(OrderNumber),0)+ISNULL(DATALENGTH(Payload),0)+32) FROM bench.OrderHeader"),
        await Width(source, "SELECT AVG(ISNULL(DATALENGTH(Sku),0)+24) FROM bench.OrderLine"));
}

static async Task<SqlCounters> SqlCountersAsync(SecretEndpoint endpoint)
{
    const string sql = """
        SELECT
            ISNULL((SELECT SUM(CAST(cntr_value AS bigint)) FROM sys.dm_os_performance_counters WHERE counter_name = 'Bytes Sent/sec' AND instance_name <> '_Total'), 0),
            ISNULL((SELECT SUM(CAST(cntr_value AS bigint)) FROM sys.dm_os_performance_counters WHERE counter_name = 'Bytes Received/sec' AND instance_name <> '_Total'), 0),
            ISNULL((SELECT SUM(CAST(total_worker_time AS bigint)) FROM sys.dm_exec_query_stats), 0);
        """;
    await using var connection = Open(endpoint);
    await connection.OpenAsync();
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    return new SqlCounters(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
}

static async Task<MatchResult> SignaturesMatchAsync(SecretEndpoint source, SecretEndpoint targetEndpoint)
{
    var sourceSig = await SignatureAsync(source);
    var targetSig = await SignatureAsync(targetEndpoint);
    var details = new List<string>();
    foreach (var key in sourceSig.Keys)
    {
        if (!targetSig.TryGetValue(key, out var other) || other != sourceSig[key])
        {
            details.Add($"{key} source={sourceSig[key]} target={other ?? "missing"}");
        }
    }

    var heap = await ScalarLongAsync(targetEndpoint, "SELECT COUNT_BIG(*) FROM bench.AuditHeap");
    if (heap != 0)
    {
        details.Add($"bench.AuditHeap target rows={heap}");
    }

    return new MatchResult(details.Count == 0, details.Count == 0 ? "match" : string.Join("; ", details), heap);
}

static async Task<Dictionary<string, string>> SignatureAsync(SecretEndpoint endpoint)
{
    const string sql = """
        SELECT 'Customer', CONCAT(COUNT_BIG(*), '|', CHECKSUM_AGG(BINARY_CHECKSUM(CustomerId, Code, Name, Email, Balance, IsActive, CreatedAt, UpdatedAt, CONVERT(nvarchar(200), Notes))), '|', SUM(Balance))
        FROM bench.Customer
        UNION ALL
        SELECT 'OrderHeader', CONCAT(COUNT_BIG(*), '|', CHECKSUM_AGG(BINARY_CHECKSUM(OrderId, CustomerId, OrderNumber, OrderDate, Total, Status, UpdatedAt, Payload)), '|', SUM(CAST(Total AS decimal(38,4))))
        FROM bench.OrderHeader
        UNION ALL
        SELECT 'OrderLine', CONCAT(COUNT_BIG(*), '|', CHECKSUM_AGG(BINARY_CHECKSUM(OrderLineId, OrderId, Sku, Qty, UnitPrice, UpdatedAt)), '|', SUM(CAST(Qty AS bigint)))
        FROM bench.OrderLine;
        """;
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    await using var connection = Open(endpoint);
    await connection.OpenAsync();
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        map[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
    }

    return map;
}

static async Task DropBenchAsync(SecretEndpoint endpoint)
{
    const string sql = """
        IF OBJECT_ID(N'bench.OrderLine', N'U') IS NOT NULL DROP TABLE bench.OrderLine;
        IF OBJECT_ID(N'bench.OrderHeader', N'U') IS NOT NULL DROP TABLE bench.OrderHeader;
        IF OBJECT_ID(N'bench.Customer', N'U') IS NOT NULL DROP TABLE bench.Customer;
        IF OBJECT_ID(N'bench.AuditHeap', N'U') IS NOT NULL DROP TABLE bench.AuditHeap;
        IF OBJECT_ID(N'bench.Nums', N'U') IS NOT NULL DROP TABLE bench.Nums;
        IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'bench') DROP SCHEMA bench;
        """;
    await ExecAsync(endpoint, sql);
}

static async Task ExecAsync(SecretEndpoint endpoint, string sql)
{
    await using var connection = Open(endpoint);
    await connection.OpenAsync();
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
    await command.ExecuteNonQueryAsync();
}

static async Task<long> ScalarLongAsync(SecretEndpoint endpoint, string sql)
{
    await using var connection = Open(endpoint);
    await connection.OpenAsync();
    await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
    var value = await command.ExecuteScalarAsync();
    return value is null or DBNull ? 0 : Convert.ToInt64(value);
}

static SqlConnection Open(SecretEndpoint endpoint) => new(endpoint.ConnectionString());

static (int LogLines, int JsonFiles, int JsonlFiles) CountConflicts(string directory)
{
    if (!Directory.Exists(directory)) return (0, 0, 0);
    var log = Path.Combine(directory, "conflict-log.jsonl");
    var lines = File.Exists(log) ? File.ReadLines(log).Count(line => line.Contains("\"operation\"", StringComparison.Ordinal)) : 0;
    var json = Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories).Length;
    var jsonl = Directory.GetFiles(directory, "*.jsonl", SearchOption.AllDirectories).Count(file => !file.EndsWith("conflict-log.jsonl", StringComparison.Ordinal));
    return (lines, json, jsonl);
}

static async Task<Probe> ProbeAsync(SecretEndpoint endpoint)
{
    try
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await tcp.ConnectAsync(endpoint.Host, 1433, cts.Token);
    }
    catch (Exception ex)
    {
        return new Probe(endpoint, false, $"TCP 1433 failed: {ex.GetType().Name}");
    }

    try
    {
        await using var connection = Open(endpoint);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT DB_NAME()", connection);
        var db = (string?)await command.ExecuteScalarAsync();
        return new Probe(endpoint, db == endpoint.Database, $"SQL login ok, database={db}");
    }
    catch (Exception ex)
    {
        var message = Redact(ex.Message, null);
        if (message.Length > 180) message = message[..180];
        return new Probe(endpoint, false, message);
    }
}

static string Redact(string? text, SecretConfig? secret)
{
    if (string.IsNullOrEmpty(text)) return "";
    if (secret is not null)
    {
        foreach (var endpoint in secret.Endpoints)
        {
            if (!string.IsNullOrEmpty(endpoint.Password))
            {
                text = text.Replace(endpoint.Password, "***", StringComparison.Ordinal);
            }
        }
    }

    return text.Replace('\r', ' ').Replace('\n', ' ');
}

static async Task WriteReportAsync(string resultsDir, List<RunRecord> runs, bool substitute, bool cleaned)
{
    var report = new StringBuilder();
    report.AppendLine("# Sync benchmark on the test servers");
    report.AppendLine();
    report.AppendLine("No local SQL Server and no latency proxy. Bytes labeled payload are the engine estimate (new) or rows read times the measured average row width (old). SQL network counters are instance-wide and can include other traffic, so they are secondary.");
    report.AppendLine();
    report.AppendLine(substitute
        ? "Main Cloud `172.20.228.2` did not accept TCP 1433 from this VM (timed out). Cloud Server (SQL Server 2022 Express) was the stand-in main and Cloud Server 2 (SQL Server 2025 Standard Developer) was the only target. Both use database `RND_PMS_Sync`."
        : "Main Cloud was reachable and was the source. Targets were the configured Cloud Server databases.");
    report.AppendLine();
    report.AppendLine(cleaned
        ? "Only schema `bench` was created (`bench.Customer`, `bench.OrderHeader`, `bench.OrderLine`, `bench.AuditHeap`, `bench.Nums`). It was dropped on both servers after the run."
        : "Cleanup did not finish. Schema `bench` may still exist and should be dropped.");
    report.AppendLine();
    report.AppendLine("`bench.AuditHeap` has no primary key and must stay empty on the target. Conflicts are 20 newer customers plus up to 1,500 newer order lines, including deletes.");
    report.AppendLine();
    report.AppendLine("The shape suggested by [BTK_ExcelSearch](https://github.com/Rayder-23/BTK_ExcelSearch) is to bulk-copy a `(primary key, SHA-256)` list into a staging table and classify it with one join, because moving full rows is the expensive part. On these servers that hash was the expensive part instead: building SHA-256 for every narrow row took longer than the old full copy, and the hash list was about the same size as the rows. Equal ranges now stop after one grouped checksum on each server. A mismatched range is read once per server as primary key plus an 8-byte checksum pair, classified in memory, and only those keys are bulk-fetched and merged. Apply batches of about 4,000 rows only avoid lock escalation. Tables without a single integer key still use the staged SHA-256 join. Interval cycles can skip unchanged source rows with an `UpdatedAt` or `rowversion` watermark, with a full compare every sixth cycle so deletes are not lost. This benchmark's cycles are manual, so they do not use the watermark. One million rows were not scheduled; that size is included below only if a run named `1m` finished.");
    report.AppendLine();
    report.AppendLine("## Before the staged-hash change (10k, 3 repeats)");
    report.AppendLine();
    report.AppendLine("The previous engine compared rows with many round trips and then applied differences in small batches. Medians from that run, kept as the baseline:");
    report.AppendLine();
    report.AppendLine("| Scenario | Old time | New time | Old payload | New payload |");
    report.AppendLine("| --- | ---: | ---: | ---: | ---: |");
    report.AppendLine("| initial | 20.3 s | 17.0 s | 0.48 MB | 1.20 MB |");
    report.AppendLine("| steady | 19.7 s | 38.6 s | 0.48 MB | 0.48 MB |");
    report.AppendLine("| delta-1pct | 19.8 s | 56.7 s | 0.48 MB | 0.51 MB |");
    report.AppendLine("| delta-10pct | 20.5 s | 88.4 s | 0.49 MB | 0.74 MB |");
    report.AppendLine("| conflicts | 19.9 s | (run stopped) | 0.47 MB | (run stopped) |");
    report.AppendLine();
    report.AppendLine("## After, 10k, 3 repeats");
    report.AppendLine();
    report.AppendLine("Medians. Payload for the new engine is its own estimate (hash bytes plus rows actually sent). The old payload is rows read times the measured average row width, so the initial-load figures are not the same ruler.");
    report.AppendLine();
    report.AppendLine("| Scenario | Old time | New time | Old payload | New payload | Match | Conflicts logged |");
    report.AppendLine("| --- | ---: | ---: | ---: | ---: | --- | ---: |");
    report.AppendLine("| initial | 19.9 s | 22.6 s | 0.48 MB | 1.20 MB | yes | 0 |");
    report.AppendLine("| steady | 19.9 s | 7.7 s | 0.48 MB | 288 B | yes | 0 |");
    report.AppendLine("| delta-1pct | 19.7 s | 22.2 s | 0.48 MB | 138 KB | yes | 0 |");
    report.AppendLine("| delta-10pct | 20.0 s | 24.1 s | 0.49 MB | 388 KB | yes | 0 |");
    report.AppendLine("| conflicts | 19.9 s | 20.2 s | 0.47 MB | 141 KB | yes | 1020 of 1020 |");
    report.AppendLine();
    report.AppendLine("No-op is faster and ships almost nothing. A 1% or 10% change ships fewer bytes than the full copy and still loses a few seconds, because each of the three tables still pays several round trips at about 170 ms. Conflicts are flagged (20 newer customers and 1,000 newer lines, including deletes) and main still wins. One million rows were not run.");
    report.AppendLine();
    report.AppendLine("## This run");
    report.AppendLine();
    if (runs.Count == 0)
    {
        report.AppendLine("No scenario finished.");
    }
    else
    {
        report.AppendLine("| Size | Scenario | Engine | Median time | Spread | Payload bytes | Rows read | Rows written | Source SQL CPU | Match |");
        report.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (var size in runs.Select(run => run.Size).Distinct())
        {
            foreach (var scenario in runs.Where(run => run.Size == size).Select(run => run.Scenario).Distinct())
            {
                foreach (var engine in new[] { "old", "new" })
                {
                    var group = runs.Where(run => run.Size == size && run.Scenario == scenario && run.Engine == engine).ToList();
                    if (group.Count == 0) continue;
                    var time = Median(group.Select(run => (double)run.EngineElapsedMs).ToList());
                    var spread = group.Max(run => run.EngineElapsedMs) - group.Min(run => run.EngineElapsedMs);
                    var bytes = Median(group.Select(run => (double)run.PayloadBytes).ToList());
                    var read = Median(group.Select(run => (double)run.RowsRead).ToList());
                    var written = Median(group.Select(run => (double)run.RowsWritten).ToList());
                    var cpu = Median(group.Select(run => (double)run.SourceWorkerMs).ToList());
                    var match = group.All(run => run.DataMatch) ? "yes" : "NO";
                    report.AppendLine($"| {size} | {scenario} | {engine} | {time:0} ms | {spread} ms | {FormatBytes(bytes)} | {read:0} | {written:0} | {cpu:0} ms | {match} |");
                }
            }
        }

        report.AppendLine();
        report.AppendLine("## Conflicts");
        report.AppendLine();
        report.AppendLine("| Size | Engine | Median log lines | JSON files | JSONL files | Expected |");
        report.AppendLine("| --- | --- | ---: | ---: | ---: | ---: |");
        foreach (var group in runs.Where(run => run.Scenario == "conflicts").GroupBy(run => (run.Size, run.Engine)))
        {
            var list = group.ToList();
            report.AppendLine($"| {group.Key.Size} | {group.Key.Engine} | {Median(list.Select(run => (double)run.ConflictLogLines).ToList()):0} | {Median(list.Select(run => (double)run.ConflictJsonFiles).ToList()):0} | {Median(list.Select(run => (double)run.ConflictJsonlFiles).ToList()):0} | {list[0].ExpectedConflicts} |");
        }

        report.AppendLine();
        report.AppendLine("## Conclusion");
        report.AppendLine();
        var failed = runs.Where(run => !run.Success).ToList();
        if (failed.Count > 0)
        {
            report.AppendLine($"{failed.Count} run(s) failed. First: {failed[0].Size} {failed[0].Scenario} {failed[0].Engine}: {failed[0].Error} {failed[0].MatchDetail}");
            report.AppendLine();
        }

        foreach (var scenario in runs.Select(run => run.Scenario).Distinct())
        {
            var oldMs = runs.Where(run => run.Scenario == scenario && run.Engine == "old").Select(run => (double)run.EngineElapsedMs).ToList();
            var newMs = runs.Where(run => run.Scenario == scenario && run.Engine == "new").Select(run => (double)run.EngineElapsedMs).ToList();
            var oldBytes = runs.Where(run => run.Scenario == scenario && run.Engine == "old").Select(run => (double)run.PayloadBytes).ToList();
            var newBytes = runs.Where(run => run.Scenario == scenario && run.Engine == "new").Select(run => (double)run.PayloadBytes).ToList();
            if (oldMs.Count == 0 || newMs.Count == 0) continue;
            report.AppendLine($"- {scenario}: old median {Median(oldMs):0} ms / {FormatBytes(Median(oldBytes))}, new median {Median(newMs):0} ms / {FormatBytes(Median(newBytes))}.");
        }
    }

    var path = Path.Combine(resultsDir, "REPORT.md");
    await File.WriteAllTextAsync(path, report.ToString());
    var jsonPath = Path.Combine(resultsDir, "runs.json");
    await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(runs, new JsonSerializerOptions { WriteIndented = true }));
}

static double Median(List<double> values)
{
    values.Sort();
    var mid = values.Count / 2;
    return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
}

static string FormatBytes(double bytes)
{
    if (bytes < 1024) return $"{bytes:0} B";
    if (bytes < 1024 * 1024) return $"{bytes / 1024:0.0} KB";
    if (bytes < 1024d * 1024 * 1024) return $"{bytes / 1024 / 1024:0.0} MB";
    return $"{bytes / 1024 / 1024 / 1024:0.00} GB";
}

static string Trim(string text) => text.Length <= 400 ? text : text[^400..];

static long ReadCpuTicks(int pid)
{
    try
    {
        var text = File.ReadAllText($"/proc/{pid}/stat");
        var close = text.LastIndexOf(')');
        var fields = text[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return long.Parse(fields[11], CultureInfo.InvariantCulture) + long.Parse(fields[12], CultureInfo.InvariantCulture);
    }
    catch { return 0; }
}

static long ReadRssKb(int pid)
{
    try
    {
        foreach (var line in File.ReadLines($"/proc/{pid}/status"))
        {
            if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
            {
                return long.Parse(new string(line.Where(char.IsDigit).ToArray()), CultureInfo.InvariantCulture);
            }
        }
    }
    catch { return 0; }
    return 0;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "DatabaseSync.sln"))) return dir.FullName;
        dir = dir.Parent;
    }

    return Directory.GetCurrentDirectory();
}

sealed class SecretConfig
{
    public List<SecretEndpoint> Endpoints { get; } = [];

    public static SecretConfig Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var config = new SecretConfig();
        foreach (var item in json.RootElement.GetProperty("Connections").EnumerateArray())
        {
            if (item.TryGetProperty("IsEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) continue;
            config.Endpoints.Add(new SecretEndpoint(
                item.GetProperty("Id").GetString() ?? "",
                item.GetProperty("Name").GetString() ?? "",
                item.GetProperty("IsMain").GetBoolean(),
                item.GetProperty("Server").GetString() ?? "",
                item.GetProperty("Database").GetString() ?? "",
                item.GetProperty("UserId").GetString() ?? "",
                item.GetProperty("Password").GetString() ?? ""));
        }

        return config;
    }

    public void WriteRunFile(string path, SecretEndpoint source, SecretEndpoint targetEndpoint, string conflictDir)
    {
        var json = JsonSerializer.Serialize(new
        {
            SyncSettings = new Dictionary<string, object?>
            {
                ["TriggerConfigVersion"] = 1,
                ["EnableSynchronization"] = true,
                ["SyncMainToOthers"] = true,
                ["DryRun"] = false,
                ["IntervalSyncEnabled"] = false,
                ["ScheduledSyncEnabled"] = false,
                ["CommandTimeoutSeconds"] = 0,
                ["EnableDetailedLogging"] = false,
                ["ConflictFileRowThreshold"] = 1000,
                ["ConflictFileMaxBytes"] = 5_000_000,
                ["UseConflictBackupTables"] = false,
                ["WriteConflictLogTable"] = false,
                ["AssumeDifferingRowsConflict"] = false,
                ["ConflictBackupFolder"] = conflictDir,
                ["ChangeTimestampColumnNames"] = new[] { "UpdatedAt" },
                ["FingerprintBatchSize"] = 10000,
                ["DiffApplyBatchSize"] = 4000
            },
            Connections = new[]
            {
                source.ToJson(isMain: true),
                targetEndpoint.ToJson(isMain: false)
            }
        });
        File.WriteAllText(path, json);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

sealed record SecretEndpoint(string Id, string Name, bool IsMain, string Host, string Database, string User, string Password)
{
    public string ConnectionString() => new SqlConnectionStringBuilder
    {
        DataSource = Host,
        InitialCatalog = Database,
        UserID = User,
        Password = Password,
        Encrypt = true,
        TrustServerCertificate = true,
        ConnectTimeout = 30,
        ApplicationName = "Coditium.DatabaseSync"
    }.ConnectionString;

    public object ToJson(bool isMain) => new
    {
        Id,
        Name,
        IsMain = isMain,
        IsEnabled = true,
        Server = Host,
        Database,
        IntegratedSecurity = false,
        UserId = User,
        Password,
        ConnectionTimeoutSeconds = 30,
        Encrypt = true,
        TrustServerCertificate = true,
        ApplicationName = "Coditium.DatabaseSync"
    };
}

sealed record BenchOptions(HashSet<string> Sizes, HashSet<string> Scenarios, int Repeats, bool StopOnFailure)
{
    public static BenchOptions Parse(string[] args)
    {
        var sizes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "10k", "100k", "1m" };
        var scenarios = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "initial", "steady", "delta-1pct", "delta-10pct", "conflicts" };
        var repeats = 3;
        var stop = true;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--sizes" && i + 1 < args.Length) sizes = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            else if (args[i] == "--scenarios" && i + 1 < args.Length) scenarios = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            else if (args[i] == "--repeats" && i + 1 < args.Length) repeats = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (args[i] == "--keep-going") stop = false;
        }

        return new BenchOptions(sizes, scenarios, repeats, stop);
    }
}

sealed record RunRecord
{
    public string Size { get; init; } = "";
    public string Scenario { get; init; } = "";
    public string Engine { get; init; } = "";
    public int Repeat { get; init; }
    public bool Success { get; init; }
    public long EngineElapsedMs { get; init; }
    public long ProcessElapsedMs { get; init; }
    public double PeakRssMb { get; init; }
    public double AvgCpuPercent { get; init; }
    public long RowsRead { get; init; }
    public long RowsWritten { get; init; }
    public long RowsInserted { get; init; }
    public long RowsUpdated { get; init; }
    public long RowsDeleted { get; init; }
    public long RowsConflicted { get; init; }
    public long PayloadBytes { get; init; }
    public long SourceNetworkBytes { get; init; }
    public long TargetNetworkBytes { get; init; }
    public long SourceWorkerMs { get; init; }
    public long TargetWorkerMs { get; init; }
    public bool DataMatch { get; init; }
    public string MatchDetail { get; init; } = "";
    public int ConflictLogLines { get; init; }
    public int ConflictJsonFiles { get; init; }
    public int ConflictJsonlFiles { get; init; }
    public int ExpectedConflicts { get; init; }
    public long AuditHeapTargetRows { get; init; }
    public string? Error { get; init; }
}

sealed record MeasuredRun(bool Success, long EngineElapsedMs, long ProcessElapsedMs, double PeakRssMb, double AvgCpuPercent, long RowsRead, long RowsWritten, long RowsInserted, long RowsUpdated, long RowsDeleted, long RowsConflicted, long EstimatedBytes, string? Error);
sealed record RowWidths(int Customer, int Order, int Line);
sealed record SqlCounters(long BytesSent, long BytesReceived, long WorkerUs);
sealed record MatchResult(bool Match, string Detail, long AuditHeapTarget);
sealed record Probe(SecretEndpoint Endpoint, bool Ok, string Detail)
{
    public override string ToString() => $"{(Endpoint.IsMain ? "MAIN" : "TARGET")} {Endpoint.Name} {Endpoint.Host} db={Endpoint.Database} {(Ok ? "OK" : "FAIL")} {Detail}";
}
