using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using TDengine.Driver;
using TDengine.Driver.Client.Websocket;
using Xunit;

namespace Driver.Test.Client.Query
{
    [Collection("WebSocket async collection")]
    public sealed class PoolOffline
    {
        [Fact]
        public void PoolKeyDistinguishesInvalidSurrogateCredentials()
        {
            var first = new ConnectionStringBuilder(
                "protocol=WebSocket;host=localhost;port=6041")
            {
                Password = new string((char)0xd800, 1)
            };
            var second = new ConnectionStringBuilder(
                "protocol=WebSocket;host=localhost;port=6041")
            {
                Password = new string((char)0xd801, 1)
            };

            Assert.NotEqual(WSClientAsyncPoolRegistry.BuildPoolKey(first),
                WSClientAsyncPoolRegistry.BuildPoolKey(second));
        }

        [Fact]
        public void PoolKeyDistinguishesCustomTimeZonesWithSameId()
        {
            const string id = "pool-key-custom-zone";
            var first = new ConnectionStringBuilder(
                "protocol=WebSocket;host=localhost;port=6041")
            {
                Timezone = TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(1), id, id)
            };
            var second = new ConnectionStringBuilder(
                "protocol=WebSocket;host=localhost;port=6041")
            {
                Timezone = TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(2), id, id)
            };

