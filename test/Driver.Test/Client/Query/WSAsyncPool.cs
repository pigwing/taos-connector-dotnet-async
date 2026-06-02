using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using TDengine.Driver;
using TDengine.Driver.Client;
using TDengine.Driver.Client.Websocket;
using Test.Fixture;
using Xunit;

namespace Driver.Test.Client.Query
{
    [Collection("WebSocket async collection")]
    public class WSAsyncPool
    {
        private readonly string _wsConnectString;

        public WSAsyncPool()
        {
            _wsConnectString = TestConnectionOptions.WebSocketConnectionString();
        }

        [Fact]
        public async Task PoolReusesReturnedConnectionTest()
        {
            var created = 0;
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token => Task.FromResult<ITDengineClientAsync>(new FakeClientAsync(Interlocked.Increment(ref created)))))
            {
                using (var client = await pool.AcquireAsync())
                {
                    Assert.True(client.ConnectionAvailable());
                }

                using (var client = await pool.AcquireAsync())
                {
                    Assert.True(client.ConnectionAvailable());
                }

                var metrics = pool.GetMetrics();
                Assert.Equal(1, created);
                Assert.Equal(2, metrics.AcquireCount);
                Assert.Equal(0, metrics.ActiveConnections);
                Assert.Equal(1, metrics.IdleConnections);
                Assert.Equal(1, metrics.TotalConnections);
            }
        }

        [Fact]
        public async Task PoolAcquireTimeoutWhenExhaustedTest()
        {
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       ConnectionTimeout = TimeSpan.FromMilliseconds(80),
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token => Task.FromResult<ITDengineClientAsync>(new FakeClientAsync(1))))
            using (await pool.AcquireAsync())
            {
                await Assert.ThrowsAsync<TimeoutException>(() => pool.AcquireAsync());
                Assert.Equal(1, pool.GetMetrics().AcquireTimeoutCount);
            }
        }

        [Fact]
        public async Task PoolWaiterReceivesReturnedConnectionTest()
        {
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       ConnectionTimeout = TimeSpan.FromSeconds(5),
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token => Task.FromResult<ITDengineClientAsync>(new FakeClientAsync(1))))
            {
                var first = await pool.AcquireAsync();
                var waiting = pool.AcquireAsync();
                await Task.Delay(100);

                Assert.False(waiting.IsCompleted);
                first.Dispose();

                using (await waiting)
                {
                    var metrics = pool.GetMetrics();
                    Assert.Equal(1, metrics.ActiveConnections);
                    Assert.Equal(0, metrics.IdleConnections);
                }
            }
        }

        [Fact]
        public async Task PoolKeepsLeaseActiveUntilRowsDisposedTest()
        {
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       ConnectionTimeout = TimeSpan.FromMilliseconds(120),
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token => Task.FromResult<ITDengineClientAsync>(new FakeClientAsync(1))))
            {
                IRowsAsync rows;
                using (var client = await pool.AcquireAsync())
                {
                    rows = await client.QueryAsync("select 1");
                }

                Assert.Equal(1, pool.GetMetrics().ActiveConnections);
                await Assert.ThrowsAsync<TimeoutException>(() => pool.AcquireAsync());

                rows.Dispose();
                using (await pool.AcquireAsync())
                {
                    Assert.Equal(1, pool.GetMetrics().ActiveConnections);
                }
            }
        }

        [Fact]
        public async Task PoolKeepsLeaseActiveUntilStmtDisposedTest()
        {
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       ConnectionTimeout = TimeSpan.FromMilliseconds(120),
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token => Task.FromResult<ITDengineClientAsync>(new FakeClientAsync(1))))
            {
                IStmtAsync stmt;
                using (var client = await pool.AcquireAsync())
                {
                    stmt = await client.StmtInitAsync();
                }

                Assert.Equal(1, pool.GetMetrics().ActiveConnections);
                await Assert.ThrowsAsync<TimeoutException>(() => pool.AcquireAsync());

                stmt.Dispose();
                using (await pool.AcquireAsync())
                {
                    Assert.Equal(1, pool.GetMetrics().ActiveConnections);
                }
            }
        }

        [Fact]
        public async Task PoolReplacesExpiredIdleConnectionTest()
        {
            var created = 0;
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       MaxLifetime = TimeSpan.FromMilliseconds(120),
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token => Task.FromResult<ITDengineClientAsync>(new FakeClientAsync(Interlocked.Increment(ref created)))))
            {
                using (await pool.AcquireAsync())
                {
                }

                await Task.Delay(180);
                using (await pool.AcquireAsync())
                {
                }

                var metrics = pool.GetMetrics();
                Assert.Equal(2, created);
                Assert.Equal(1, metrics.RecycledConnectionCount);
                Assert.Equal(1, metrics.IdleConnections);
            }
        }

        [Fact]
        public async Task PoolLeakDetectionReportsCheckoutStackTest()
        {
            var leakTcs = new TaskCompletionSource<WSClientAsyncPoolLeakEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       LeakDetectionThreshold = TimeSpan.FromMilliseconds(80),
                       HousekeepingInterval = TimeSpan.FromMilliseconds(40),
                       LeakDetected = args => leakTcs.TrySetResult(args)
                   }, token => Task.FromResult<ITDengineClientAsync>(new FakeClientAsync(1))))
            {
                using (await pool.AcquireAsync())
                {
                    var completed = await Task.WhenAny(leakTcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
                    Assert.Same(leakTcs.Task, completed);
                    var args = await leakTcs.Task;
                    Assert.True(args.Elapsed >= TimeSpan.FromMilliseconds(80));
                    Assert.False(string.IsNullOrWhiteSpace(args.StackTrace));
                }
            }
        }

        [Fact]
        public async Task PoolConcurrentBorrowReturnStaysWithinMaximumSizeTest()
        {
            var created = 0;
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 4,
                       ConnectionTimeout = TimeSpan.FromSeconds(5),
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token => Task.FromResult<ITDengineClientAsync>(new FakeClientAsync(Interlocked.Increment(ref created)))))
            {
                var tasks = new Task[64];
                for (var i = 0; i < tasks.Length; i++)
                {
                    tasks[i] = Task.Run(async () =>
                    {
                        using (var client = await pool.AcquireAsync())
                        {
                            await client.ExecAsync("insert");
                        }
                    });
                }

                await Task.WhenAll(tasks);

                var metrics = pool.GetMetrics();
                Assert.True(created <= 4, $"Created {created} clients.");
                Assert.Equal(64, metrics.AcquireCount);
                Assert.Equal(0, metrics.ActiveConnections);
                Assert.True(metrics.IdleConnections <= 4);
                Assert.True(metrics.TotalConnections <= 4);
            }
        }

        [Fact]
        public async Task PoolThreadLocalFastPathDoesNotRetainQueueDuplicatesTest()
        {
            var created = 0;
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token => Task.FromResult<ITDengineClientAsync>(new FakeClientAsync(Interlocked.Increment(ref created)))))
            {
                for (var i = 0; i < 200; i++)
                {
                    using (await pool.AcquireAsync())
                    {
                    }
                }

                var metrics = pool.GetMetrics();
                Assert.Equal(1, created);
                Assert.Equal(0, metrics.ActiveConnections);
                Assert.Equal(1, metrics.IdleConnections);
                Assert.Equal(1, metrics.TotalConnections);
                Assert.Equal(200, metrics.AcquireCount);
            }
        }

        [Fact]
        public async Task PoolDisposeClosesThreadLocalIdleConnectionTest()
        {
            FakeClientAsync createdClient = null!;
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token =>
                   {
                       createdClient = new FakeClientAsync(1);
                       return Task.FromResult<ITDengineClientAsync>(createdClient);
                   }))
            {
                using (await pool.AcquireAsync())
                {
                }

                Assert.True(createdClient.ConnectionAvailable());
                pool.Dispose();
                Assert.False(createdClient.ConnectionAvailable());
            }
        }

        [Fact]
        public async Task PoolWarmupMaintainsMinimumIdleTest()
        {
            var created = 0;
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MinIdle = 2,
                       MaximumPoolSize = 4,
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token => Task.FromResult<ITDengineClientAsync>(new FakeClientAsync(Interlocked.Increment(ref created)))))
            {
                await pool.WarmupAsync();

                var metrics = pool.GetMetrics();
                Assert.Equal(2, metrics.IdleConnections);
                Assert.Equal(2, metrics.TotalConnections);
                Assert.Equal(2, created);
            }
        }

        [Fact]
        public async Task PoolAcquireTimeoutPreservesLastCreationExceptionTest()
        {
            var failure = new InvalidOperationException("connect failed");
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       ConnectionTimeout = TimeSpan.FromMilliseconds(120),
                       CreationRetryBackoff = TimeSpan.FromMilliseconds(20),
                       MaxCreationRetryBackoff = TimeSpan.FromMilliseconds(20),
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token => Task.FromException<ITDengineClientAsync>(failure)))
            {
                var ex = await Assert.ThrowsAsync<TimeoutException>(() => pool.AcquireAsync());

                Assert.Same(failure, ex.InnerException);
                Assert.True(pool.GetMetrics().CreationFailureCount > 0);
            }
        }

        [Fact]
        public async Task PoolAcquireFailFastWhenCreationBackoffDisabledTest()
        {
            var failure = new InvalidOperationException("connect failed fast");
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       CreationRetryBackoff = TimeSpan.Zero,
                       MaxCreationRetryBackoff = TimeSpan.Zero,
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token => Task.FromException<ITDengineClientAsync>(failure)))
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.AcquireAsync());

                Assert.Same(failure, ex);
                var metrics = pool.GetMetrics();
                Assert.Equal(1, metrics.CreationFailureCount);
                Assert.Equal(0, metrics.TotalConnections);
            }
        }

        [Fact]
        public async Task PoolDisposeClosesActiveLeaseTest()
        {
            FakeClientAsync createdClient = null!;
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token =>
                   {
                       createdClient = new FakeClientAsync(1);
                       return Task.FromResult<ITDengineClientAsync>(createdClient);
                   }))
            {
                var client = await pool.AcquireAsync();
                Assert.True(createdClient.ConnectionAvailable());

                pool.Dispose();

                Assert.False(createdClient.ConnectionAvailable());
                Assert.False(client.ConnectionAvailable());
                var metrics = pool.GetMetrics();
                Assert.Equal(0, metrics.ActiveConnections);
                Assert.Equal(0, metrics.TotalConnections);
            }
        }

        [Fact]
        public async Task PoolRowsReadPinsLeaseUntilReadCompletesTest()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       ConnectionTimeout = TimeSpan.FromMilliseconds(120),
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token => Task.FromResult<ITDengineClientAsync>(
                       new FakeClientAsync(1, () => new FakeRowsAsync(gate.Task)))))
            {
                IRowsAsync rows;
                using (var client = await pool.AcquireAsync())
                {
                    rows = await client.QueryAsync("select 1");
                }

                var readTask = rows.ReadAsync();
                await Task.Delay(20);
                rows.Dispose();

                await Assert.ThrowsAsync<TimeoutException>(() => pool.AcquireAsync());
                gate.SetResult(true);
                await Assert.ThrowsAsync<ObjectDisposedException>(async () => await readTask);

                using (await pool.AcquireAsync())
                {
                    Assert.Equal(1, pool.GetMetrics().ActiveConnections);
                }
            }
        }

        [Fact]
        public async Task PoolStmtExecPinsLeaseUntilExecCompletesTest()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       ConnectionTimeout = TimeSpan.FromMilliseconds(120),
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token => Task.FromResult<ITDengineClientAsync>(
                       new FakeClientAsync(1, stmtFactory: () => new FakeStmtAsync(gate.Task)))))
            {
                IStmtAsync stmt;
                using (var client = await pool.AcquireAsync())
                {
                    stmt = await client.StmtInitAsync();
                }

                var execTask = stmt.ExecAsync();
                await Task.Delay(20);
                stmt.Dispose();

                await Assert.ThrowsAsync<TimeoutException>(() => pool.AcquireAsync());
                gate.SetResult(true);
                await Assert.ThrowsAsync<ObjectDisposedException>(async () => await execTask);

                using (await pool.AcquireAsync())
                {
                    Assert.Equal(1, pool.GetMetrics().ActiveConnections);
                }
            }
        }

        [Fact]
        public async Task PoolStmtGetFieldsPinsLeaseUntilCallCompletesTest()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       ConnectionTimeout = TimeSpan.FromMilliseconds(120),
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token => Task.FromResult<ITDengineClientAsync>(
                       new FakeClientAsync(1, stmtFactory: () => new FakeStmtAsync(fieldsGate: gate.Task)))))
            {
                IStmtAsync stmt;
                using (var client = await pool.AcquireAsync())
                {
                    stmt = await client.StmtInitAsync();
                }

                var fieldsTask = stmt.GetTagFieldsAsync();
                await Task.Delay(20);
                stmt.Dispose();

                await Assert.ThrowsAsync<TimeoutException>(() => pool.AcquireAsync());
                gate.SetResult(true);
                await Assert.ThrowsAsync<ObjectDisposedException>(async () => await fieldsTask);

                using (await pool.AcquireAsync())
                {
                    Assert.Equal(1, pool.GetMetrics().ActiveConnections);
                }
            }
        }

        [Fact]
        public async Task PoolDisposeDuringRowsReadDoesNotUnderflowMetricsTest()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeClientAsync createdClient = null!;
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token =>
                   {
                       createdClient = new FakeClientAsync(1, () => new FakeRowsAsync(gate.Task));
                       return Task.FromResult<ITDengineClientAsync>(createdClient);
                   }))
            {
                IRowsAsync rows;
                using (var client = await pool.AcquireAsync())
                {
                    rows = await client.QueryAsync("select 1");
                }

                var readTask = rows.ReadAsync();
                await Task.Delay(20);

                pool.Dispose();

                Assert.False(createdClient.ConnectionAvailable());
                var metricsAfterDispose = pool.GetMetrics();
                Assert.Equal(0, metricsAfterDispose.ActiveConnections);
                Assert.Equal(0, metricsAfterDispose.TotalConnections);

                gate.SetResult(true);
                await Assert.ThrowsAsync<ObjectDisposedException>(async () => await readTask);

                var finalMetrics = pool.GetMetrics();
                Assert.Equal(0, finalMetrics.ActiveConnections);
                Assert.Equal(0, finalMetrics.TotalConnections);
            }
        }

        [Fact]
        public async Task PoolDisposeWhileAcquireIsCreatingClosesCreatedConnectionTest()
        {
            var factoryStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowFactoryToReturn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeClientAsync createdClient = null!;
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, async token =>
                   {
                       factoryStarted.SetResult(true);
                       await allowFactoryToReturn.Task.ConfigureAwait(false);
                       createdClient = new FakeClientAsync(1);
                       return createdClient;
                   }))
            {
                var acquireTask = pool.AcquireAsync();
                await factoryStarted.Task;

                pool.Dispose();
                allowFactoryToReturn.SetResult(true);

                await Assert.ThrowsAsync<ObjectDisposedException>(async () => await acquireTask);
                Assert.NotNull(createdClient);
                Assert.False(createdClient.ConnectionAvailable());

                var metrics = pool.GetMetrics();
                Assert.Equal(0, metrics.ActiveConnections);
                Assert.Equal(0, metrics.TotalConnections);
            }
        }

        [Fact]
        public async Task PoolQueryNullRowsDoesNotLeakLeaseTest()
        {
            using (var pool = CreateFakePool(new WSClientAsyncPoolOptions
                   {
                       MaximumPoolSize = 1,
                       ConnectionTimeout = TimeSpan.FromMilliseconds(120),
                       HousekeepingInterval = TimeSpan.FromMinutes(5)
                   }, token => Task.FromResult<ITDengineClientAsync>(
                       new FakeClientAsync(1, () => null!))))
            {
                using (var client = await pool.AcquireAsync())
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAsync("select 1"));
                }

                var metrics = pool.GetMetrics();
                Assert.Equal(0, metrics.ActiveConnections);
                Assert.Equal(1, metrics.IdleConnections);

                using (await pool.AcquireAsync())
                {
                    Assert.Equal(1, pool.GetMetrics().ActiveConnections);
                }
            }
        }

        [Fact]
        public async Task WebSocketPoolExecQueryAndConcurrentBorrowIntegrationTest()
        {
            var db = "ws_async_pool_integration_test";
            var builder = new ConnectionStringBuilder(_wsConnectString);
            using (var setupClient = await DbDriver.OpenAsync(builder))
            {
                try
                {
                    await setupClient.ExecAsync($"drop database if exists {db}");
                    await setupClient.ExecAsync($"create database {db}");
                    await setupClient.ExecAsync($"use {db}");
                    await setupClient.ExecAsync("create table test_pool(ts timestamp, c1 int, c2 binary(32))");
                }
                finally
                {
                    setupClient.Dispose();
                }
            }

            try
            {
                var pooledBuilder = new ConnectionStringBuilder(TestConnectionOptions.WebSocketConnectionString(db));
                using (var pool = DbDriver.CreateWebSocketAsyncPool(pooledBuilder, new WSClientAsyncPoolOptions
                       {
                           MinIdle = 2,
                           MaximumPoolSize = 4,
                           ConnectionTimeout = TimeSpan.FromSeconds(10),
                           HousekeepingInterval = TimeSpan.FromSeconds(5)
                       }))
                {
                    await pool.WarmupAsync();

                    const int count = 24;
                    var baseTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var insertTasks = new Task[count];
                    for (var i = 0; i < count; i++)
                    {
                        var value = i;
                        insertTasks[i] = Task.Run(async () =>
                        {
                            using (var client = await pool.AcquireAsync())
                            {
                                await client.ExecAsync(
                                    $"insert into test_pool values({baseTimestamp + value}, {value}, 'value_{value}')");
                            }
                        });
                    }

                    await Task.WhenAll(insertTasks);

                    using (var client = await pool.AcquireAsync())
                    using (var rows = await client.QueryAsync("select count(*) from test_pool"))
                    {
                        Assert.True(await rows.ReadAsync());
                        Assert.Equal(count, rows.GetInt64(0));
                    }

                    var queryTasks = new Task[count];
                    for (var i = 0; i < count; i++)
                    {
                        var value = i;
                        queryTasks[i] = Task.Run(async () =>
                        {
                            using (var client = await pool.AcquireAsync())
                            using (var rows = await client.QueryAsync(
                                       $"select c1, c2 from test_pool where c1 = {value}"))
                            {
                                Assert.True(await rows.ReadAsync());
                                Assert.Equal(value, rows.GetInt32(0));
                                Assert.Equal($"value_{value}", rows.GetString(1));
                                Assert.False(await rows.ReadAsync());
                            }
                        });
                    }

                    await Task.WhenAll(queryTasks);

                    var metrics = pool.GetMetrics();
                    Assert.Equal(0, metrics.ActiveConnections);
                    Assert.True(metrics.TotalConnections <= 4);
                    Assert.True(metrics.AcquireCount >= count * 2 + 1);
                }
            }
            finally
            {
                using (var cleanupClient = await DbDriver.OpenAsync(builder))
                {
                    if (cleanupClient.ConnectionAvailable())
                    {
                        await cleanupClient.ExecAsync($"drop database if exists {db}");
                    }
                }
            }
        }

        [Fact]
        public async Task WebSocketConnectionStringPoolingReusesSharedPoolIntegrationTest()
        {
            DbDriver.ClearWebSocketAsyncPools();
            var db = "ws_async_pool_connstr_test";
            var setupBuilder = new ConnectionStringBuilder(_wsConnectString);
            using (var setupClient = await DbDriver.OpenAsync(setupBuilder))
            {
                await setupClient.ExecAsync($"drop database if exists {db}");
                await setupClient.ExecAsync($"create database {db}");
                await setupClient.ExecAsync($"use {db}");
                await setupClient.ExecAsync("create table test_connstr_pool(ts timestamp, c1 int, c2 binary(32))");
                await setupClient.ExecAsync("insert into test_connstr_pool values(now, 1, 'first')");
            }

            var pooledBuilder = new ConnectionStringBuilder(
                TestConnectionOptions.WebSocketConnectionString(db) +
                ";pooling=true;minPoolSize=0;maxPoolSize=2;connectionTimeout=5s;housekeepingInterval=5s");

            try
            {
                using (var client = await DbDriver.OpenAsync(pooledBuilder))
                {
                    await client.ExecAsync("insert into test_connstr_pool values(now + 1s, 2, 'second')");
                }

                using (var client = await DbDriver.OpenAsync(pooledBuilder))
                using (var rows = await client.QueryAsync("select count(*) from test_connstr_pool"))
                {
                    Assert.True(await rows.ReadAsync());
                    Assert.Equal(2, rows.GetInt64(0));
                }

                var metrics = DbDriver.GetWebSocketAsyncPoolMetrics(pooledBuilder);
                Assert.NotNull(metrics);
                Assert.True(metrics!.AcquireCount >= 2);
                Assert.True(metrics.TotalConnections <= 2);
                Assert.Equal(0, metrics.ActiveConnections);
                Assert.True(metrics.IdleConnections >= 1);
            }
            finally
            {
                DbDriver.ClearWebSocketAsyncPools();
                using (var cleanupClient = await DbDriver.OpenAsync(setupBuilder))
                {
                    if (cleanupClient.ConnectionAvailable())
                    {
                        await cleanupClient.ExecAsync($"drop database if exists {db}");
                    }
                }
            }
        }

        [Fact]
        public async Task WebSocketConnectionStringPoolingTimeoutWhenPoolExhaustedIntegrationTest()
        {
            DbDriver.ClearWebSocketAsyncPools();
            var builder = new ConnectionStringBuilder(
                _wsConnectString +
                ";pooling=true;minPoolSize=0;maxPoolSize=1;connectionTimeout=2s;housekeepingInterval=5s");

            try
            {
                using (await DbDriver.OpenAsync(builder))
                {
                    await Assert.ThrowsAsync<TimeoutException>(() => DbDriver.OpenAsync(builder));
                }

                var metrics = DbDriver.GetWebSocketAsyncPoolMetrics(builder);
                Assert.NotNull(metrics);
                Assert.Equal(1, metrics!.AcquireTimeoutCount);
            }
            finally
            {
                DbDriver.ClearWebSocketAsyncPools();
            }
        }

        private static WSClientAsyncPool CreateFakePool(WSClientAsyncPoolOptions options,
            Func<CancellationToken, Task<ITDengineClientAsync>> factory)
        {
            return new WSClientAsyncPool(new ConnectionStringBuilder("protocol=WebSocket;host=localhost;port=6041"),
                options, factory);
        }

        private sealed class FakeClientAsync : ITDengineClientAsync
        {
            private readonly ConcurrentQueue<FakeRowsAsync> _rows = new ConcurrentQueue<FakeRowsAsync>();
            private readonly Func<FakeRowsAsync> _rowsFactory;
            private readonly Func<FakeStmtAsync> _stmtFactory;
            private int _disposed;

            public FakeClientAsync(int id, Func<FakeRowsAsync>? rowsFactory = null,
                Func<FakeStmtAsync>? stmtFactory = null)
            {
                Id = id;
                _rowsFactory = rowsFactory ?? (() => new FakeRowsAsync());
                _stmtFactory = stmtFactory ?? (() => new FakeStmtAsync());
            }

            public int Id { get; }

            public WebSocketState State => ConnectionAvailable() ? WebSocketState.Open : WebSocketState.Closed;

            public bool ConnectionAvailable()
            {
                return Volatile.Read(ref _disposed) == 0;
            }

            public Task<IStmtAsync> StmtInitAsync()
            {
                return Task.FromResult<IStmtAsync>(_stmtFactory());
            }

            public Task<IStmtAsync> StmtInitAsync(long reqId)
            {
                return StmtInitAsync();
            }

            public Task<IStmtAsync> StmtInitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return StmtInitAsync();
            }

            public Task<IStmtAsync> StmtInitAsync(long reqId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return StmtInitAsync();
            }

            public Task<IRowsAsync> QueryAsync(string query)
            {
                return QueryAsync(query, ReqId.GetReqId(), CancellationToken.None);
            }

            public Task<IRowsAsync> QueryAsync(string query, long reqId)
            {
                return QueryAsync(query, reqId, CancellationToken.None);
            }

            public Task<IRowsAsync> QueryAsync(string query, CancellationToken cancellationToken)
            {
                return QueryAsync(query, ReqId.GetReqId(), cancellationToken);
            }

            public Task<IRowsAsync> QueryAsync(string query, long reqId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                var rows = _rowsFactory();
                _rows.Enqueue(rows);
                return Task.FromResult<IRowsAsync>(rows);
            }

            public Task<long> ExecAsync(string query)
            {
                return ExecAsync(query, ReqId.GetReqId(), CancellationToken.None);
            }

            public Task<long> ExecAsync(string query, long reqId)
            {
                return ExecAsync(query, reqId, CancellationToken.None);
            }

            public Task<long> ExecAsync(string query, CancellationToken cancellationToken)
            {
                return ExecAsync(query, ReqId.GetReqId(), cancellationToken);
            }

            public Task<long> ExecAsync(string query, long reqId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                return Task.FromResult(1L);
            }

            public Task SchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol,
                TDengineSchemalessPrecision precision, int ttl, long reqId)
            {
                return SchemalessInsertAsync(lines, protocol, precision, ttl, reqId, CancellationToken.None);
            }

            public Task SchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol,
                TDengineSchemalessPrecision precision, int ttl, long reqId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _disposed, 1);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }

            private void ThrowIfDisposed()
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    throw new ObjectDisposedException(nameof(FakeClientAsync));
                }
            }
        }

        private sealed class FakeRowsAsync : IRowsAsync
        {
            private readonly Task _readGate;
            private int _disposed;
            private int _read;

            public FakeRowsAsync(Task? readGate = null)
            {
                _readGate = readGate ?? Task.CompletedTask;
            }

            public bool HasRows => true;

            public int AffectRows => -1;

            public int FieldCount => 1;

            public long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length)
            {
                return 0;
            }

            public char GetChar(int ordinal)
            {
                return '\0';
            }

            public long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length)
            {
                return 0;
            }

            public string GetDataTypeName(int ordinal)
            {
                return "INT";
            }

            public object GetValue(int ordinal)
            {
                return 1;
            }

            public Type GetFieldType(int ordinal)
            {
                return typeof(int);
            }

            public int GetFieldSize(int ordinal)
            {
                return 4;
            }

            public string GetName(int ordinal)
            {
                return "c1";
            }

            public int GetFieldPrecision(int ordinal)
            {
                return 0;
            }

            public int GetFieldScale(int ordinal)
            {
                return 0;
            }

            public int GetOrdinal(string name)
            {
                return 0;
            }

            public Task<bool> ReadAsync()
            {
                return ReadAsync(CancellationToken.None);
            }

            public async Task<bool> ReadAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                await _readGate.ConfigureAwait(false);
                ThrowIfDisposed();
                return Interlocked.Increment(ref _read) == 1;
            }

            public bool IsDBNull(int ordinal)
            {
                return false;
            }

            public byte GetByte(int ordinal)
            {
                return 1;
            }

            public short GetInt16(int ordinal)
            {
                return 1;
            }

            public int GetInt32(int ordinal)
            {
                return 1;
            }

            public long GetInt64(int ordinal)
            {
                return 1;
            }

            public bool GetBoolean(int ordinal)
            {
                return true;
            }

            public DateTime GetDateTime(int ordinal)
            {
                return DateTime.UtcNow;
            }

            public decimal GetDecimal(int ordinal)
            {
                return 1;
            }

            public double GetDouble(int ordinal)
            {
                return 1;
            }

            public float GetFloat(int ordinal)
            {
                return 1;
            }

            public string GetString(int ordinal)
            {
                return "1";
            }

            public int GetValues(object[] values)
            {
                values[0] = 1;
                return 1;
            }

            public DateTimeOffset GetDateTimeOffset(int ordinal)
            {
                return DateTimeOffset.UtcNow;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _disposed, 1);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }

            private void ThrowIfDisposed()
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    throw new ObjectDisposedException(nameof(FakeRowsAsync));
                }
            }
        }

        private sealed class FakeStmtAsync : IStmtAsync
        {
            private readonly Task _execGate;
            private readonly Task _fieldsGate;
            private int _disposed;

            public FakeStmtAsync(Task? execGate = null, Task? fieldsGate = null)
            {
                _execGate = execGate ?? Task.CompletedTask;
                _fieldsGate = fieldsGate ?? Task.CompletedTask;
            }

            public Task PrepareAsync(string query)
            {
                ThrowIfDisposed();
                return Task.CompletedTask;
            }

            public Task PrepareAsync(string query, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return PrepareAsync(query);
            }

            public bool IsInsert()
            {
                ThrowIfDisposed();
                return false;
            }

            public Task SetTableNameAsync(string tableName)
            {
                ThrowIfDisposed();
                return Task.CompletedTask;
            }

            public Task SetTableNameAsync(string tableName, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return SetTableNameAsync(tableName);
            }

            public Task SetTagsAsync(object[] tags)
            {
                ThrowIfDisposed();
                return Task.CompletedTask;
            }

            public Task SetTagsAsync(object[] tags, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return SetTagsAsync(tags);
            }

            public async Task<TaosFieldE[]> GetTagFieldsAsync()
            {
                ThrowIfDisposed();
                await _fieldsGate.ConfigureAwait(false);
                ThrowIfDisposed();
                return new TaosFieldE[0];
            }

            public async Task<TaosFieldE[]> GetColFieldsAsync()
            {
                ThrowIfDisposed();
                await _fieldsGate.ConfigureAwait(false);
                ThrowIfDisposed();
                return new TaosFieldE[0];
            }

            public Task BindRowAsync(object[] row)
            {
                ThrowIfDisposed();
                return Task.CompletedTask;
            }

            public Task BindRowAsync(object[] row, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return BindRowAsync(row);
            }

            public Task BindColumnAsync(TaosFieldE[] fields, params Array[] arrays)
            {
                ThrowIfDisposed();
                return Task.CompletedTask;
            }

            public Task BindColumnAsync(TaosFieldE[] fields, CancellationToken cancellationToken,
                params Array[] arrays)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return BindColumnAsync(fields, arrays);
            }

            public Task AddBatchAsync()
            {
                ThrowIfDisposed();
                return Task.CompletedTask;
            }

            public Task AddBatchAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return AddBatchAsync();
            }

            public async Task ExecAsync()
            {
                ThrowIfDisposed();
                await _execGate.ConfigureAwait(false);
                ThrowIfDisposed();
            }

            public Task ExecAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ExecAsync();
            }

            public long Affected()
            {
                ThrowIfDisposed();
                return 1;
            }

            public Task<IRowsAsync> ResultAsync()
            {
                return ResultAsync(CancellationToken.None);
            }

            public Task<IRowsAsync> ResultAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                return Task.FromResult<IRowsAsync>(new FakeRowsAsync());
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _disposed, 1);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }

            private void ThrowIfDisposed()
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    throw new ObjectDisposedException(nameof(FakeStmtAsync));
                }
            }
        }
    }
}
