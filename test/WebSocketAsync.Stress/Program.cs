using System.Diagnostics;
using TDengine.Driver;
using TDengine.Driver.Client;
using TDengine.Driver.Client.Websocket;

var host = Environment.GetEnvironmentVariable("TEST_HOST") ?? "172.17.0.5";
var port = int.TryParse(Environment.GetEnvironmentVariable("TEST_WS_PORT"), out var configuredPort)
    ? configuredPort : 6341;
var user = Environment.GetEnvironmentVariable("TEST_USER") ?? "root";
var password = Environment.GetEnvironmentVariable("TEST_PASSWORD") ?? "taosdata";
var sharedWorkers = ReadPositiveInt("STRESS_SHARED_WORKERS", 16);
var poolWorkers = ReadPositiveInt("STRESS_POOL_WORKERS", 32);
var operationsPerWorker = ReadPositiveInt("STRESS_OPERATIONS_PER_WORKER", 200);
var database = "codex_ws_async_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
var baseConnectionString = $"protocol=WebSocket;host={host};port={port};useSSL=false;" +
                           $"username={user};password={password};enableCompression=true;" +
                           "connTimeout=00:00:10;readTimeout=00:00:30;writeTimeout=00:00:10";
var setupBuilder = new ConnectionStringBuilder(baseConnectionString);
var dataBuilder = new ConnectionStringBuilder(baseConnectionString + $";db={database}");
var poolBuilder = new ConnectionStringBuilder(baseConnectionString + $";db={database}")
{
    Pooling = true,
    MinPoolSize = 2,
    MaxPoolSize = 8,
    PoolConnectionTimeout = TimeSpan.FromSeconds(30),
    PoolHousekeepingInterval = TimeSpan.FromSeconds(5),
    PoolMaxLifetime = TimeSpan.FromSeconds(30),
    PoolKeepaliveTime = TimeSpan.FromSeconds(15)
};
var sharedCount = checked(sharedWorkers * operationsPerWorker);
var poolCount = checked(poolWorkers * operationsPerWorker);
var expectedRows = checked(sharedCount + poolCount);
var firstTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var created = false;

Console.WriteLine($"target={host}:{port} database={database} sharedWorkers={sharedWorkers} " +
                  $"poolWorkers={poolWorkers} operationsPerWorker={operationsPerWorker}");

