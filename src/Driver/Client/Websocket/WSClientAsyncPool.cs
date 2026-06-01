using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace TDengine.Driver.Client.Websocket
{
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
    public sealed class WSClientAsyncPool : IDisposable, IAsyncDisposable
#else
    public sealed class WSClientAsyncPool : IDisposable
#endif
    {
        private readonly ConnectionStringBuilder _builder;
        private readonly WSClientAsyncPoolOptions _options;
        private readonly Func<CancellationToken, Task<ITDengineClientAsync>> _clientFactory;
        private readonly ConcurrentDictionary<PooledConnection, byte> _connections =
            new ConcurrentDictionary<PooledConnection, byte>();
        private readonly ConcurrentQueue<PooledConnection> _idleQueue = new ConcurrentQueue<PooledConnection>();
        private readonly ConcurrentDictionary<PoolLease, byte> _activeLeases =
            new ConcurrentDictionary<PoolLease, byte>();
        private readonly ThreadLocal<PooledConnection> _threadCache;
        private readonly SemaphoreSlim _idleSignal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        private readonly Task _housekeepingTask;
        private int _disposed;
        private int _totalConnections;
        private int _idleConnections;
        private int _activeConnections;
        private int _awaitingConnections;
        private long _acquireCount;
        private long _acquireTimeoutCount;
        private long _creationCount;
        private long _creationFailureCount;
        private long _disposedConnectionCount;
        private long _recycledConnectionCount;
        private long _totalAcquireStopwatchTicks;
        private long _maxAcquireStopwatchTicks;

        public WSClientAsyncPool(ConnectionStringBuilder builder)
            : this(builder, null)
        {
        }

        public WSClientAsyncPool(ConnectionStringBuilder builder, WSClientAsyncPoolOptions options)
            : this(CloneBuilder(builder), options, null)
        {
        }

        internal WSClientAsyncPool(ConnectionStringBuilder builder, WSClientAsyncPoolOptions options,
            Func<CancellationToken, Task<ITDengineClientAsync>> clientFactory)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (builder.Protocol != TDengineConstant.ProtocolWebSocket)
            {
                throw new ArgumentException("WSClientAsyncPool only supports WebSocket protocol.", nameof(builder));
            }

            _builder = builder;
            _options = (options ?? new WSClientAsyncPoolOptions()).CloneAndValidate();
            _clientFactory = clientFactory;
            _threadCache = new ThreadLocal<PooledConnection>(() => null);
            _housekeepingTask = Task.Run(() => HousekeepingLoopAsync(_disposeCts.Token));
        }

        public Task<ITDengineClientAsync> AcquireAsync()
        {
            return AcquireAsync(CancellationToken.None);
        }

        public Task WarmupAsync()
        {
            return WarmupAsync(CancellationToken.None);
        }

        public async Task<ITDengineClientAsync> AcquireAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var startTimestamp = Stopwatch.GetTimestamp();
            using (var timeoutCts = new CancellationTokenSource(_options.ConnectionTimeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token,
                       cancellationToken, _disposeCts.Token))
            {
                try
                {
                    return await AcquireCoreAsync(startTimestamp, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (Volatile.Read(ref _disposed) == 1 || _disposeCts.IsCancellationRequested)
                    {
                        throw new ObjectDisposedException(nameof(WSClientAsyncPool));
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    Interlocked.Increment(ref _acquireTimeoutCount);
                    throw new TimeoutException("Timed out waiting for a WebSocket async connection from the pool.");
                }
            }
        }

        public async Task WarmupAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            while (Volatile.Read(ref _idleConnections) < _options.MinIdle &&
                   Volatile.Read(ref _totalConnections) < _options.MaximumPoolSize)
            {
                if (!TryReserveConnectionSlot())
                {
                    return;
                }

                PooledConnection connection = null;
                try
                {
                    connection = await CreateConnectionAsync(PooledConnection.StateActive, cancellationToken)
                        .ConfigureAwait(false);
                    ReturnIdleConnection(connection);
                    connection = null;
                }
                catch
                {
                    if (connection == null)
                    {
                        Interlocked.Decrement(ref _totalConnections);
                    }

                    throw;
                }
                finally
                {
                    if (connection != null)
                    {
                        Interlocked.Decrement(ref _totalConnections);
                        await DisposeClientAsync(connection.Client, true).ConfigureAwait(false);
                    }
                }
            }
        }

        public WSClientAsyncPoolMetrics GetMetrics()
        {
            var acquireCount = Interlocked.Read(ref _acquireCount);
            var totalAcquireTicks = Interlocked.Read(ref _totalAcquireStopwatchTicks);
            var averageAcquireDuration = acquireCount == 0
                ? TimeSpan.Zero
                : StopwatchTicksToTimeSpan(totalAcquireTicks / acquireCount);

            return new WSClientAsyncPoolMetrics(
                Volatile.Read(ref _activeConnections),
                Volatile.Read(ref _idleConnections),
                Volatile.Read(ref _totalConnections),
                Volatile.Read(ref _awaitingConnections),
                acquireCount,
                Interlocked.Read(ref _acquireTimeoutCount),
                Interlocked.Read(ref _creationCount),
                Interlocked.Read(ref _creationFailureCount),
                Interlocked.Read(ref _disposedConnectionCount),
                Interlocked.Read(ref _recycledConnectionCount),
                averageAcquireDuration,
                StopwatchTicksToTimeSpan(Interlocked.Read(ref _maxAcquireStopwatchTicks)));
        }

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
        public async ValueTask DisposeAsync()
        {
            await DisposeCoreAsync(true).ConfigureAwait(false);
        }
#endif

        public void Dispose()
        {
            DisposeCoreAsync(false).GetAwaiter().GetResult();
        }

        private async Task<ITDengineClientAsync> AcquireCoreAsync(long startTimestamp,
            CancellationToken cancellationToken)
        {
            var backoff = _options.CreationRetryBackoff;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PooledConnection connection;
                if (TryAcquireIdleConnection(out connection))
                {
                    if (connection.Client.ConnectionAvailable() && !IsExpired(connection))
                    {
                        return CreateLease(connection, startTimestamp);
                    }

                    Interlocked.Decrement(ref _activeConnections);
                    await CloseActiveConnection(connection, IsExpired(connection), true)
                        .ConfigureAwait(false);
                    continue;
                }

                if (TryReserveConnectionSlot())
                {
                    try
                    {
                        connection = await CreateConnectionAsync(PooledConnection.StateActive, cancellationToken)
                            .ConfigureAwait(false);
                        Interlocked.Increment(ref _activeConnections);
                        return CreateLease(connection, startTimestamp);
                    }
                    catch
                    {
                        Interlocked.Decrement(ref _totalConnections);
                        Interlocked.Increment(ref _creationFailureCount);
                        ReleaseWaiter();

                        if (backoff > TimeSpan.Zero)
                        {
                            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                            backoff = NextBackoff(backoff);
                        }

                        continue;
                    }
                }

                Interlocked.Increment(ref _awaitingConnections);
                try
                {
                    if (TryAcquireIdleConnection(out connection))
                    {
                        if (connection.Client.ConnectionAvailable() && !IsExpired(connection))
                        {
                            return CreateLease(connection, startTimestamp);
                        }

                        Interlocked.Decrement(ref _activeConnections);
                        await CloseActiveConnection(connection, IsExpired(connection), true)
                            .ConfigureAwait(false);
                        continue;
                    }

                    await _idleSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _awaitingConnections);
                }
            }
        }

        private ITDengineClientAsync CreateLease(PooledConnection connection, long startTimestamp)
        {
            RecordAcquireDuration(startTimestamp);
            var lease = new PoolLease(this, connection, _options.LeakDetectionThreshold > TimeSpan.Zero);
            _activeLeases.TryAdd(lease, 0);
            Interlocked.Increment(ref _acquireCount);
            return new PooledTDengineClientAsync(lease);
        }

        private static ConnectionStringBuilder CloneBuilder(ConnectionStringBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            return new ConnectionStringBuilder(builder.ConnectionString);
        }

        private static async Task<ITDengineClientAsync> CreateWebSocketClientAsync(ConnectionStringBuilder builder,
            CancellationToken cancellationToken)
        {
            var client = new WSClientAsync(builder);
            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return client;
            }
            catch
            {
                await DisposeClientAsync(client, true).ConfigureAwait(false);
                throw;
            }
        }

        private async Task<PooledConnection> CreateConnectionAsync(int initialState,
            CancellationToken cancellationToken)
        {
            var client = _clientFactory == null
                ? await CreateWebSocketClientAsync(_builder, cancellationToken).ConfigureAwait(false)
                : await _clientFactory(cancellationToken).ConfigureAwait(false);
            if (client == null)
            {
                throw new InvalidOperationException("The WebSocket async pool client factory returned null.");
            }

            Interlocked.Increment(ref _creationCount);
            var connection = new PooledConnection(client, initialState);
            _connections.TryAdd(connection, 0);
            return connection;
        }

        private bool TryReserveConnectionSlot()
        {
            while (true)
            {
                var current = Volatile.Read(ref _totalConnections);
                if (current >= _options.MaximumPoolSize)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _totalConnections, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        private bool TryAcquireIdleConnection(out PooledConnection connection)
        {
            connection = _threadCache.Value;
            if (TryActivateCachedConnection(connection))
            {
                _threadCache.Value = null;
                return true;
            }

            if (connection != null)
            {
                _threadCache.Value = null;
            }

            while (_idleQueue.TryDequeue(out connection))
            {
                if (TryActivateQueuedConnection(connection))
                {
                    return true;
                }
            }

            foreach (var registered in _connections.Keys)
            {
                if (TryActivateCachedConnection(registered))
                {
                    connection = registered;
                    return true;
                }
            }

            connection = null;
            return false;
        }

        private bool TryActivateCachedConnection(PooledConnection connection)
        {
            if (connection == null)
            {
                return false;
            }

            if (Volatile.Read(ref connection.Queued) != 0)
            {
                return false;
            }

            return TryActivateIdleConnection(connection);
        }

        private bool TryActivateQueuedConnection(PooledConnection connection)
        {
            if (connection == null)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref connection.Queued, 0, 1) != 1)
            {
                return false;
            }

            return TryActivateIdleConnection(connection);
        }

        private bool TryActivateIdleConnection(PooledConnection connection)
        {
            if (Interlocked.CompareExchange(ref connection.State, PooledConnection.StateActive,
                    PooledConnection.StateIdle) != PooledConnection.StateIdle)
            {
                return false;
            }

            Interlocked.Decrement(ref _idleConnections);
            Interlocked.Increment(ref _activeConnections);
            return true;
        }

        private void ReturnIdleConnection(PooledConnection connection)
        {
            Volatile.Write(ref connection.LastUsedTimestamp, Stopwatch.GetTimestamp());
            if (Interlocked.CompareExchange(ref connection.State, PooledConnection.StateIdle,
                    PooledConnection.StateActive) != PooledConnection.StateActive)
            {
                return;
            }

            Interlocked.Increment(ref _idleConnections);
            var cached = _threadCache.Value;
            if (Volatile.Read(ref _awaitingConnections) == 0 &&
                (cached == null || Volatile.Read(ref cached.State) != PooledConnection.StateIdle))
            {
                _threadCache.Value = connection;
                ReleaseWaiter();
                return;
            }

            if (Interlocked.Exchange(ref connection.Queued, 1) == 0)
            {
                _idleQueue.Enqueue(connection);
                ReleaseWaiter();
            }
        }

        private async Task ReturnConnectionAsync(PoolLease lease, bool preferAsync)
        {
            _activeLeases.TryRemove(lease, out _);
            var connection = lease.Connection;
            Interlocked.Decrement(ref _activeConnections);

            if (Volatile.Read(ref _disposed) == 1 || !connection.Client.ConnectionAvailable())
            {
                await CloseActiveConnection(connection, false, preferAsync).ConfigureAwait(false);
                return;
            }

            if (IsExpired(connection))
            {
                await CloseActiveConnection(connection, true, preferAsync).ConfigureAwait(false);
                return;
            }

            ReturnIdleConnection(connection);
        }

        private async Task CloseActiveConnection(PooledConnection connection, bool recycled, bool preferAsync)
        {
            if (Interlocked.Exchange(ref connection.State, PooledConnection.StateClosed) ==
                PooledConnection.StateClosed)
            {
                return;
            }

            Interlocked.Exchange(ref connection.Queued, 0);
            _connections.TryRemove(connection, out _);
            Interlocked.Decrement(ref _totalConnections);
            Interlocked.Increment(ref _disposedConnectionCount);
            if (recycled)
            {
                Interlocked.Increment(ref _recycledConnectionCount);
            }

            await DisposeClientAsync(connection.Client, preferAsync).ConfigureAwait(false);
            ReleaseWaiter();
        }

        private async Task CloseIdleConnectionAsync(PooledConnection connection, bool recycled, bool preferAsync)
        {
            if (connection == null)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref connection.State, PooledConnection.StateClosed,
                    PooledConnection.StateIdle) != PooledConnection.StateIdle)
            {
                return;
            }

            Interlocked.Exchange(ref connection.Queued, 0);
            _connections.TryRemove(connection, out _);
            Interlocked.Decrement(ref _idleConnections);
            Interlocked.Decrement(ref _totalConnections);
            Interlocked.Increment(ref _disposedConnectionCount);
            if (recycled)
            {
                Interlocked.Increment(ref _recycledConnectionCount);
            }

            await DisposeClientAsync(connection.Client, preferAsync).ConfigureAwait(false);
            ReleaseWaiter();
        }

        private async Task HousekeepingLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(_options.HousekeepingInterval, cancellationToken).ConfigureAwait(false);
                    await RunHousekeepingAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                Trace.TraceWarning("WSClientAsyncPool housekeeping stopped: " + e);
            }
        }

        private async Task RunHousekeepingAsync(CancellationToken cancellationToken)
        {
            ReportLeakedConnections();
            await RecycleIdleConnectionsAsync(cancellationToken).ConfigureAwait(false);
            await EnsureMinIdleAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task RecycleIdleConnectionsAsync(CancellationToken cancellationToken)
        {
            foreach (var connection in _connections.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ShouldCloseIdleConnection(connection))
                {
                    await CloseIdleConnectionAsync(connection, true, true).ConfigureAwait(false);
                }
            }
        }

        private bool ShouldCloseIdleConnection(PooledConnection connection)
        {
            if (connection == null || Volatile.Read(ref connection.State) != PooledConnection.StateIdle)
            {
                return false;
            }

            if (IsExpired(connection))
            {
                return true;
            }

            if (_options.KeepaliveTime == TimeSpan.Zero)
            {
                return !connection.Client.ConnectionAvailable();
            }

            if (_options.KeepaliveTime > TimeSpan.Zero &&
                GetElapsed(Volatile.Read(ref connection.LastUsedTimestamp)) >= _options.KeepaliveTime)
            {
                return !connection.Client.ConnectionAvailable();
            }

            return false;
        }

        private async Task EnsureMinIdleAsync(CancellationToken cancellationToken)
        {
            while (Volatile.Read(ref _idleConnections) < _options.MinIdle &&
                   Volatile.Read(ref _totalConnections) < _options.MaximumPoolSize &&
                   Volatile.Read(ref _disposed) == 0)
            {
                if (!TryReserveConnectionSlot())
                {
                    return;
                }

                PooledConnection connection = null;
                try
                {
                    connection = await CreateConnectionAsync(PooledConnection.StateActive, cancellationToken)
                        .ConfigureAwait(false);
                    ReturnIdleConnection(connection);
                    connection = null;
                }
                catch
                {
                    Interlocked.Decrement(ref _totalConnections);
                    Interlocked.Increment(ref _creationFailureCount);
                    return;
                }
                finally
                {
                    if (connection != null)
                    {
                        await DisposeClientAsync(connection.Client, true).ConfigureAwait(false);
                    }
                }
            }
        }

        private void ReportLeakedConnections()
        {
            if (_options.LeakDetectionThreshold <= TimeSpan.Zero)
            {
                return;
            }

            foreach (var lease in _activeLeases.Keys)
            {
                var elapsed = GetElapsed(lease.CheckoutTimestamp);
                if (elapsed < _options.LeakDetectionThreshold || !lease.TryMarkLeakReported())
                {
                    continue;
                }

                var args = new WSClientAsyncPoolLeakEventArgs(elapsed, lease.CheckoutStackTrace);
                var callback = _options.LeakDetected;
                if (callback != null)
                {
                    callback(args);
                }
                else
                {
                    Trace.TraceWarning("Potential WSClientAsyncPool connection leak. Elapsed: " +
                                       elapsed + Environment.NewLine + lease.CheckoutStackTrace);
                }
            }
        }

        private async Task DisposeCoreAsync(bool preferAsync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _disposeCts.Cancel();
            await WaitHousekeepingAsync().ConfigureAwait(false);
            await CloseIdleConnectionsAsync(preferAsync).ConfigureAwait(false);
            _threadCache.Dispose();
            _idleSignal.Dispose();
            _disposeCts.Dispose();
        }

        private async Task WaitHousekeepingAsync()
        {
            try
            {
                await _housekeepingTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task CloseIdleConnectionsAsync(bool preferAsync)
        {
            PooledConnection connection;
            foreach (var cached in _connections.Keys)
            {
                await CloseIdleConnectionAsync(cached, false, preferAsync).ConfigureAwait(false);
            }

            while (_idleQueue.TryDequeue(out connection))
            {
                Interlocked.Exchange(ref connection.Queued, 0);
            }
        }

        private bool IsExpired(PooledConnection connection)
        {
            return _options.MaxLifetime > TimeSpan.Zero &&
                   GetElapsed(connection.CreatedTimestamp) >= _options.MaxLifetime;
        }

        private TimeSpan NextBackoff(TimeSpan current)
        {
            if (current == TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var nextTicks = current.Ticks > long.MaxValue / 2 ? long.MaxValue : current.Ticks * 2;
            if (_options.MaxCreationRetryBackoff > TimeSpan.Zero)
            {
                nextTicks = Math.Min(nextTicks, _options.MaxCreationRetryBackoff.Ticks);
            }

            return TimeSpan.FromTicks(nextTicks);
        }

        private void RecordAcquireDuration(long startTimestamp)
        {
            var elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
            Interlocked.Add(ref _totalAcquireStopwatchTicks, elapsedTicks);
            while (true)
            {
                var current = Interlocked.Read(ref _maxAcquireStopwatchTicks);
                if (elapsedTicks <= current)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _maxAcquireStopwatchTicks, elapsedTicks, current) == current)
                {
                    return;
                }
            }
        }

        private static TimeSpan GetElapsed(long startTimestamp)
        {
            return StopwatchTicksToTimeSpan(Stopwatch.GetTimestamp() - startTimestamp);
        }

        private static TimeSpan StopwatchTicksToTimeSpan(long stopwatchTicks)
        {
            if (stopwatchTicks <= 0)
            {
                return TimeSpan.Zero;
            }

            var ticks = stopwatchTicks * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;
            return TimeSpan.FromTicks((long)ticks);
        }

        private void ReleaseWaiter()
        {
            if (Volatile.Read(ref _awaitingConnections) <= 0)
            {
                return;
            }

            try
            {
                _idleSignal.Release();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SemaphoreFullException)
            {
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                throw new ObjectDisposedException(nameof(WSClientAsyncPool));
            }
        }

        private static async Task DisposeClientAsync(ITDengineClientAsync client, bool preferAsync)
        {
            if (client == null)
            {
                return;
            }

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
            if (preferAsync)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return;
            }
#endif
            client.Dispose();
            await CompletedTask().ConfigureAwait(false);
        }

        private static Task CompletedTask()
        {
#if NET45 || NET451
            return Task.FromResult(0);
#else
            return Task.CompletedTask;
#endif
        }

        private sealed class PooledConnection
        {
            internal const int StateIdle = 0;
            internal const int StateActive = 1;
            internal const int StateClosed = 2;

            internal readonly ITDengineClientAsync Client;
            internal readonly long CreatedTimestamp;
            internal long LastUsedTimestamp;
            internal int Queued;
            internal int State;

            internal PooledConnection(ITDengineClientAsync client, int state)
            {
                Client = client;
                CreatedTimestamp = Stopwatch.GetTimestamp();
                LastUsedTimestamp = CreatedTimestamp;
                State = state;
            }
        }

        private sealed class PoolLease
        {
            private readonly WSClientAsyncPool _pool;
            private int _references = 1;
            private int _clientDisposed;
            private int _returned;
            private int _leakReported;

            internal PoolLease(WSClientAsyncPool pool, PooledConnection connection, bool captureStackTrace)
            {
                _pool = pool;
                Connection = connection;
                CheckoutTimestamp = Stopwatch.GetTimestamp();
                CheckoutStackTrace = captureStackTrace ? Environment.StackTrace : string.Empty;
            }

            internal PooledConnection Connection { get; }

            internal long CheckoutTimestamp { get; }

            internal string CheckoutStackTrace { get; }

            internal WebSocketState State
            {
                get
                {
                    if (Volatile.Read(ref _returned) == 1)
                    {
                        return WebSocketState.Closed;
                    }

                    return Connection.Client.State;
                }
            }

            internal bool ConnectionAvailable()
            {
                return Volatile.Read(ref _returned) == 0 && Connection.Client.ConnectionAvailable();
            }

            internal void ThrowIfClientDisposed()
            {
                if (Volatile.Read(ref _clientDisposed) == 1 || Volatile.Read(ref _returned) == 1)
                {
                    throw new ObjectDisposedException(nameof(PooledTDengineClientAsync));
                }
            }

            internal void AddClientOperationReference()
            {
                ThrowIfClientDisposed();
                AddReference();
            }

            internal void AddChildReference()
            {
                AddReference();
            }

            private void AddReference()
            {
                while (true)
                {
                    var current = Volatile.Read(ref _references);
                    if (current <= 0 || Volatile.Read(ref _returned) == 1)
                    {
                        throw new ObjectDisposedException(nameof(PooledTDengineClientAsync));
                    }

                    if (Interlocked.CompareExchange(ref _references, current + 1, current) == current)
                    {
                        return;
                    }
                }
            }

            internal async Task ReleaseReferenceAsync(bool preferAsync)
            {
                if (Interlocked.Decrement(ref _references) != 0)
                {
                    return;
                }

                if (Interlocked.Exchange(ref _returned, 1) == 1)
                {
                    return;
                }

                await _pool.ReturnConnectionAsync(this, preferAsync).ConfigureAwait(false);
            }

            internal void DisposeClient()
            {
                if (Interlocked.Exchange(ref _clientDisposed, 1) == 0)
                {
                    ReleaseReferenceAsync(false).GetAwaiter().GetResult();
                }
            }

            internal async Task DisposeClientAsync()
            {
                if (Interlocked.Exchange(ref _clientDisposed, 1) == 0)
                {
                    await ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }

            internal bool TryMarkLeakReported()
            {
                return Interlocked.Exchange(ref _leakReported, 1) == 0;
            }
        }

        private sealed class PooledTDengineClientAsync : ITDengineClientAsync
        {
            private readonly PoolLease _lease;

            internal PooledTDengineClientAsync(PoolLease lease)
            {
                _lease = lease;
            }

            public WebSocketState State => _lease.State;

            public bool ConnectionAvailable()
            {
                return _lease.ConnectionAvailable();
            }

            public Task<IStmtAsync> StmtInitAsync()
            {
                return StmtInitAsync(ReqId.GetReqId(), CancellationToken.None);
            }

            public Task<IStmtAsync> StmtInitAsync(long reqId)
            {
                return StmtInitAsync(reqId, CancellationToken.None);
            }

            public Task<IStmtAsync> StmtInitAsync(CancellationToken cancellationToken)
            {
                return StmtInitAsync(ReqId.GetReqId(), cancellationToken);
            }

            public async Task<IStmtAsync> StmtInitAsync(long reqId, CancellationToken cancellationToken)
            {
                _lease.AddClientOperationReference();
                try
                {
                    var stmt = await _lease.Connection.Client.StmtInitAsync(reqId, cancellationToken)
                        .ConfigureAwait(false);
                    _lease.AddChildReference();
                    return new PooledStmtAsync(stmt, _lease);
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
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

            public async Task<IRowsAsync> QueryAsync(string query, long reqId,
                CancellationToken cancellationToken)
            {
                _lease.AddClientOperationReference();
                try
                {
                    var rows = await _lease.Connection.Client.QueryAsync(query, reqId, cancellationToken)
                        .ConfigureAwait(false);
                    _lease.AddChildReference();
                    return new PooledRowsAsync(rows, _lease);
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
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

            public async Task<long> ExecAsync(string query, long reqId, CancellationToken cancellationToken)
            {
                _lease.AddClientOperationReference();
                try
                {
                    return await _lease.Connection.Client.ExecAsync(query, reqId, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }

            public Task SchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol,
                TDengineSchemalessPrecision precision, int ttl, long reqId)
            {
                return SchemalessInsertAsync(lines, protocol, precision, ttl, reqId, CancellationToken.None);
            }

            public async Task SchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol,
                TDengineSchemalessPrecision precision, int ttl, long reqId, CancellationToken cancellationToken)
            {
                _lease.AddClientOperationReference();
                try
                {
                    await _lease.Connection.Client.SchemalessInsertAsync(lines, protocol, precision, ttl, reqId,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
            public async ValueTask DisposeAsync()
            {
                await _lease.DisposeClientAsync().ConfigureAwait(false);
            }
#endif

            public void Dispose()
            {
                _lease.DisposeClient();
            }
        }

        private sealed class PooledRowsAsync : IRowsAsync
        {
            private readonly IRowsAsync _inner;
            private readonly PoolLease _lease;
            private int _disposed;

            internal PooledRowsAsync(IRowsAsync inner, PoolLease lease)
            {
                _inner = inner;
                _lease = lease;
            }

            public bool HasRows => _inner.HasRows;

            public int AffectRows => _inner.AffectRows;

            public int FieldCount => _inner.FieldCount;

            public long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length)
            {
                return _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
            }

            public char GetChar(int ordinal)
            {
                return _inner.GetChar(ordinal);
            }

            public long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length)
            {
                return _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
            }

            public string GetDataTypeName(int ordinal)
            {
                return _inner.GetDataTypeName(ordinal);
            }

            public object GetValue(int ordinal)
            {
                return _inner.GetValue(ordinal);
            }

            public Type GetFieldType(int ordinal)
            {
                return _inner.GetFieldType(ordinal);
            }

            public int GetFieldSize(int ordinal)
            {
                return _inner.GetFieldSize(ordinal);
            }

            public string GetName(int ordinal)
            {
                return _inner.GetName(ordinal);
            }

            public int GetFieldPrecision(int ordinal)
            {
                return _inner.GetFieldPrecision(ordinal);
            }

            public int GetFieldScale(int ordinal)
            {
                return _inner.GetFieldScale(ordinal);
            }

            public int GetOrdinal(string name)
            {
                return _inner.GetOrdinal(name);
            }

            public Task<bool> ReadAsync()
            {
                return _inner.ReadAsync();
            }

            public Task<bool> ReadAsync(CancellationToken cancellationToken)
            {
                return _inner.ReadAsync(cancellationToken);
            }

            public bool IsDBNull(int ordinal)
            {
                return _inner.IsDBNull(ordinal);
            }

            public byte GetByte(int ordinal)
            {
                return _inner.GetByte(ordinal);
            }

            public short GetInt16(int ordinal)
            {
                return _inner.GetInt16(ordinal);
            }

            public int GetInt32(int ordinal)
            {
                return _inner.GetInt32(ordinal);
            }

            public long GetInt64(int ordinal)
            {
                return _inner.GetInt64(ordinal);
            }

            public bool GetBoolean(int ordinal)
            {
                return _inner.GetBoolean(ordinal);
            }

            public DateTime GetDateTime(int ordinal)
            {
                return _inner.GetDateTime(ordinal);
            }

            public decimal GetDecimal(int ordinal)
            {
                return _inner.GetDecimal(ordinal);
            }

            public double GetDouble(int ordinal)
            {
                return _inner.GetDouble(ordinal);
            }

            public float GetFloat(int ordinal)
            {
                return _inner.GetFloat(ordinal);
            }

            public string GetString(int ordinal)
            {
                return _inner.GetString(ordinal);
            }

            public int GetValues(object[] values)
            {
                return _inner.GetValues(values);
            }

            public DateTimeOffset GetDateTimeOffset(int ordinal)
            {
                return _inner.GetDateTimeOffset(ordinal);
            }

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1)
                {
                    return;
                }

                try
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }
#endif

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1)
                {
                    return;
                }

                try
                {
                    _inner.Dispose();
                }
                finally
                {
                    _lease.ReleaseReferenceAsync(false).GetAwaiter().GetResult();
                }
            }
        }

        private sealed class PooledStmtAsync : IStmtAsync
        {
            private readonly IStmtAsync _inner;
            private readonly PoolLease _lease;
            private int _disposed;

            internal PooledStmtAsync(IStmtAsync inner, PoolLease lease)
            {
                _inner = inner;
                _lease = lease;
            }

            public Task PrepareAsync(string query)
            {
                return _inner.PrepareAsync(query);
            }

            public Task PrepareAsync(string query, CancellationToken cancellationToken)
            {
                return _inner.PrepareAsync(query, cancellationToken);
            }

            public bool IsInsert()
            {
                return _inner.IsInsert();
            }

            public Task SetTableNameAsync(string tableName)
            {
                return _inner.SetTableNameAsync(tableName);
            }

            public Task SetTableNameAsync(string tableName, CancellationToken cancellationToken)
            {
                return _inner.SetTableNameAsync(tableName, cancellationToken);
            }

            public Task SetTagsAsync(object[] tags)
            {
                return _inner.SetTagsAsync(tags);
            }

            public Task SetTagsAsync(object[] tags, CancellationToken cancellationToken)
            {
                return _inner.SetTagsAsync(tags, cancellationToken);
            }

            public Task<TaosFieldE[]> GetTagFieldsAsync()
            {
                return _inner.GetTagFieldsAsync();
            }

            public Task<TaosFieldE[]> GetColFieldsAsync()
            {
                return _inner.GetColFieldsAsync();
            }

            public Task BindRowAsync(object[] row)
            {
                return _inner.BindRowAsync(row);
            }

            public Task BindRowAsync(object[] row, CancellationToken cancellationToken)
            {
                return _inner.BindRowAsync(row, cancellationToken);
            }

            public Task BindColumnAsync(TaosFieldE[] fields, params Array[] arrays)
            {
                return _inner.BindColumnAsync(fields, arrays);
            }

            public Task BindColumnAsync(TaosFieldE[] fields, CancellationToken cancellationToken,
                params Array[] arrays)
            {
                return _inner.BindColumnAsync(fields, cancellationToken, arrays);
            }

            public Task AddBatchAsync()
            {
                return _inner.AddBatchAsync();
            }

            public Task AddBatchAsync(CancellationToken cancellationToken)
            {
                return _inner.AddBatchAsync(cancellationToken);
            }

            public Task ExecAsync()
            {
                return _inner.ExecAsync();
            }

            public Task ExecAsync(CancellationToken cancellationToken)
            {
                return _inner.ExecAsync(cancellationToken);
            }

            public long Affected()
            {
                return _inner.Affected();
            }

            public Task<IRowsAsync> ResultAsync()
            {
                return ResultAsync(CancellationToken.None);
            }

            public async Task<IRowsAsync> ResultAsync(CancellationToken cancellationToken)
            {
                var rows = await _inner.ResultAsync(cancellationToken).ConfigureAwait(false);
                _lease.AddChildReference();
                return new PooledRowsAsync(rows, _lease);
            }

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1)
                {
                    return;
                }

                try
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }
#endif

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1)
                {
                    return;
                }

                try
                {
                    _inner.Dispose();
                }
                finally
                {
                    _lease.ReleaseReferenceAsync(false).GetAwaiter().GetResult();
                }
            }
        }
    }
}