            Assert.NotEqual(WSClientAsyncPoolRegistry.BuildPoolKey(first),
                WSClientAsyncPoolRegistry.BuildPoolKey(second));
        }

        [Fact]
        public void EquivalentPoolConfigurationsProduceSameKey()
        {
            var first = new ConnectionStringBuilder(
                "protocol=WebSocket;host=host-a,host-b:6042;port=6041;db=test;" +
                "username=root;password=secret;pooling=true;minPoolSize=2;maxPoolSize=10");
            var second = new ConnectionStringBuilder(
                "maxPoolSize=10;minPoolSize=2;pooling=true;password=secret;username=root;" +
                "db=test;port=6041;host=host-a,host-b:6042;protocol=WebSocket");

            Assert.Equal(WSClientAsyncPoolRegistry.BuildPoolKey(first),
                WSClientAsyncPoolRegistry.BuildPoolKey(second));
        }

        [Fact]
        public void OverlappingHaRegistrationsPreserveSeedAliasesAndRemoveStaleMembers()
        {
            AdapterClusterRegistry.Clear();
            try
            {
                var a = Address("a");
                var b = Address("b");
                var c = Address("c");
                var stale = Address("stale");
                AdapterClusterRegistry.RegisterCluster(new[] { a }, new[] { a, b, stale });
                AdapterClusterRegistry.RegisterCluster(new[] { c }, new[] { b, c });

                var expanded = AdapterClusterRegistry.ExpandIfKnown(new[] { a });
                Assert.Equal(2, expanded.Count);
                Assert.Equal(b.CacheKey, expanded[0].CacheKey);
                Assert.Equal(c.CacheKey, expanded[1].CacheKey);

                var staleSeed = new[] { stale };
                Assert.Same(staleSeed, AdapterClusterRegistry.ExpandIfKnown(staleSeed));
                Assert.Equal(3, AdapterClusterRegistry.Count);
            }
            finally
            {
                AdapterClusterRegistry.Clear();
            }
        }

        [Fact]
        public async Task UnavailableFactoryClientIsDisposedAndNotRegistered()
        {
            OfflineFakeClient? unavailable = null;
            var factoryCalls = 0;
            using var pool = CreatePool(DefaultOptions(), _ =>
            {
                factoryCalls++;
                if (factoryCalls == 1)
                {
                    unavailable = new OfflineFakeClient(available: false);
                    return Task.FromResult<ITDengineClientAsync>(unavailable);
                }

                return Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient());
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() => pool.AcquireAsync())
                .ConfigureAwait(false);

            Assert.NotNull(unavailable);
            Assert.Equal(1, unavailable!.DisposeCount);
            Assert.Equal(0, pool.GetMetrics().TotalConnections);

            using (await pool.AcquireAsync().ConfigureAwait(false))
            {
            }

            Assert.Equal(1, pool.GetMetrics().IdleConnections);
        }

        [Fact]
        public async Task NegativeRequestIdsAreRejectedBeforeBorrowedClientOperation()
        {
            using var pool = CreatePool(DefaultOptions(), _ =>
                Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient()));
            var client = await pool.AcquireAsync().ConfigureAwait(false);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.StmtInitAsync(-1))
                .ConfigureAwait(false);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.QueryAsync("select 1", -1))
                .ConfigureAwait(false);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ExecAsync("select 1", -1))
                .ConfigureAwait(false);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SchemalessInsertAsync(
                    new[] { "st,t=1i 1" }, TDengineSchemalessProtocol.TSDB_SML_LINE_PROTOCOL,
                    TDengineSchemalessPrecision.TSDB_SML_TIMESTAMP_NOT_CONFIGURED, 0, -1))
                .ConfigureAwait(false);

            client.Dispose();
        }

        [Fact]
        public async Task LazyPoolCreatedDuringRetirementIsDisposed()
        {
            var factoryEntered = NewCompletionSource<bool>();
            var releaseFactory = NewCompletionSource<bool>();
            WSClientAsyncPool? createdPool = null;
            var entry = new WSClientAsyncPoolRegistry.PoolEntry(() =>
            {
                factoryEntered.TrySetResult(true);
                releaseFactory.Task.GetAwaiter().GetResult();
                createdPool = CreatePool(new WSClientAsyncPoolOptions
                {
                    MaximumPoolSize = 1,
                    KeepaliveTime = TimeSpan.Zero,
                    MaxLifetime = TimeSpan.Zero,
                    HousekeepingInterval = TimeSpan.FromMinutes(5)
                }, _ => Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient()));
                return createdPool;
            });

            Assert.True(entry.TryEnter());
            var getPoolTask = Task.Run(() => entry.GetPool());
            await factoryEntered.Task.ConfigureAwait(false);
            Assert.True(entry.TryRetire(true));
            entry.DisposePool();
            releaseFactory.TrySetResult(true);

            await Assert.ThrowsAsync<ObjectDisposedException>(() => getPoolTask).ConfigureAwait(false);
            Assert.NotNull(createdPool);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => createdPool!.AcquireAsync())
                .ConfigureAwait(false);
            entry.Exit();
        }

        [Fact]
        public async Task DisposedWrapperReportsUnavailableWhileRowsRemainUsable()
        {
            using var pool = CreatePool(DefaultOptions(),
                _ => Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient()));
            var client = await pool.AcquireAsync().ConfigureAwait(false);
            var rows = await client.QueryAsync("select 1").ConfigureAwait(false);

            client.Dispose();
            Assert.False(client.ConnectionAvailable());
            Assert.Equal(WebSocketState.Closed, client.State);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ExecAsync("select 1"))
                .ConfigureAwait(false);

            Assert.True(await rows.ReadAsync().ConfigureAwait(false));
            Assert.Equal(1, rows.GetInt32(0));
            rows.Dispose();

            var metrics = pool.GetMetrics();
            Assert.Equal(0, metrics.ActiveConnections);
            Assert.Equal(1, metrics.IdleConnections);
        }

        [Fact]
        public async Task DelayedExecCompletionSurvivesWrapperDisposal()
        {
            var gate = NewCompletionSource<bool>();
            using var pool = CreatePool(DefaultOptions(),
                _ => Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient(execGate: gate.Task)));
            var client = await pool.AcquireAsync().ConfigureAwait(false);
            var execTask = client.ExecAsync("insert");
            client.Dispose();
            Assert.False(execTask.IsCompleted);

            gate.TrySetResult(true);
            Assert.Equal(1, await execTask.ConfigureAwait(false));
            var metrics = pool.GetMetrics();
            Assert.Equal(0, metrics.ActiveConnections);
            Assert.Equal(1, metrics.IdleConnections);
        }

        [Fact]
        public async Task DelayedQueryCompletionSurvivesWrapperDisposal()
        {
            var gate = NewCompletionSource<bool>();
            using var pool = CreatePool(DefaultOptions(),
                _ => Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient(queryGate: gate.Task)));
            var client = await pool.AcquireAsync().ConfigureAwait(false);
            var queryTask = client.QueryAsync("select delayed");
            client.Dispose();
            Assert.False(queryTask.IsCompleted);

            gate.TrySetResult(true);
            var rows = await queryTask.ConfigureAwait(false);
            Assert.True(await rows.ReadAsync().ConfigureAwait(false));
            Assert.Equal(1, pool.GetMetrics().ActiveConnections);
            rows.Dispose();
            Assert.Equal(1, pool.GetMetrics().IdleConnections);
        }

        [Fact]
        public async Task DelayedStatementCompletionSurvivesWrapperDisposal()
        {
            var gate = NewCompletionSource<bool>();
            using var pool = CreatePool(DefaultOptions(),
                _ => Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient(stmtGate: gate.Task)));
            var client = await pool.AcquireAsync().ConfigureAwait(false);
            var stmtTask = client.StmtInitAsync();
            client.Dispose();
            Assert.False(stmtTask.IsCompleted);

            gate.TrySetResult(true);
            var stmt = await stmtTask.ConfigureAwait(false);
            await stmt.PrepareAsync("select 1").ConfigureAwait(false);
            Assert.Equal(1, pool.GetMetrics().ActiveConnections);
            stmt.Dispose();
            Assert.Equal(1, pool.GetMetrics().IdleConnections);
        }

        [Fact]
        public async Task CanceledWaiterDoesNotConsumeNextConnectionWakeup()
        {
            using var pool = CreatePool(new WSClientAsyncPoolOptions
            {
                MaximumPoolSize = 1,
                ConnectionTimeout = TimeSpan.FromSeconds(3),
                KeepaliveTime = TimeSpan.Zero,
                MaxLifetime = TimeSpan.Zero,
                HousekeepingInterval = TimeSpan.FromMinutes(5),
                CreationRetryBackoff = TimeSpan.Zero,
                MaxCreationRetryBackoff = TimeSpan.Zero
            }, _ => Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient()));
            var first = await pool.AcquireAsync().ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            var canceledWaiter = pool.AcquireAsync(cancellation.Token);
            await WaitForMetricAsync(pool, metrics => metrics.ThreadsAwaitingConnection == 1)
                .ConfigureAwait(false);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter)
                .ConfigureAwait(false);

            var nextWaiter = pool.AcquireAsync();
            await WaitForMetricAsync(pool, metrics => metrics.ThreadsAwaitingConnection == 1)
                .ConfigureAwait(false);
            first.Dispose();
            var next = await AssertCompletesAsync(nextWaiter, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            next.Dispose();

            AssertNonNegativeCounters(pool.GetMetrics());
            Assert.Equal(1, pool.GetMetrics().IdleConnections);
        }

        [Fact]
        public async Task ConcurrentDoubleDisposalDoesNotUnderflowPoolCounters()
        {
            using var pool = CreatePool(new WSClientAsyncPoolOptions
            {
                MaximumPoolSize = 4,
                ConnectionTimeout = TimeSpan.FromSeconds(3),
                KeepaliveTime = TimeSpan.Zero,
                MaxLifetime = TimeSpan.Zero,
                HousekeepingInterval = TimeSpan.FromMinutes(5),
                CreationRetryBackoff = TimeSpan.Zero,
                MaxCreationRetryBackoff = TimeSpan.Zero
            }, _ => Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient()));

            var workers = new Task[64];
            for (var i = 0; i < workers.Length; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    var client = await pool.AcquireAsync().ConfigureAwait(false);
                    var rows = await client.QueryAsync("select 1").ConfigureAwait(false);
                    await Task.WhenAll(Task.Run(client.Dispose), Task.Run(client.Dispose)).ConfigureAwait(false);
                    Assert.True(await rows.ReadAsync().ConfigureAwait(false));
                    await Task.WhenAll(Task.Run(rows.Dispose), Task.Run(rows.Dispose)).ConfigureAwait(false);
                });
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
            var metrics = pool.GetMetrics();
            AssertNonNegativeCounters(metrics);
            Assert.Equal(0, metrics.ActiveConnections);
            Assert.Equal(metrics.TotalConnections, metrics.IdleConnections);
            Assert.InRange(metrics.TotalConnections, 1, 4);

            pool.Dispose();
            metrics = pool.GetMetrics();
            AssertNonNegativeCounters(metrics);
            Assert.Equal(0, metrics.TotalConnections);
        }

        [Fact]
        public async Task LifetimeJitterNeverExtendsConfiguredMaximumLifetime()
        {
            var created = 0;
            using var pool = CreatePool(new WSClientAsyncPoolOptions
            {
                MaximumPoolSize = 1,
                ConnectionTimeout = TimeSpan.FromSeconds(2),
                KeepaliveTime = TimeSpan.Zero,
                MaxLifetime = TimeSpan.FromMilliseconds(120),
                HousekeepingInterval = TimeSpan.FromMinutes(5),
                CreationRetryBackoff = TimeSpan.Zero,
                MaxCreationRetryBackoff = TimeSpan.Zero
            }, _ => Task.FromResult<ITDengineClientAsync>(
                new OfflineFakeClient(Interlocked.Increment(ref created))));

            using (await pool.AcquireAsync().ConfigureAwait(false))
            {
            }

            await Task.Delay(140).ConfigureAwait(false);
            using (await pool.AcquireAsync().ConfigureAwait(false))
            {
            }

            Assert.Equal(2, created);
            Assert.Equal(1, pool.GetMetrics().RecycledConnectionCount);
        }

        [Fact]
        public async Task RecycledIdleConnectionsDoNotAccumulateQueueEntries()
        {
            var created = 0;
            using var pool = CreatePool(new WSClientAsyncPoolOptions
            {
                MinIdle = 2,
                MaximumPoolSize = 2,
                ConnectionTimeout = TimeSpan.FromSeconds(2),
                KeepaliveTime = TimeSpan.Zero,
                MaxLifetime = TimeSpan.FromMilliseconds(2),
                HousekeepingInterval = TimeSpan.FromMinutes(5),
                CreationRetryBackoff = TimeSpan.Zero,
                MaxCreationRetryBackoff = TimeSpan.Zero
            }, _ => Task.FromResult<ITDengineClientAsync>(
                new OfflineFakeClient(Interlocked.Increment(ref created))));

            await pool.WarmupAsync().ConfigureAwait(false);
            for (var iteration = 0; iteration < 12; iteration++)
            {
                await Task.Delay(10).ConfigureAwait(false);
                await RunHousekeepingAsync(pool).ConfigureAwait(false);
            }

            var metrics = pool.GetMetrics();
            Assert.True(created >= 26);
            Assert.InRange(GetPrivateCollectionCount(pool, "_idleQueue"), 1, 4);
            Assert.Equal(0, metrics.ActiveConnections);
            Assert.Equal(2, metrics.IdleConnections);
            Assert.Equal(0, metrics.MaintenanceConnections);
            Assert.Equal(2, metrics.TotalConnections);
            AssertNonNegativeCounters(metrics);
        }

        [Fact]
        public async Task IdleQueueCompactionRemovesDuplicateConnectionEntries()
        {
            using var pool = CreatePool(new WSClientAsyncPoolOptions
            {
                MinIdle = 1,
                MaximumPoolSize = 1,
                ConnectionTimeout = TimeSpan.FromSeconds(2),
                KeepaliveTime = TimeSpan.Zero,
                MaxLifetime = TimeSpan.Zero,
                HousekeepingInterval = TimeSpan.FromMinutes(5),
                CreationRetryBackoff = TimeSpan.Zero,
                MaxCreationRetryBackoff = TimeSpan.Zero
            }, _ => Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient()));

            await pool.WarmupAsync().ConfigureAwait(false);
            var connection = GetOnlyPrivateDictionaryKey(pool, "_connections");
            var queuedField = connection.GetType().GetField("Queued",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var idleQueue = GetPrivateField<object>(pool, "_idleQueue");
            var enqueue = idleQueue.GetType().GetMethod("Enqueue");
            Assert.NotNull(queuedField);
            Assert.NotNull(enqueue);

            queuedField!.SetValue(connection, 1);
            for (var i = 0; i < 5; i++)
            {
                enqueue!.Invoke(idleQueue, new[] { connection });
            }

            await RunHousekeepingAsync(pool).ConfigureAwait(false);

            Assert.Equal(1, GetPrivateCollectionCount(pool, "_idleQueue"));
            using (await pool.AcquireAsync().ConfigureAwait(false))
            {
            }

            var metrics = pool.GetMetrics();
            Assert.Equal(1, metrics.IdleConnections);
            Assert.Equal(1, metrics.TotalConnections);
            AssertNonNegativeCounters(metrics);
        }

        [Fact]
        public async Task PoolDisposeWaitsForDelayedOperationAndDisposesClientOnce()
        {
            var operationGate = NewCompletionSource<bool>();
            OfflineFakeClient? createdClient = null;
            var pool = CreatePool(new WSClientAsyncPoolOptions
            {
                MaximumPoolSize = 1,
                ConnectionTimeout = TimeSpan.FromSeconds(2),
                KeepaliveTime = TimeSpan.Zero,
                MaxLifetime = TimeSpan.Zero,
                HousekeepingInterval = TimeSpan.FromMinutes(5),
                CreationRetryBackoff = TimeSpan.Zero,
                MaxCreationRetryBackoff = TimeSpan.Zero
            }, _ =>
            {
                createdClient = new OfflineFakeClient(execGate: operationGate.Task);
                return Task.FromResult<ITDengineClientAsync>(createdClient);
            });

            var client = await pool.AcquireAsync().ConfigureAwait(false);
            var operation = client.ExecAsync("insert delayed");
            var disposeTask = pool.DisposeAsync().AsTask();
            await Task.Delay(50).ConfigureAwait(false);
            Assert.False(disposeTask.IsCompleted);

            operationGate.TrySetResult(true);
            Assert.Equal(1, await operation.ConfigureAwait(false));
            await AssertCompletesAsync(disposeTask, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.NotNull(createdClient);
            Assert.Equal(1, createdClient!.DisposeCount);
            AssertNonNegativeCounters(pool.GetMetrics());
            Assert.Equal(0, pool.GetMetrics().TotalConnections);
        }

        [Fact]
        public async Task PooledRowsDisposeWaitsForSynchronousGetter()
        {
            var getterEntered = NewCompletionSource<bool>();
            var releaseGetter = NewCompletionSource<bool>();
            var innerRows = new OfflineFakeRows(releaseGetter.Task, getterEntered);
            using var pool = CreatePool(DefaultOptions(), _ =>
                Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient(rowsFactory: () => innerRows)));

            var client = await pool.AcquireAsync().ConfigureAwait(false);
            var rows = await client.QueryAsync("select 1").ConfigureAwait(false);
            client.Dispose();

            var getter = Task.Run(() => rows.GetInt32(0));
            Assert.True(await getterEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false));
            var disposeTask = rows.DisposeAsync().AsTask();
            await Task.Delay(30).ConfigureAwait(false);
            Assert.False(disposeTask.IsCompleted);

            releaseGetter.TrySetResult(true);
            Assert.Equal(1, await getter.ConfigureAwait(false));
            await AssertCompletesAsync(disposeTask, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            Assert.Equal(1, pool.GetMetrics().IdleConnections);
        }

        [Fact]
        public async Task PooledStatementDisposeWaitsForSynchronousGetter()
        {
            var getterEntered = NewCompletionSource<bool>();
            var releaseGetter = NewCompletionSource<bool>();
            var innerStmt = new OfflineFakeStmt(releaseGetter.Task, getterEntered);
            using var pool = CreatePool(DefaultOptions(), _ =>
                Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient(stmtFactory: () => innerStmt)));

            var client = await pool.AcquireAsync().ConfigureAwait(false);
            var stmt = await client.StmtInitAsync().ConfigureAwait(false);
            client.Dispose();

            var getter = Task.Run(() => stmt.IsInsert());
            Assert.True(await getterEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false));
            var disposeTask = stmt.DisposeAsync().AsTask();
            await Task.Delay(30).ConfigureAwait(false);
            Assert.False(disposeTask.IsCompleted);

            releaseGetter.TrySetResult(true);
            Assert.False(await getter.ConfigureAwait(false));
            await AssertCompletesAsync(disposeTask, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            Assert.Equal(1, pool.GetMetrics().IdleConnections);
        }

        [Fact]
        public async Task PooledRowsDisposeFailureInvalidatesConnection()
        {
            var innerRows = new OfflineFakeRows(disposeThrows: true);
            using var pool = CreatePool(DefaultOptions(), _ =>
                Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient(rowsFactory: () => innerRows)));

            var client = await pool.AcquireAsync().ConfigureAwait(false);
            var rows = await client.QueryAsync("select 1").ConfigureAwait(false);
            client.Dispose();

            await Assert.ThrowsAsync<InvalidOperationException>(() => rows.DisposeAsync().AsTask())
                .ConfigureAwait(false);
            Assert.Equal(0, pool.GetMetrics().TotalConnections);
        }

        [Fact]
        public async Task PooledStatementDisposeFailureInvalidatesConnection()
        {
            var innerStmt = new OfflineFakeStmt(disposeThrows: true);
            using var pool = CreatePool(DefaultOptions(), _ =>
                Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient(stmtFactory: () => innerStmt)));

            var client = await pool.AcquireAsync().ConfigureAwait(false);
            var stmt = await client.StmtInitAsync().ConfigureAwait(false);
            client.Dispose();

            await Assert.ThrowsAsync<InvalidOperationException>(() => stmt.DisposeAsync().AsTask())
                .ConfigureAwait(false);
            Assert.Equal(0, pool.GetMetrics().TotalConnections);
        }

        [Fact]
        public async Task PooledStatementResultCleanupFailureInvalidatesConnection()
        {
            var resultEntered = NewCompletionSource<bool>();
            var releaseResult = NewCompletionSource<bool>();
            var innerRows = new OfflineFakeRows(disposeThrows: true);
            var innerStmt = new OfflineFakeStmt(resultGate: releaseResult.Task,
                resultEntered: resultEntered, resultRowsFactory: () => innerRows);
            using var pool = CreatePool(DefaultOptions(), _ =>
                Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient(stmtFactory: () => innerStmt)));

            var client = await pool.AcquireAsync().ConfigureAwait(false);
            var stmt = await client.StmtInitAsync().ConfigureAwait(false);
            client.Dispose();

            var resultTask = stmt.ResultAsync();
            Assert.True(await resultEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false));
            await stmt.DisposeAsync().ConfigureAwait(false);
            releaseResult.TrySetResult(true);

            await Assert.ThrowsAsync<ObjectDisposedException>(() => resultTask).ConfigureAwait(false);
            Assert.Equal(0, pool.GetMetrics().IdleConnections);
            Assert.Equal(0, pool.GetMetrics().TotalConnections);
        }

        [Fact]
        public void PoolDisposeContinuesWhenCancellationCallbackThrows()
        {
            using var pool = CreatePool(DefaultOptions(), _ =>
                Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient()));
            using var registration = GetPrivateField<CancellationTokenSource>(pool, "_disposeCts")
                .Token.Register(() => throw new InvalidOperationException("test cancellation callback"));

            pool.Dispose();
            AssertNonNegativeCounters(pool.GetMetrics());
            Assert.Equal(0, pool.GetMetrics().TotalConnections);
        }

        [Fact]
        public void InternalPoolSnapshotsConnectionBuilder()
        {
            var builder = new ConnectionStringBuilder(
                "protocol=WebSocket;host=original;port=6041;customOption=original-value");
            using var pool = CreatePoolFromBuilder(builder, DefaultOptions(), _ =>
                Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient()));

            builder.Host = "mutated";
            builder["customOption"] = "mutated-value";
            var snapshot = GetPrivateField<ConnectionStringBuilder>(pool, "_builder");
            Assert.Equal("original", snapshot.Host);
            Assert.True(snapshot.TryGetValue("customOption", out var customOption));
            Assert.Equal("original-value", customOption);
        }

        [Fact]
        public void RegistryPoolEntrySnapshotsBuilderBeforeLazyCreation()
        {
            var builder = new ConnectionStringBuilder(
                "protocol=WebSocket;host=original;port=6041;maxPoolSize=3");
            var entry = new WSClientAsyncPoolRegistry.PoolEntry(builder);

            builder.Host = "mutated";
            builder.MaxPoolSize = 9;
            var pool = entry.GetPool();
            try
            {
                var snapshot = GetPrivateField<ConnectionStringBuilder>(pool, "_builder");
                var options = GetPrivateField<WSClientAsyncPoolOptions>(pool, "_options");
                Assert.Equal("original", snapshot.Host);
                Assert.Equal(3, options.MaximumPoolSize);
            }
            finally
            {
                Assert.True(entry.TryRetire(true));
                entry.DisposePool();
            }
        }

        [Fact]
        public async Task RegistryScheduledTrimDoesNotBlockOnSlowPoolDisposal()
        {
            WSClientAsyncPoolRegistry.Clear();
            var disposeStarted = NewCompletionSource<bool>();
            using var releaseDispose = new ManualResetEventSlim(false);
            var client = new OfflineFakeClient(disposeAction: () =>
            {
                disposeStarted.TrySetResult(true);
                releaseDispose.Wait();
            });
            var pool = CreatePool(DefaultOptions(), _ => Task.FromResult<ITDengineClientAsync>(client));
            try
            {
                using (await pool.AcquireAsync().ConfigureAwait(false))
                {
                }

                const string key = "slow-registry-disposal";
                var entry = new WSClientAsyncPoolRegistry.PoolEntry(() => pool);
                Assert.Same(pool, entry.GetPool());
                var poolsField = typeof(WSClientAsyncPoolRegistry).GetField("Pools",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                var lastTrimField = typeof(WSClientAsyncPoolRegistry).GetField("_lastTrimTimestamp",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                var trimMethod = typeof(WSClientAsyncPoolRegistry).GetMethod("ScheduledTrim",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                var lastAccessField = typeof(WSClientAsyncPoolRegistry.PoolEntry).GetField("_lastAccessTimestamp",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(poolsField);
                Assert.NotNull(lastTrimField);
                Assert.NotNull(trimMethod);
                Assert.NotNull(lastAccessField);
                var pools = Assert.IsType<System.Collections.Concurrent.ConcurrentDictionary<string,
                    WSClientAsyncPoolRegistry.PoolEntry>>(poolsField!.GetValue(null));
                Assert.True(pools.TryAdd(key, entry));
                var staleTimestamp = Stopwatch.GetTimestamp() -
                                     checked((long)(TimeSpan.FromHours(1).TotalSeconds * Stopwatch.Frequency));
                lastAccessField!.SetValue(entry, staleTimestamp);
                lastTrimField!.SetValue(null, 0L);

                var stopwatch = Stopwatch.StartNew();
                var trimTask = Task.Run(() => trimMethod!.Invoke(null, new object[] { null! }));
                var completedTrim = await Task.WhenAny(trimTask, Task.Delay(TimeSpan.FromMilliseconds(500)))
                    .ConfigureAwait(false);
                if (!ReferenceEquals(completedTrim, trimTask))
                {
                    releaseDispose.Set();
                }

                await trimTask.ConfigureAwait(false);
                stopwatch.Stop();

                Assert.Same(trimTask, completedTrim);
                Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
                    $"Registry trim blocked for {stopwatch.Elapsed}.");
                await AssertCompletesAsync(disposeStarted.Task, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                Assert.Equal(0, WSClientAsyncPoolRegistry.Count);
                releaseDispose.Set();
                await WaitForConditionAsync(() => client.DisposeCount == 1, TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
            finally
            {
                releaseDispose.Set();
                pool.Dispose();
                WSClientAsyncPoolRegistry.Clear();
            }
        }

        [Fact]
        public async Task PoolDisposeReleasesSynchronizationPrimitives()
        {
            var pool = CreatePool(DefaultOptions(), _ =>
                Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient()));
            var threadCache = GetPrivateField<object>(pool, "_threadCache");
            var idleSignal = GetPrivateField<SemaphoreSlim>(pool, "_idleSignal");
            var drainedSignal = GetPrivateField<SemaphoreSlim>(pool, "_operationsDrainedSignal");

            await pool.DisposeAsync().ConfigureAwait(false);

            Assert.Throws<ObjectDisposedException>(() => idleSignal.Wait(0));
            Assert.Throws<ObjectDisposedException>(() => drainedSignal.Wait(0));
            var isValueCreated = threadCache.GetType().GetProperty("IsValueCreated");
            Assert.NotNull(isValueCreated);
            var invocation = Assert.Throws<System.Reflection.TargetInvocationException>(
                () => isValueCreated!.GetValue(threadCache));
            Assert.IsType<ObjectDisposedException>(invocation.InnerException);
        }

        [Fact]
        public async Task PoolDisposeRejectsNewChildOperationsWhileLeaseIsDraining()
        {
            var pool = CreatePool(DefaultOptions(), _ =>
                Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient()));
            var client = await pool.AcquireAsync().ConfigureAwait(false);
            var rows = await client.QueryAsync("select 1").ConfigureAwait(false);
            client.Dispose();
            var lease = GetPrivateField<object>(rows, "_lease");

            var disposeTask = pool.DisposeAsync().AsTask();
            await WaitForPrivateIntAsync(lease, "_forceCloseRequested", 1).ConfigureAwait(false);

            await Assert.ThrowsAsync<ObjectDisposedException>(() => rows.ReadAsync()).ConfigureAwait(false);
            await rows.DisposeAsync().ConfigureAwait(false);
            await AssertCompletesAsync(disposeTask, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.Equal(0, pool.GetMetrics().TotalConnections);
        }

        [Fact]
        public async Task ValidatorIgnoringCancellationDoesNotBlockPoolDispose()
        {
            var validatorEntered = NewCompletionSource<bool>();
            var validatorCompletion = NewCompletionSource<bool>();
            var pool = CreatePool(new WSClientAsyncPoolOptions
            {
                MaximumPoolSize = 1,
                ConnectionTimeout = TimeSpan.FromMilliseconds(100),
                KeepaliveTime = TimeSpan.FromMilliseconds(1),
                MaxLifetime = TimeSpan.Zero,
                HousekeepingInterval = TimeSpan.FromMilliseconds(10),
                CreationRetryBackoff = TimeSpan.Zero,
                MaxCreationRetryBackoff = TimeSpan.Zero
            }, _ => Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient()),
                (_, _) =>
                {
                    validatorEntered.TrySetResult(true);
                    return validatorCompletion.Task;
                });

            using (await pool.AcquireAsync().ConfigureAwait(false))
            {
            }

            await AssertCompletesAsync(validatorEntered.Task, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await AssertCompletesAsync(pool.DisposeAsync().AsTask(), TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);
            AssertNonNegativeCounters(pool.GetMetrics());
            Assert.Equal(0, pool.GetMetrics().TotalConnections);
            validatorCompletion.TrySetResult(true);
        }

        [Fact]
        public async Task FactoryIgnoringCancellationDoesNotBlockPoolDisposeAndLateClientIsDisposed()
        {
            var factoryEntered = NewCompletionSource<bool>();
            var factoryCompletion = NewCompletionSource<ITDengineClientAsync>();
            var lateClient = new OfflineFakeClient();
            var pool = CreatePool(new WSClientAsyncPoolOptions
            {
                MaximumPoolSize = 1,
                ConnectionTimeout = TimeSpan.FromSeconds(5),
                KeepaliveTime = TimeSpan.Zero,
                MaxLifetime = TimeSpan.Zero,
                HousekeepingInterval = TimeSpan.FromMinutes(5),
                CreationRetryBackoff = TimeSpan.Zero,
                MaxCreationRetryBackoff = TimeSpan.Zero
            }, _ =>
            {
                factoryEntered.TrySetResult(true);
                return factoryCompletion.Task;
            });

            try
            {
                var acquireTask = pool.AcquireAsync();
                await AssertCompletesAsync(factoryEntered.Task, TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                var disposeTask = pool.DisposeAsync().AsTask();
                await AssertCompletesAsync(disposeTask, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                await Assert.ThrowsAsync<ObjectDisposedException>(() => acquireTask).ConfigureAwait(false);

                factoryCompletion.TrySetResult(lateClient);
                await WaitForConditionAsync(() => lateClient.DisposeCount == 1, TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
                Assert.Equal(0, pool.GetMetrics().TotalConnections);
            }
            finally
            {
                factoryCompletion.TrySetResult(lateClient);
                await pool.DisposeAsync().ConfigureAwait(false);
            }
        }

        [Fact]
        public async Task RegistryEntryCreationRemainsStableDuringConcurrentClears()
        {
            WSClientAsyncPoolRegistry.Clear();
            var builder = new ConnectionStringBuilder("protocol=WebSocket;host=localhost;port=6041");
            var start = NewCompletionSource<bool>();
            var workerCount = Math.Max(4, Environment.ProcessorCount);
            var workers = new Task[workerCount + 1];
            for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
            {
                var capturedWorkerIndex = workerIndex;
                workers[workerIndex] = Task.Run(async () =>
                {
                    await start.Task.ConfigureAwait(false);
                    for (var iteration = 0; iteration < 2000; iteration++)
                    {
                        var key = "registry-contention-" + ((capturedWorkerIndex + iteration) & 7);
                        var entry = WSClientAsyncPoolRegistry.GetOrCreateEntry(key, builder);
                        if (entry.TryEnter())
                        {
                            entry.Exit();
                        }
                    }
                });
            }

            workers[workerCount] = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                for (var iteration = 0; iteration < 1000; iteration++)
                {
                    WSClientAsyncPoolRegistry.Clear();
                }
            });

            try
            {
                start.TrySetResult(true);
                await AssertCompletesAsync(Task.WhenAll(workers), TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                Assert.InRange(WSClientAsyncPoolRegistry.Count, 0, 8);
            }
            finally
            {
                WSClientAsyncPoolRegistry.Clear();
            }
        }

        [Fact]
        public async Task ChildOperationsRacingPoolDisposeDoNotUnderflowMetrics()
        {
            const int leaseCount = 16;
            var pool = CreatePool(new WSClientAsyncPoolOptions
            {
                MaximumPoolSize = leaseCount,
                ConnectionTimeout = TimeSpan.FromMilliseconds(100),
                KeepaliveTime = TimeSpan.Zero,
                MaxLifetime = TimeSpan.Zero,
                HousekeepingInterval = TimeSpan.FromMinutes(5),
                CreationRetryBackoff = TimeSpan.Zero,
                MaxCreationRetryBackoff = TimeSpan.Zero
            }, _ => Task.FromResult<ITDengineClientAsync>(new OfflineFakeClient()));
            var rows = new IRowsAsync[leaseCount];
            for (var i = 0; i < rows.Length; i++)
            {
                var client = await pool.AcquireAsync().ConfigureAwait(false);
                rows[i] = await client.QueryAsync("select 1").ConfigureAwait(false);
                client.Dispose();
            }

            var start = NewCompletionSource<bool>();
            var operations = new Task[rows.Length];
            for (var i = 0; i < rows.Length; i++)
            {
                var currentRows = rows[i];
                operations[i] = Task.Run(async () =>
                {
                    await start.Task.ConfigureAwait(false);
                    try
                    {
                        await currentRows.ReadAsync().ConfigureAwait(false);
                        currentRows.GetName(0);
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });
            }

            var disposeTask = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                await pool.DisposeAsync().ConfigureAwait(false);
            });
            start.TrySetResult(true);
            await Task.WhenAll(operations).ConfigureAwait(false);
            await AssertCompletesAsync(disposeTask, TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            foreach (var currentRows in rows)
            {
                await currentRows.DisposeAsync().ConfigureAwait(false);
            }

            var metrics = pool.GetMetrics();
            AssertNonNegativeCounters(metrics);
            Assert.Equal(0, metrics.ActiveConnections);
            Assert.Equal(0, metrics.IdleConnections);
            Assert.Equal(0, metrics.MaintenanceConnections);
            Assert.Equal(0, metrics.TotalConnections);
        }

        [Theory]
        [InlineData(",host-a")]
        [InlineData("host-a,,host-b")]
        [InlineData("host-a,")]
        [InlineData("host-a,   ,host-b")]
        public void EmptyFailoverEndpointIsRejected(string hosts)
        {
            var builder = new ConnectionStringBuilder(
                $"protocol=WebSocket;host={hosts};port=6041");

            Assert.Throws<ArgumentException>(() => builder.GetFailoverAddresses());
        }

        private static WSClientAsyncPoolOptions DefaultOptions()
        {
            return new WSClientAsyncPoolOptions
            {
                MaximumPoolSize = 1,
                ConnectionTimeout = TimeSpan.FromSeconds(2),
                KeepaliveTime = TimeSpan.Zero,
                MaxLifetime = TimeSpan.Zero,
                HousekeepingInterval = TimeSpan.FromMinutes(5),
                CreationRetryBackoff = TimeSpan.Zero,
                MaxCreationRetryBackoff = TimeSpan.Zero
            };
        }

        private static WSClientAsyncPool CreatePool(WSClientAsyncPoolOptions options,
            Func<CancellationToken, Task<ITDengineClientAsync>> factory,
            Func<ITDengineClientAsync, CancellationToken, Task<bool>>? validator = null)
        {
            return CreatePoolFromBuilder(new ConnectionStringBuilder(
                    "protocol=WebSocket;host=localhost;port=6041"), options, factory, validator);
        }

        private static WSClientAsyncPool CreatePoolFromBuilder(ConnectionStringBuilder builder,
            WSClientAsyncPoolOptions options, Func<CancellationToken, Task<ITDengineClientAsync>> factory,
            Func<ITDengineClientAsync, CancellationToken, Task<bool>>? validator = null)
        {
            return new WSClientAsyncPool(builder, options, factory, validator);
        }

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            var field = target.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            return (T)field!.GetValue(target)!;
        }

        private static int GetPrivateCollectionCount(object target, string fieldName)
        {
            var collection = GetPrivateField<System.Collections.ICollection>(target, fieldName);
            return collection.Count;
        }

        private static object GetOnlyPrivateDictionaryKey(object target, string fieldName)
        {
            var dictionary = GetPrivateField<System.Collections.IEnumerable>(target, fieldName);
            object? key = null;
            var count = 0;
            foreach (var entry in dictionary)
            {
                Assert.NotNull(entry);
                var keyProperty = entry!.GetType().GetProperty("Key");
                Assert.NotNull(keyProperty);
                key = keyProperty!.GetValue(entry);
                count++;
            }

            Assert.Equal(1, count);
            Assert.NotNull(key);
            return key!;
        }

        private static Task RunHousekeepingAsync(WSClientAsyncPool pool)
        {
            var method = typeof(WSClientAsyncPool).GetMethod("RunHousekeepingAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);
            return (Task)method!.Invoke(pool, new object[] { CancellationToken.None })!;
        }

        private static FailoverAddress Address(string host)
        {
            return new FailoverAddress(host, 6041, "ws://" + host + ":6041");
        }

        private static async Task WaitForMetricAsync(WSClientAsyncPool pool,
            Func<WSClientAsyncPoolMetrics, bool> predicate)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(2))
            {
                if (predicate(pool.GetMetrics()))
                {
                    return;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            Assert.True(predicate(pool.GetMetrics()), "The expected pool metric state was not reached.");
        }

        private static async Task WaitForPrivateIntAsync(object target, string fieldName, int expected)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(2))
            {
                if (GetPrivateField<int>(target, fieldName) == expected)
                {
                    return;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            Assert.Equal(expected, GetPrivateField<int>(target, fieldName));
        }

        private static async Task WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (predicate())
                {
                    return;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            Assert.True(predicate(), "The expected condition was not reached.");
        }

        private static async Task<T> AssertCompletesAsync<T>(Task<T> task, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            Assert.Same(task, completed);
            return await task.ConfigureAwait(false);
        }

        private static async Task AssertCompletesAsync(Task task, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            Assert.Same(task, completed);
            await task.ConfigureAwait(false);
        }

        private static void AssertNonNegativeCounters(WSClientAsyncPoolMetrics metrics)
        {
            Assert.True(metrics.ActiveConnections >= 0);
            Assert.True(metrics.IdleConnections >= 0);
            Assert.True(metrics.TotalConnections >= 0);
            Assert.True(metrics.ThreadsAwaitingConnection >= 0);
            Assert.True(metrics.MaintenanceConnections >= 0);
        }

        private static TaskCompletionSource<T> NewCompletionSource<T>()
        {
            return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class OfflineFakeClient : ITDengineClientAsync
        {
            private readonly Task _execGate;
            private readonly Task _queryGate;
            private readonly Task _stmtGate;
            private readonly Func<OfflineFakeRows> _rowsFactory;
            private readonly Func<OfflineFakeStmt> _stmtFactory;
            private readonly bool _available;
            private readonly Action? _disposeAction;
            private int _disposed;
            private int _disposeCount;

            internal OfflineFakeClient(int id = 1, Task? execGate = null, Task? queryGate = null,
                Task? stmtGate = null, Func<OfflineFakeRows>? rowsFactory = null,
                Func<OfflineFakeStmt>? stmtFactory = null, bool available = true, Action? disposeAction = null)
            {
                Id = id;
                _execGate = execGate ?? Task.CompletedTask;
                _queryGate = queryGate ?? Task.CompletedTask;
                _stmtGate = stmtGate ?? Task.CompletedTask;
                _rowsFactory = rowsFactory ?? (() => new OfflineFakeRows());
                _stmtFactory = stmtFactory ?? (() => new OfflineFakeStmt());
                _available = available;
                _disposeAction = disposeAction;
            }

            internal int Id { get; }
            internal int DisposeCount => Volatile.Read(ref _disposeCount);

            public WebSocketState State => ConnectionAvailable() ? WebSocketState.Open : WebSocketState.Closed;

            public bool ConnectionAvailable()
            {
                return _available && Volatile.Read(ref _disposed) == 0;
            }

            public Task<IStmtAsync> StmtInitAsync()
            {
                return StmtInitAsync(0, CancellationToken.None);
            }

            public Task<IStmtAsync> StmtInitAsync(long reqId)
            {
                return StmtInitAsync(reqId, CancellationToken.None);
            }

            public Task<IStmtAsync> StmtInitAsync(CancellationToken cancellationToken)
            {
                return StmtInitAsync(0, cancellationToken);
            }

            public async Task<IStmtAsync> StmtInitAsync(long reqId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                await _stmtGate.ConfigureAwait(false);
                ThrowIfDisposed();
                return _stmtFactory();
            }

            public Task<IRowsAsync> QueryAsync(string query)
            {
                return QueryAsync(query, 0, CancellationToken.None);
            }

            public Task<IRowsAsync> QueryAsync(string query, long reqId)
            {
                return QueryAsync(query, reqId, CancellationToken.None);
            }

            public Task<IRowsAsync> QueryAsync(string query, CancellationToken cancellationToken)
            {
                return QueryAsync(query, 0, cancellationToken);
            }

            public async Task<IRowsAsync> QueryAsync(string query, long reqId,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                await _queryGate.ConfigureAwait(false);
                ThrowIfDisposed();
                return _rowsFactory();
            }

            public Task<long> ExecAsync(string query)
            {
                return ExecAsync(query, 0, CancellationToken.None);
            }

            public Task<long> ExecAsync(string query, long reqId)
            {
                return ExecAsync(query, reqId, CancellationToken.None);
            }

            public Task<long> ExecAsync(string query, CancellationToken cancellationToken)
            {
                return ExecAsync(query, 0, cancellationToken);
            }

            public async Task<long> ExecAsync(string query, long reqId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                await _execGate.ConfigureAwait(false);
                ThrowIfDisposed();
                return 1;
            }

            public Task SchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol,
                TDengineSchemalessPrecision precision, int ttl, long reqId)
            {
                return SchemalessInsertAsync(lines, protocol, precision, ttl, reqId, CancellationToken.None);
            }

            public Task SchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol,
                TDengineSchemalessPrecision precision, int ttl, long reqId,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Increment(ref _disposeCount);
                    _disposeAction?.Invoke();
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }

            private void ThrowIfDisposed()
            {
                if (!ConnectionAvailable())
                {
                    throw new ObjectDisposedException(nameof(OfflineFakeClient));
                }
            }
        }

        private sealed class OfflineFakeRows : IRowsAsync
        {
            private readonly Task _getterGate;
            private readonly TaskCompletionSource<bool>? _getterEntered;
            private readonly bool _disposeThrows;
            private int _disposed;
            private int _read;

            internal OfflineFakeRows(Task? getterGate = null,
                TaskCompletionSource<bool>? getterEntered = null, bool disposeThrows = false)
            {
                _getterGate = getterGate ?? Task.CompletedTask;
                _getterEntered = getterEntered;
                _disposeThrows = disposeThrows;
            }

            public bool HasRows => true;
            public int AffectRows => -1;
            public int FieldCount => 1;
            public long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length) => 0;
            public char GetChar(int ordinal) => '1';
            public long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length) => 0;
            public string GetDataTypeName(int ordinal) => "INT";
            public object GetValue(int ordinal) => GetInt32(ordinal);
            public Type GetFieldType(int ordinal) => typeof(int);
            public int GetFieldSize(int ordinal) => sizeof(int);
            public string GetName(int ordinal) => "value";
            public int GetFieldPrecision(int ordinal) => 0;
            public int GetFieldScale(int ordinal) => 0;
            public int GetOrdinal(string name) => 0;
            public Task<bool> ReadAsync() => ReadAsync(CancellationToken.None);

            public Task<bool> ReadAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                return Task.FromResult(Interlocked.Increment(ref _read) == 1);
            }

            public bool IsDBNull(int ordinal) => false;
            public byte GetByte(int ordinal) => 1;
            public short GetInt16(int ordinal) => 1;
            public int GetInt32(int ordinal)
            {
                _getterEntered?.TrySetResult(true);
                _getterGate.GetAwaiter().GetResult();
                ThrowIfDisposed();
                return 1;
            }

            public long GetInt64(int ordinal) => 1;
            public bool GetBoolean(int ordinal) => true;
            public DateTime GetDateTime(int ordinal) => DateTime.UnixEpoch;
            public decimal GetDecimal(int ordinal) => 1;
            public double GetDouble(int ordinal) => 1;
            public float GetFloat(int ordinal) => 1;
            public string GetString(int ordinal) => "1";
            public int GetValues(object[] values)
            {
                values[0] = 1;
                return 1;
            }

            public DateTimeOffset GetDateTimeOffset(int ordinal) => DateTimeOffset.UnixEpoch;

            public void Dispose()
            {
                Interlocked.Exchange(ref _disposed, 1);
                if (_disposeThrows)
                {
                    throw new InvalidOperationException("offline rows disposal failed");
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }

            private void ThrowIfDisposed()
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(OfflineFakeRows));
                }
            }
        }

        private sealed class OfflineFakeStmt : IStmtAsync
        {
            private readonly Task _getterGate;
            private readonly TaskCompletionSource<bool>? _getterEntered;
            private readonly bool _disposeThrows;
            private readonly Task _resultGate;
            private readonly TaskCompletionSource<bool>? _resultEntered;
            private readonly Func<OfflineFakeRows> _resultRowsFactory;
            private int _disposed;

            internal OfflineFakeStmt(Task? getterGate = null,
                TaskCompletionSource<bool>? getterEntered = null, bool disposeThrows = false,
                Task? resultGate = null, TaskCompletionSource<bool>? resultEntered = null,
                Func<OfflineFakeRows>? resultRowsFactory = null)
            {
                _getterGate = getterGate ?? Task.CompletedTask;
                _getterEntered = getterEntered;
                _disposeThrows = disposeThrows;
                _resultGate = resultGate ?? Task.CompletedTask;
                _resultEntered = resultEntered;
                _resultRowsFactory = resultRowsFactory ?? (() => new OfflineFakeRows());
            }

            public Task PrepareAsync(string query) => PrepareAsync(query, CancellationToken.None);
            public Task PrepareAsync(string query, CancellationToken cancellationToken) => Completed(cancellationToken);
            public bool IsInsert()
            {
                _getterEntered?.TrySetResult(true);
                _getterGate.GetAwaiter().GetResult();
                ThrowIfDisposed();
                return false;
            }
            public Task SetTableNameAsync(string tableName) => SetTableNameAsync(tableName, CancellationToken.None);
            public Task SetTableNameAsync(string tableName, CancellationToken cancellationToken) => Completed(cancellationToken);
            public Task SetTagsAsync(object[] tags) => SetTagsAsync(tags, CancellationToken.None);
            public Task SetTagsAsync(object[] tags, CancellationToken cancellationToken) => Completed(cancellationToken);
            public Task<TaosFieldE[]> GetTagFieldsAsync() => GetTagFieldsAsync(CancellationToken.None);
            public Task<TaosFieldE[]> GetTagFieldsAsync(CancellationToken cancellationToken) => Fields(cancellationToken);
            public Task<TaosFieldE[]> GetColFieldsAsync() => GetColFieldsAsync(CancellationToken.None);
            public Task<TaosFieldE[]> GetColFieldsAsync(CancellationToken cancellationToken) => Fields(cancellationToken);
            public Task BindRowAsync(object[] row) => BindRowAsync(row, CancellationToken.None);
            public Task BindRowAsync(object[] row, CancellationToken cancellationToken) => Completed(cancellationToken);
            public Task BindColumnAsync(TaosFieldE[] fields, params Array[] arrays) =>
                BindColumnAsync(fields, CancellationToken.None, arrays);
            public Task BindColumnAsync(TaosFieldE[] fields, CancellationToken cancellationToken,
                params Array[] arrays) => Completed(cancellationToken);
            public Task AddBatchAsync() => AddBatchAsync(CancellationToken.None);
            public Task AddBatchAsync(CancellationToken cancellationToken) => Completed(cancellationToken);
            public Task ExecAsync() => ExecAsync(CancellationToken.None);
            public Task ExecAsync(CancellationToken cancellationToken) => Completed(cancellationToken);
            public long Affected() => 1;
            public Task<IRowsAsync> ResultAsync() => ResultAsync(CancellationToken.None);
            public async Task<IRowsAsync> ResultAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _resultEntered?.TrySetResult(true);
                await _resultGate.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return _resultRowsFactory();
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _disposed, 1);
                if (_disposeThrows)
                {
                    throw new InvalidOperationException("offline statement disposal failed");
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }

            private Task Completed(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                return Task.CompletedTask;
            }

            private Task<TaosFieldE[]> Fields(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                return Task.FromResult(Array.Empty<TaosFieldE>());
            }

            private void ThrowIfDisposed()
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(OfflineFakeStmt));
                }
            }
        }
    }
}