try
{
    await using (var setup = await DbDriver.OpenAsync(setupBuilder))
    {
        await setup.ExecAsync($"create database {database} precision 'ms'");
        created = true;
    }

    await using (var setup = await DbDriver.OpenAsync(dataBuilder))
    {
        await setup.ExecAsync("create table readings(ts timestamp, worker int, seq int, payload binary(64))");
    }

    await using (var shared = await DbDriver.OpenAsync(dataBuilder))
    {
        var sharedMetrics = await RunPhaseAsync("shared-connection", sharedWorkers,
            operationsPerWorker, async (worker, sequence, index, timings) =>
            {
                var timestamp = firstTimestamp + index;
                var begin = Stopwatch.GetTimestamp();
                var affected = await shared.ExecAsync(
                    $"insert into readings values({timestamp}, {worker}, {sequence}, 'value_{index}')");
                timings.Insert.Add(ElapsedMilliseconds(begin));
                if (affected != 1) throw new InvalidDataException("Shared insert affected an unexpected row count.");
                if ((sequence & 7) == 0)
                    await VerifyRowAsync(shared, timestamp, worker, sequence, index, timings);
            });
        PrintPhase(sharedMetrics);
        PrintMemory("after-shared");
    }

    var poolMetrics = await RunPhaseAsync("connection-string-pool", poolWorkers,
        operationsPerWorker, async (worker, sequence, index, timings) =>
        {
            var begin = Stopwatch.GetTimestamp();
            await using var lease = await DbDriver.OpenAsync(poolBuilder);
            timings.Acquire.Add(ElapsedMilliseconds(begin));
            var timestamp = firstTimestamp + sharedCount + index;
            begin = Stopwatch.GetTimestamp();
            var affected = await lease.ExecAsync(
                $"insert into readings values({timestamp}, {worker}, {sequence}, 'value_{sharedCount + index}')");
            timings.Insert.Add(ElapsedMilliseconds(begin));
            if (affected != 1) throw new InvalidDataException("Pooled insert affected an unexpected row count.");
            if ((sequence & 7) == 0)
                await VerifyRowAsync(lease, timestamp, worker, sequence, sharedCount + index, timings);
        });
    PrintPhase(poolMetrics);
    PrintPoolMetrics(poolBuilder);
    PrintMemory("after-pool");

    await using (var verifier = await DbDriver.OpenAsync(dataBuilder))
    {
        await using var countRows = await verifier.QueryAsync("select count(*) from readings");
        if (!await countRows.ReadAsync() || countRows.GetInt64(0) != expectedRows)
            throw new InvalidDataException("Final row count does not match acknowledged inserts.");
    }
    Console.WriteLine($"truth-check rows={expectedRows} exact=true");

    await using (var verifier = await DbDriver.OpenAsync(dataBuilder))
    {
        var begin = Stopwatch.GetTimestamp();
        var rowsRead = 0;
        await using (var rows = await verifier.QueryAsync("select worker, seq, payload from readings order by ts"))
        {
            while (await rows.ReadAsync())
            {
                _ = rows.GetInt32(0);
                _ = rows.GetInt32(1);
                if (string.IsNullOrEmpty(rows.GetString(2)))
                    throw new InvalidDataException("Large result contained an empty payload.");
                rowsRead++;
            }
        }

        if (rowsRead != expectedRows)
            throw new InvalidDataException("Large fetch omitted or duplicated rows.");
        Console.WriteLine($"large-fetch rows={rowsRead} elapsedMs={ElapsedMilliseconds(begin):F1}");
    }

    var canceled = 0;
    var completed = 0;
    for (var i = 0; i < 50; i++)
    {
        await using var verifier = await DbDriver.OpenAsync(poolBuilder);
        using var cancellation = new CancellationTokenSource();
        var query = verifier.QueryAsync("select worker, seq from readings order by ts",
            cancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(1));
        try
        {
            await using var rows = await query;
            await rows.ReadAsync(cancellation.Token);
            completed++;
        }
        catch (OperationCanceledException)
        {
            canceled++;
        }
        catch (TDengineWebSocketRequestException error) when (error.RequestMayHaveBeenSent &&
                                                           error.InnerException is OperationCanceledException)
        {
            canceled++;
        }
    }
    await using (var verifier = await DbDriver.OpenAsync(poolBuilder))
    await using (var rows = await verifier.QueryAsync("select count(*) from readings"))
    {
        if (!await rows.ReadAsync() || rows.GetInt64(0) != expectedRows)
            throw new InvalidDataException("Cancellation phase changed test data.");
    }
    Console.WriteLine($"cancellation attempts=50 canceled={canceled} completed={completed} probe=ok");

    var exhaustionBuilder = new ConnectionStringBuilder(baseConnectionString + $";db={database}")
    {
        Pooling = true,
        MinPoolSize = 0,
        MaxPoolSize = poolBuilder.MaxPoolSize,
        PoolConnectionTimeout = TimeSpan.FromSeconds(3),
        PoolHousekeepingInterval = TimeSpan.FromSeconds(5)
    };
    var held = new List<ITDengineClientAsync>();
    try
    {
        for (var i = 0; i < exhaustionBuilder.MaxPoolSize; i++)
            held.Add(await DbDriver.OpenAsync(exhaustionBuilder));
        var begin = Stopwatch.GetTimestamp();
        try
        {
            await using var unexpected = await DbDriver.OpenAsync(exhaustionBuilder);
            throw new InvalidDataException("Pool exhaustion did not time out.");
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"pool-exhaustion timeoutMs={ElapsedMilliseconds(begin):F1}");
        }
    }
    finally
    {
        foreach (var lease in held)
            await lease.DisposeAsync();
    }

    PrintPoolMetrics(poolBuilder);
    PrintPoolMetrics(exhaustionBuilder);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    PrintMemory("final-collected");
    Console.WriteLine("result=PASS");
}
finally
{
    DbDriver.ClearWebSocketAsyncPools();
    if (created)
    {
        await using var cleanup = await DbDriver.OpenAsync(setupBuilder);
        await cleanup.ExecAsync($"drop database if exists {database}");
        Console.WriteLine($"cleanup database={database} dropped=true");
    }
}

static async Task VerifyRowAsync(ITDengineClientAsync client, long timestamp, int worker, int sequence,
    int index, WorkerTimings timings)
{
    var begin = Stopwatch.GetTimestamp();
    await using var rows = await client.QueryAsync(
        $"select worker, seq, payload from readings where ts = {timestamp}");
    if (!await rows.ReadAsync() || rows.GetInt32(0) != worker || rows.GetInt32(1) != sequence ||
        rows.GetString(2) != $"value_{index}" || await rows.ReadAsync())
        throw new InvalidDataException("Read-after-write returned incorrect data.");
    timings.Query.Add(ElapsedMilliseconds(begin));
}

static async Task<PhaseMetrics> RunPhaseAsync(string name, int workers, int operationsPerWorker,
    Func<int, int, int, WorkerTimings, Task> operation)
{
    var timings = Enumerable.Range(0, workers).Select(_ => new WorkerTimings()).ToArray();
    var stopwatch = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, workers).Select(async worker =>
    {
        for (var sequence = 0; sequence < operationsPerWorker; sequence++)
            await operation(worker, sequence, worker * operationsPerWorker + sequence,
                timings[worker]);
    }));
    stopwatch.Stop();
    return new PhaseMetrics(name, workers * operationsPerWorker, stopwatch.Elapsed,
        timings.SelectMany(x => x.Acquire).ToArray(), timings.SelectMany(x => x.Insert).ToArray(),
        timings.SelectMany(x => x.Query).ToArray());
}

static void PrintPhase(PhaseMetrics phase)
{
    Console.WriteLine($"phase={phase.Name} inserts={phase.Inserts} elapsedSec={phase.Elapsed.TotalSeconds:F2} " +
                      $"insertPerSec={phase.Inserts / phase.Elapsed.TotalSeconds:F1}");
    PrintLatency("acquire", phase.Acquire);
    PrintLatency("insert", phase.Insert);
    PrintLatency("query+fetch+dispose", phase.Query);
}

static void PrintLatency(string name, double[] values)
{
    if (values.Length == 0) return;
    Array.Sort(values);
    Console.WriteLine($"latency={name} count={values.Length} p50Ms={At(0.50):F2} " +
                      $"p95Ms={At(0.95):F2} p99Ms={At(0.99):F2} maxMs={values[^1]:F2}");
    double At(double percentile) => values[(int)Math.Ceiling((values.Length - 1) * percentile)];
}

static void PrintPoolMetrics(ConnectionStringBuilder builder)
{
    var metrics = DbDriver.GetWebSocketAsyncPoolMetrics(builder)
                  ?? throw new InvalidDataException("Pool metrics are unavailable.");
    Console.WriteLine($"pool total={metrics.TotalConnections} active={metrics.ActiveConnections} " +
                      $"idle={metrics.IdleConnections} awaiting={metrics.ThreadsAwaitingConnection} " +
                      $"acquires={metrics.AcquireCount} acquireTimeouts={metrics.AcquireTimeoutCount} " +
                      $"created={metrics.CreationCount} creationFailures={metrics.CreationFailureCount} " +
                      $"recycled={metrics.RecycledConnectionCount} keepaliveFailures={metrics.KeepaliveFailureCount} " +
                      $"avgAcquireMs={metrics.AverageAcquireDuration.TotalMilliseconds:F2} " +
                      $"maxAcquireMs={metrics.MaxAcquireDuration.TotalMilliseconds:F2}");
    if (metrics.TotalConnections > builder.MaxPoolSize || metrics.ActiveConnections != 0 ||
        metrics.CreationFailureCount != 0 || metrics.KeepaliveFailureCount != 0)
        throw new InvalidDataException("Pool metrics violate the stress-test invariants.");
}

static void PrintMemory(string phase)
{
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    var managedBytes = Math.Max(0L, GC.GetTotalMemory(false));
    var gcInfo = GC.GetGCMemoryInfo();
    Console.WriteLine($"memory phase={phase} managedMiB={managedBytes / 1048576.0:F1} " +
                      $"heapSizeMiB={gcInfo.HeapSizeBytes / 1048576.0:F1} " +
                      $"allocatedMiB={GC.GetTotalAllocatedBytes(false) / 1048576.0:F1} " +
                      $"privateMiB={process.PrivateMemorySize64 / 1048576.0:F1} " +
                      $"workingSetMiB={process.WorkingSet64 / 1048576.0:F1} " +
                      $"gen2Collections={GC.CollectionCount(2)}");
}

static int ReadPositiveInt(string name, int fallback)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrEmpty(value)) return fallback;
    if (!int.TryParse(value, out var parsed) || parsed <= 0 || parsed > 1000)
        throw new ArgumentOutOfRangeException(name);
    return parsed;
}

static double ElapsedMilliseconds(long start) =>
    (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

sealed class WorkerTimings
{
    internal List<double> Acquire { get; } = new();
    internal List<double> Insert { get; } = new();
    internal List<double> Query { get; } = new();
}

sealed record PhaseMetrics(string Name, int Inserts, TimeSpan Elapsed, double[] Acquire,
    double[] Insert, double[] Query);
