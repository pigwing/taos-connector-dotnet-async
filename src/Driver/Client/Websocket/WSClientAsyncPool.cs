using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private static int _jitterState = Environment.TickCount;
        private readonly ConnectionStringBuilder _builder;
        private readonly WSClientAsyncPoolOptions _options;
        private readonly Func<CancellationToken, Task<ITDengineClientAsync>> _clientFactory;
        private readonly Func<ITDengineClientAsync, CancellationToken, Task<bool>> _connectionValidator;
        private readonly ConcurrentDictionary<PooledConnection, byte> _connections =
            new ConcurrentDictionary<PooledConnection, byte>();
        private ConcurrentQueue<PooledConnection> _idleQueue = new ConcurrentQueue<PooledConnection>();
        private readonly ConcurrentDictionary<PoolLease, byte> _activeLeases =
            new ConcurrentDictionary<PoolLease, byte>();
        private readonly ThreadLocal<ThreadCacheSlot> _threadCache;
        private readonly SemaphoreSlim _idleSignal = new SemaphoreSlim(0);
        private readonly SemaphoreSlim _operationsDrainedSignal = new SemaphoreSlim(0, 1);
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        private readonly object _disposeLock = new object();
        private readonly Task _housekeepingTask;
        private Task _disposeTask;
        private int _disposed;
        private int _totalConnections;
        private int _idleConnections;
        private int _activeConnections;
        private int _awaitingConnections;
        private int _maintenanceConnections;
        private int _poolOperations;
        private int _pendingWakeups;
        private long _acquireCount;
        private long _acquireTimeoutCount;
        private long _creationCount;
        private long _creationFailureCount;
        private long _disposedConnectionCount;
        private long _recycledConnectionCount;
        private long _keepaliveCount;
        private long _keepaliveFailureCount;
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
            Func<CancellationToken, Task<ITDengineClientAsync>> clientFactory,
            Func<ITDengineClientAsync, CancellationToken, Task<bool>> connectionValidator = null)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (builder.Protocol != TDengineConstant.ProtocolWebSocket)
            {
                throw new ArgumentException("WSClientAsyncPool only supports WebSocket protocol.", nameof(builder));
            }

            // Keep the pool independent from a mutable builder supplied by an
            // internal caller as well as from the public constructors.
            _builder = builder.CreateSnapshot();
            _options = (options ?? new WSClientAsyncPoolOptions()).CloneAndValidate();
            _clientFactory = clientFactory;
            _connectionValidator = connectionValidator ?? ValidateClientAsync;
            _threadCache = new ThreadLocal<ThreadCacheSlot>(() => new ThreadCacheSlot(), false);
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
            BeginPoolOperation();
            try
            {
                var startTimestamp = Stopwatch.GetTimestamp();
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                           _disposeCts.Token))
                {
                    linkedCts.CancelAfter(_options.ConnectionTimeout);
                    return await AcquireCoreAsync(startTimestamp, linkedCts.Token, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                EndPoolOperation();
            }
        }

        public async Task WarmupAsync(CancellationToken cancellationToken)
        {
            BeginPoolOperation();
            try
            {
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                           _disposeCts.Token))
                {
                    try
                    {
                        while (Volatile.Read(ref _idleConnections) < _options.MinIdle &&
                               Volatile.Read(ref _totalConnections) < _options.MaximumPoolSize &&
                               Volatile.Read(ref _disposed) == 0)
                        {
                            linkedCts.Token.ThrowIfCancellationRequested();
                            if (!TryReserveConnectionSlot())
                            {
                                return;
                            }

                            PooledConnection connection = null;
                            try
                            {
                                connection = await CreateConnectionWithTimeoutAsync(PooledConnection.StateActive,
                                        linkedCts.Token)
                                    .ConfigureAwait(false);
                                if (Volatile.Read(ref _disposed) == 1)
                                {
                                    throw new ObjectDisposedException(nameof(WSClientAsyncPool));
                                }

                                await ReturnIdleConnectionAsync(connection, true).ConfigureAwait(false);
                                connection = null;
                            }
                            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested &&
                                                                    !cancellationToken.IsCancellationRequested)
                            {
                                await CleanupFailedCreationAsync(connection, true).ConfigureAwait(false);
                                throw new ObjectDisposedException(nameof(WSClientAsyncPool));
                            }
                            catch (OperationCanceledException)
                            {
                                await CleanupFailedCreationAsync(connection, true).ConfigureAwait(false);
                                throw;
                            }
                            catch
                            {
                                await CleanupFailedCreationAsync(connection, true).ConfigureAwait(false);
                                Interlocked.Increment(ref _creationFailureCount);
                                throw;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested &&
                                                            !cancellationToken.IsCancellationRequested)
                    {
                        throw new ObjectDisposedException(nameof(WSClientAsyncPool));
                    }
                }
            }
            finally
            {
                EndPoolOperation();
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
                Volatile.Read(ref _maintenanceConnections),
                acquireCount,
                Interlocked.Read(ref _acquireTimeoutCount),
                Interlocked.Read(ref _creationCount),
                Interlocked.Read(ref _creationFailureCount),
                Interlocked.Read(ref _disposedConnectionCount),
                Interlocked.Read(ref _recycledConnectionCount),
                Interlocked.Read(ref _keepaliveCount),
                Interlocked.Read(ref _keepaliveFailureCount),
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
            CancellationToken operationToken, CancellationToken callerToken)
        {
            var backoff = _options.CreationRetryBackoff;
            Exception lastCreationException = null;
            try
            {
                while (true)
                {
                    operationToken.ThrowIfCancellationRequested();
                    PooledConnection connection;
                    if (TryAcquireIdleConnection(out connection))
                    {
                        if (IsClientAvailable(connection) && !IsExpired(connection))
                        {
                            if (operationToken.IsCancellationRequested)
                            {
                                await ReleaseCanceledActivatedConnectionAsync(connection, true)
                                    .ConfigureAwait(false);
                                operationToken.ThrowIfCancellationRequested();
                            }

                            return await CreateLeaseAsync(connection, startTimestamp).ConfigureAwait(false);
                        }

                        await CloseActiveConnection(connection, IsExpired(connection), true)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (TryReserveConnectionSlot())
                    {
                        PooledConnection createdConnection = null;
                        try
                        {
                            createdConnection = await CreateConnectionAsync(PooledConnection.StateActive,
                                    operationToken)
                                .ConfigureAwait(false);
                            operationToken.ThrowIfCancellationRequested();
                            var lease = await CreateLeaseAsync(createdConnection, startTimestamp)
                                .ConfigureAwait(false);
                            createdConnection = null;
                            return lease;
                        }
                        catch (OperationCanceledException)
                        {
                            if (createdConnection == null)
                            {
                                Interlocked.Decrement(ref _totalConnections);
                                ReleaseWaiter();
                            }
                            else
                            {
                                await CloseActiveConnection(createdConnection, false, true).ConfigureAwait(false);
                            }

                            Interlocked.Increment(ref _creationFailureCount);
                            throw;
                        }
                        catch (Exception e)
                        {
                            lastCreationException = e;
                            if (createdConnection == null)
                            {
                                Interlocked.Decrement(ref _totalConnections);
                                ReleaseWaiter();
                            }
                            else
                            {
                                await CloseActiveConnection(createdConnection, false, true).ConfigureAwait(false);
                            }

                            Interlocked.Increment(ref _creationFailureCount);

                            if (backoff > TimeSpan.Zero)
                            {
                                await Task.Delay(GetJitteredBackoff(backoff), operationToken)
                                    .ConfigureAwait(false);
                                backoff = NextBackoff(backoff);
                            }
                            else
                            {
                                throw;
                            }

                            continue;
                        }
                    }

                    Interlocked.Increment(ref _awaitingConnections);
                    try
                    {
                        if (TryAcquireIdleConnection(out connection))
                        {
                            if (IsClientAvailable(connection) && !IsExpired(connection))
                            {
                                if (operationToken.IsCancellationRequested)
                                {
                                    await ReleaseCanceledActivatedConnectionAsync(connection, true)
                                        .ConfigureAwait(false);
                                    operationToken.ThrowIfCancellationRequested();
                                }

                                return await CreateLeaseAsync(connection, startTimestamp).ConfigureAwait(false);
                            }

                            await CloseActiveConnection(connection, IsExpired(connection), true)
                                .ConfigureAwait(false);
                            continue;
                        }

                        await _idleSignal.WaitAsync(operationToken).ConfigureAwait(false);
                        Interlocked.Decrement(ref _pendingWakeups);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _awaitingConnections);
                    }
                }
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
                if (Volatile.Read(ref _disposed) == 1 || _disposeCts.IsCancellationRequested)
                {
                    throw new ObjectDisposedException(nameof(WSClientAsyncPool));
                }

                if (callerToken.IsCancellationRequested)
                {
                    throw;
                }

                Interlocked.Increment(ref _acquireTimeoutCount);
                throw new TimeoutException(
                    "Timed out waiting for a WebSocket async connection from the pool.",
                    lastCreationException);
            }
        }

        private async Task<ITDengineClientAsync> CreateLeaseAsync(PooledConnection connection, long startTimestamp)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                await ReleaseCanceledActivatedConnectionAsync(connection, true).ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(WSClientAsyncPool));
            }

            var lease = new PoolLease(this, connection, _options.LeakDetectionThreshold > TimeSpan.Zero);
            _activeLeases.TryAdd(lease, 0);
            if (Volatile.Read(ref _disposed) == 1)
            {
                await lease.ForceCloseAsync(true).ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(WSClientAsyncPool));
            }

            RecordAcquireDuration(startTimestamp);
            Interlocked.Increment(ref _acquireCount);
            return new PooledTDengineClientAsync(lease);
        }

        private static ConnectionStringBuilder CloneBuilder(ConnectionStringBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            return builder.CreateSnapshot();
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
                await TryDisposeClientAsync(client, true).ConfigureAwait(false);
                throw;
            }
        }

        private async Task<PooledConnection> CreateConnectionWithTimeoutAsync(int initialState,
            CancellationToken cancellationToken)
        {
            using (var timeoutCts = new CancellationTokenSource())
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                       timeoutCts.Token))
            {
                timeoutCts.CancelAfter(_options.ConnectionTimeout);
                try
                {
                    return await CreateConnectionAsync(initialState, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested &&
                                                         !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Timed out creating a WebSocket async pool connection.");
                }
            }
        }

        private async Task<PooledConnection> CreateConnectionAsync(int initialState,
            CancellationToken cancellationToken)
        {
            ITDengineClientAsync client = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var clientTask = _clientFactory == null
                    ? CreateWebSocketClientAsync(_builder, cancellationToken)
                    : _clientFactory(cancellationToken);
                client = await AwaitClientFactoryAsync(clientTask, cancellationToken).ConfigureAwait(false);
                if (client == null)
                {
                    throw new InvalidOperationException("The WebSocket async pool client factory returned null.");
                }

                bool available;
                try
                {
                    available = client.ConnectionAvailable();
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException(
                        "The WebSocket async pool client factory returned a client whose availability check failed.",
                        e);
                }

                if (!available)
                {
                    throw new InvalidOperationException(
                        "The WebSocket async pool client factory returned an unavailable client.");
                }

                cancellationToken.ThrowIfCancellationRequested();

                var connection = new PooledConnection(client, initialState, GetJitteredLifetime());
                if (!_connections.TryAdd(connection, 0))
                {
                    throw new InvalidOperationException("Failed to register a WebSocket async pool connection.");
                }

                if (initialState == PooledConnection.StateActive)
                {
                    Interlocked.Increment(ref _activeConnections);
                }

                Interlocked.Increment(ref _creationCount);
                client = null;
                return connection;
            }
            catch
            {
                await TryDisposeClientAsync(client, true).ConfigureAwait(false);
                throw;
            }
        }

        private static async Task<ITDengineClientAsync> AwaitClientFactoryAsync(
            Task<ITDengineClientAsync> clientTask, CancellationToken cancellationToken)
        {
            if (clientTask == null)
            {
                throw new InvalidOperationException("The WebSocket async pool client factory returned a null task.");
            }

            if (clientTask.IsCompleted)
            {
                return await clientTask.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var cancellationCompletion = CreateBooleanCompletionSource();
            using (cancellationToken.Register(state =>
                       ((TaskCompletionSource<bool>)state).TrySetResult(true), cancellationCompletion))
            {
                var completedTask = await Task.WhenAny(clientTask, cancellationCompletion.Task)
                    .ConfigureAwait(false);
                if (completedTask == clientTask || clientTask.IsCompleted)
                {
                    return await clientTask.ConfigureAwait(false);
                }

                ObserveFaultedTask(DisposeLateClientWhenCompletedAsync(clientTask));
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private static async Task DisposeLateClientWhenCompletedAsync(Task<ITDengineClientAsync> clientTask)
        {
            try
            {
                var client = await clientTask.ConfigureAwait(false);
                await TryDisposeClientAsync(client, true).ConfigureAwait(false);
            }
            catch
            {
                // The original operation already observed cancellation. This path only
                // observes and cleans up a factory result that completed too late.
            }
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
            var cacheSlot = _threadCache.Value;
            connection = cacheSlot.GetTarget();
            if (TryActivateCachedConnection(connection))
            {
                cacheSlot.Clear();
                return true;
            }

            if (connection != null)
            {
                cacheSlot.Clear();
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
            if (Interlocked.CompareExchange(ref connection.ActiveCounted, 1, 0) != 0)
            {
                return false;
            }

            Interlocked.Increment(ref _activeConnections);
            if (Interlocked.CompareExchange(ref connection.State, PooledConnection.StateActive,
                    PooledConnection.StateIdle) != PooledConnection.StateIdle)
            {
                if (Interlocked.Exchange(ref connection.ActiveCounted, 0) == 1)
                {
                    Interlocked.Decrement(ref _activeConnections);
                }

                return false;
            }

            Interlocked.Decrement(ref _idleConnections);
            return true;
        }

        private async Task ReturnIdleConnectionAsync(PooledConnection connection, bool preferAsync)
        {
            Volatile.Write(ref connection.LastUsedTimestamp, Stopwatch.GetTimestamp());
            if (Interlocked.CompareExchange(ref connection.State, PooledConnection.StateIdle,
                    PooledConnection.StateActive) != PooledConnection.StateActive)
            {
                return;
            }

            if (Interlocked.Exchange(ref connection.ActiveCounted, 0) == 1)
            {
                Interlocked.Decrement(ref _activeConnections);
            }

            Interlocked.Increment(ref _idleConnections);
            if (!PublishIdleConnection(connection))
            {
                await CloseIdleConnectionAsync(connection, false, preferAsync).ConfigureAwait(false);
            }
        }

        private async Task ReturnMaintainedConnectionAsync(PooledConnection connection, bool preferAsync)
        {
            Volatile.Write(ref connection.LastUsedTimestamp, Stopwatch.GetTimestamp());
            if (Interlocked.CompareExchange(ref connection.State, PooledConnection.StateIdle,
                    PooledConnection.StateMaintenance) != PooledConnection.StateMaintenance)
            {
                return;
            }

            Interlocked.Decrement(ref _maintenanceConnections);
            Interlocked.Increment(ref _idleConnections);
            if (!PublishIdleConnection(connection))
            {
                await CloseIdleConnectionAsync(connection, false, preferAsync).ConfigureAwait(false);
            }
        }

        private bool PublishIdleConnection(PooledConnection connection)
        {
            lock (_disposeLock)
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    return false;
                }

                var cacheSlot = _threadCache.Value;
                var cached = cacheSlot.GetTarget();
                if (Volatile.Read(ref _awaitingConnections) == 0 &&
                    (cached == null || Volatile.Read(ref cached.State) != PooledConnection.StateIdle))
                {
                    cacheSlot.SetTarget(connection);
                    ReleaseWaiter();
                    return true;
                }

                if (Interlocked.Exchange(ref connection.Queued, 1) == 0)
                {
                    _idleQueue.Enqueue(connection);
                }

                ReleaseWaiter();

                return true;
            }
        }

        private async Task ReturnConnectionAsync(PoolLease lease, bool preferAsync)
        {
            _activeLeases.TryRemove(lease, out _);
            var connection = lease.Connection;

            if (Volatile.Read(ref _disposed) == 1 || lease.IsInvalidated || !IsClientAvailable(connection))
            {
                await CloseActiveConnection(connection, false, preferAsync).ConfigureAwait(false);
                return;
            }

            if (IsExpired(connection))
            {
                await CloseActiveConnection(connection, true, preferAsync).ConfigureAwait(false);
                return;
            }

            await ReturnIdleConnectionAsync(connection, preferAsync).ConfigureAwait(false);
        }

        private async Task ForceCloseLeaseAsync(PoolLease lease, bool preferAsync)
        {
            if (!_activeLeases.TryRemove(lease, out _))
            {
                return;
            }

            await CloseActiveConnection(lease.Connection, false, preferAsync).ConfigureAwait(false);
        }


        private async Task CloseActiveConnection(PooledConnection connection, bool recycled, bool preferAsync)
        {
            if (Interlocked.CompareExchange(ref connection.State, PooledConnection.StateClosed,
                    PooledConnection.StateActive) != PooledConnection.StateActive)
            {
                return;
            }

            Interlocked.Exchange(ref connection.Queued, 0);
            _connections.TryRemove(connection, out _);
            if (Interlocked.Exchange(ref connection.ActiveCounted, 0) == 1)
            {
                Interlocked.Decrement(ref _activeConnections);
            }
            Interlocked.Decrement(ref _totalConnections);
            Interlocked.Increment(ref _disposedConnectionCount);
            if (recycled)
            {
                Interlocked.Increment(ref _recycledConnectionCount);
            }

            try
            {
                await DisposeClientAsync(connection.Client, preferAsync).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                TracePoolFailure("WSClientAsyncPool failed to dispose active connection", e);
            }
            finally
            {
                ReleaseWaiter();
            }
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

            try
            {
                await DisposeClientAsync(connection.Client, preferAsync).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                TracePoolFailure("WSClientAsyncPool failed to dispose idle connection", e);
            }
            finally
            {
                ReleaseWaiter();
            }
        }

        private async Task ReleaseCanceledActivatedConnectionAsync(PooledConnection connection, bool preferAsync)
        {
            if (Volatile.Read(ref _disposed) == 1 || !IsClientAvailable(connection))
            {
                await CloseActiveConnection(connection, false, preferAsync).ConfigureAwait(false);
                return;
            }

            if (IsExpired(connection))
            {
                await CloseActiveConnection(connection, true, preferAsync).ConfigureAwait(false);
                return;
            }

            await ReturnIdleConnectionAsync(connection, preferAsync).ConfigureAwait(false);
        }

        private async Task HousekeepingLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RunHousekeepingAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    TracePoolFailure("WSClientAsyncPool housekeeping failed", e);
                }

                try
                {
                    await Task.Delay(_options.HousekeepingInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private async Task RunHousekeepingAsync(CancellationToken cancellationToken)
        {
            ReportLeakedConnections();
            await MaintainIdleConnectionsAsync(cancellationToken).ConfigureAwait(false);
            await EnsureMinIdleAsync(cancellationToken).ConfigureAwait(false);
            CompactIdleQueue();
        }

        private void CompactIdleQueue()
        {
            var maximumQueueEntries = _options.MaximumPoolSize > int.MaxValue / 2
                ? int.MaxValue
                : _options.MaximumPoolSize * 2;
            var queue = Volatile.Read(ref _idleQueue);
            if (queue.Count <= maximumQueueEntries)
            {
                return;
            }

            var requeuedConnections = 0;
            lock (_disposeLock)
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    return;
                }

                queue = Volatile.Read(ref _idleQueue);
                if (queue.Count <= maximumQueueEntries)
                {
                    return;
                }

                var replacement = new ConcurrentQueue<PooledConnection>();
                var seenConnections = new HashSet<PooledConnection>();
                var retiredQueue = Interlocked.Exchange(ref _idleQueue, replacement);
                while (retiredQueue.TryDequeue(out var connection))
                {
                    if (connection == null || !seenConnections.Add(connection) ||
                        Interlocked.CompareExchange(ref connection.Queued, 0, 1) != 1)
                    {
                        continue;
                    }

                    if (Volatile.Read(ref connection.State) == PooledConnection.StateClosed ||
                        !_connections.ContainsKey(connection) ||
                        Interlocked.CompareExchange(ref connection.Queued, 1, 0) != 0)
                    {
                        continue;
                    }

                    if (Volatile.Read(ref connection.State) == PooledConnection.StateClosed ||
                        !_connections.ContainsKey(connection))
                    {
                        Interlocked.CompareExchange(ref connection.Queued, 0, 1);
                        continue;
                    }

                    replacement.Enqueue(connection);
                    requeuedConnections++;
                }
            }

            for (var i = 0; i < requeuedConnections; i++)
            {
                ReleaseWaiter();
            }
        }

        private async Task MaintainIdleConnectionsAsync(CancellationToken cancellationToken)
        {
            List<Task> maintenanceTasks = null;
            foreach (var connection in _connections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pooledConnection = connection.Key;
                var expired = IsExpired(pooledConnection);
                var unavailable = !IsClientAvailable(pooledConnection);
                var keepaliveDue = _options.KeepaliveTime > TimeSpan.Zero &&
                                   GetElapsed(Volatile.Read(ref pooledConnection.LastUsedTimestamp)) >=
                                   _options.KeepaliveTime;
                if (!expired && !unavailable && !keepaliveDue)
                {
                    continue;
                }

                if (!TryClaimIdleForMaintenance(pooledConnection))
                {
                    continue;
                }

                if (maintenanceTasks == null)
                {
                    maintenanceTasks = new List<Task>();
                }

                maintenanceTasks.Add(MaintainIdleConnectionAsync(pooledConnection, expired || unavailable, expired,
                    cancellationToken));
            }

            if (maintenanceTasks != null)
            {
                await Task.WhenAll(maintenanceTasks).ConfigureAwait(false);
            }
        }

        private async Task CloseMaintenanceConnectionAsync(PooledConnection connection, bool recycled,
            bool preferAsync)
        {
            if (connection == null ||
                Interlocked.CompareExchange(ref connection.State, PooledConnection.StateClosed,
                    PooledConnection.StateMaintenance) != PooledConnection.StateMaintenance)
            {
                return;
            }

            Interlocked.Exchange(ref connection.Queued, 0);
            _connections.TryRemove(connection, out _);
            Interlocked.Decrement(ref _maintenanceConnections);
            Interlocked.Decrement(ref _totalConnections);
            Interlocked.Increment(ref _disposedConnectionCount);
            if (recycled)
            {
                Interlocked.Increment(ref _recycledConnectionCount);
            }

            try
            {
                await DisposeClientAsync(connection.Client, preferAsync).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                TracePoolFailure("WSClientAsyncPool failed to dispose maintenance connection", e);
            }
            finally
            {
                ReleaseWaiter();
            }
        }

        private async Task CleanupFailedCreationAsync(PooledConnection connection, bool preferAsync)
        {
            if (connection == null)
            {
                Interlocked.Decrement(ref _totalConnections);
                ReleaseWaiter();
                return;
            }

            switch (Volatile.Read(ref connection.State))
            {
                case PooledConnection.StateIdle:
                    await CloseIdleConnectionAsync(connection, false, preferAsync).ConfigureAwait(false);
                    break;
                case PooledConnection.StateMaintenance:
                    await CloseMaintenanceConnectionAsync(connection, false, preferAsync).ConfigureAwait(false);
                    break;
                case PooledConnection.StateActive:
                    await CloseActiveConnection(connection, false, preferAsync).ConfigureAwait(false);
                    break;
            }
        }

        private bool TryClaimIdleForMaintenance(PooledConnection connection)
        {
            if (connection == null ||
                Interlocked.CompareExchange(ref connection.State, PooledConnection.StateMaintenance,
                    PooledConnection.StateIdle) != PooledConnection.StateIdle)
            {
                return false;
            }

            Interlocked.Decrement(ref _idleConnections);
            Interlocked.Increment(ref _maintenanceConnections);
            return true;
        }

        private async Task MaintainIdleConnectionAsync(PooledConnection connection, bool closeImmediately,
            bool recycled, CancellationToken cancellationToken)
        {
            if (closeImmediately)
            {
                await CloseMaintenanceConnectionAsync(connection, recycled, true).ConfigureAwait(false);
                return;
            }

            Interlocked.Increment(ref _keepaliveCount);
            var valid = false;
            try
            {
                valid = await ValidateConnectionWithTimeoutAsync(connection.Client, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested ||
                                                     _disposeCts.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                TracePoolFailure("WSClientAsyncPool keepalive failed", e);
            }

            if (!valid)
            {
                Interlocked.Increment(ref _keepaliveFailureCount);
            }

            if (!valid || Volatile.Read(ref _disposed) == 1 || IsExpired(connection))
            {
                await CloseMaintenanceConnectionAsync(connection, IsExpired(connection), true)
                    .ConfigureAwait(false);
                return;
            }

            await ReturnMaintainedConnectionAsync(connection, true).ConfigureAwait(false);
        }

        private async Task<bool> ValidateConnectionWithTimeoutAsync(ITDengineClientAsync client,
            CancellationToken cancellationToken)
        {
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                       _disposeCts.Token))
            {
                linkedCts.CancelAfter(_options.ConnectionTimeout);
                var validationTask = _connectionValidator(client, linkedCts.Token);
                if (validationTask == null)
                {
                    throw new InvalidOperationException(
                        "The WebSocket async pool connection validator returned a null task.");
                }

                if (validationTask.IsCompleted)
                {
                    return await validationTask.ConfigureAwait(false);
                }

                var cancellationCompletion = CreateBooleanCompletionSource();
                using (linkedCts.Token.Register(state =>
                           ((TaskCompletionSource<bool>)state).TrySetResult(true), cancellationCompletion))
                {
                    var completedTask = await Task.WhenAny(validationTask, cancellationCompletion.Task)
                        .ConfigureAwait(false);
                    if (completedTask == validationTask || validationTask.IsCompleted)
                    {
                        return await validationTask.ConfigureAwait(false);
                    }

                    ObserveFaultedTask(validationTask);
                    if (cancellationToken.IsCancellationRequested || _disposeCts.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(linkedCts.Token);
                    }

                    return false;
                }
            }
        }

        private static TaskCompletionSource<bool> CreateBooleanCompletionSource()
        {
#if NET5_0_OR_GREATER || NETSTANDARD2_0_OR_GREATER
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#else
            return new TaskCompletionSource<bool>();
#endif
        }

        private static void ObserveFaultedTask(Task task)
        {
            task.ContinueWith(t => GC.KeepAlive(t.Exception), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static async Task<bool> ValidateClientAsync(ITDengineClientAsync client,
            CancellationToken cancellationToken)
        {
            var wsClient = client as WSClientAsync;
            if (wsClient != null)
            {
                return await wsClient.ValidateConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return client.ConnectionAvailable();
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
                    connection = await CreateConnectionWithTimeoutAsync(PooledConnection.StateActive, cancellationToken)
                        .ConfigureAwait(false);
                    await ReturnIdleConnectionAsync(connection, true).ConfigureAwait(false);
                    connection = null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await CleanupFailedCreationAsync(connection, true).ConfigureAwait(false);
                    throw;
                }
                catch (Exception e)
                {
                    await CleanupFailedCreationAsync(connection, true).ConfigureAwait(false);
                    Interlocked.Increment(ref _creationFailureCount);
                    TracePoolFailure("WSClientAsyncPool failed to create a minimum idle connection", e);
                    return;
                }
            }
        }

        private void ReportLeakedConnections()
        {
            if (_options.LeakDetectionThreshold <= TimeSpan.Zero)
            {
                return;
            }

            foreach (var lease in _activeLeases)
            {
                var activeLease = lease.Key;
                var elapsed = GetElapsed(activeLease.CheckoutTimestamp);
                if (elapsed < _options.LeakDetectionThreshold || !activeLease.TryMarkLeakReported())
                {
                    continue;
                }

                var args = new WSClientAsyncPoolLeakEventArgs(elapsed, activeLease.CheckoutStackTrace);
                var callback = _options.LeakDetected;
                try
                {
                    if (callback != null)
                    {
                        callback(args);
                    }
                    else
                    {
                        Trace.TraceWarning("Potential WSClientAsyncPool connection leak. Elapsed: " +
                                           elapsed + Environment.NewLine + activeLease.CheckoutStackTrace);
                    }
                }
                catch (Exception e)
                {
                    TracePoolFailure("WSClientAsyncPool leak detection callback failed", e);
                }
            }
        }

        private async Task DisposeCoreAsync(bool preferAsync)
        {
            Task disposeTask;
            lock (_disposeLock)
            {
                if (_disposeTask == null)
                {
                    _disposeTask = DisposeOnceAsync(preferAsync);
                }

                disposeTask = _disposeTask;
            }

            await disposeTask.ConfigureAwait(false);
        }

        private async Task DisposeOnceAsync(bool preferAsync)
        {
            lock (_disposeLock)
            {
                Interlocked.Exchange(ref _disposed, 1);
            }
            CancelDisposeToken();
            await WaitHousekeepingAsync().ConfigureAwait(false);
            await WaitForPoolOperationsAsync().ConfigureAwait(false);
            await CloseActiveLeasesAsync(preferAsync).ConfigureAwait(false);
            await WaitForPoolOperationsAsync().ConfigureAwait(false);
            await CloseIdleConnectionsAsync(preferAsync).ConfigureAwait(false);
            await CloseRemainingConnectionsAsync(preferAsync).ConfigureAwait(false);
            Interlocked.Exchange(ref _activeConnections, 0);
            Interlocked.Exchange(ref _idleConnections, 0);
            Interlocked.Exchange(ref _maintenanceConnections, 0);
            Interlocked.Exchange(ref _totalConnections, 0);
            Interlocked.Exchange(ref _awaitingConnections, 0);
            Interlocked.Exchange(ref _pendingWakeups, 0);
            _disposeCts.Dispose();
            _threadCache.Dispose();
            _idleSignal.Dispose();
            _operationsDrainedSignal.Dispose();
        }

        private void CancelDisposeToken()
        {
            try
            {
                _disposeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception e)
            {
                TracePoolFailure("WSClientAsyncPool dispose cancellation callback failed", e);
            }
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
            var closeTasks = new List<Task>();
            foreach (var connection in _connections)
            {
                var pooledConnection = connection.Key;
                switch (Volatile.Read(ref pooledConnection.State))
                {
                    case PooledConnection.StateIdle:
                        closeTasks.Add(CloseIdleConnectionAsync(pooledConnection, false, preferAsync));
                        break;
                    case PooledConnection.StateMaintenance:
                        closeTasks.Add(CloseMaintenanceConnectionAsync(pooledConnection, false, preferAsync));
                        break;
                    case PooledConnection.StateActive:
                        closeTasks.Add(CloseActiveConnection(pooledConnection, false, preferAsync));
                        break;
                }
            }

            if (closeTasks.Count != 0)
            {
                await Task.WhenAll(closeTasks).ConfigureAwait(false);
            }

            while (_idleQueue.TryDequeue(out var queuedConnection))
            {
                Interlocked.Exchange(ref queuedConnection.Queued, 0);
            }
        }

        private static async Task DisposeResourceAsync(IDisposable resource, bool preferAsync)
        {
            if (resource == null)
            {
                return;
            }

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
            if (preferAsync && resource is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                return;
            }
#endif
            resource.Dispose();
            await CompletedTask().ConfigureAwait(false);
        }

        private static async Task<bool> TryDisposeResourceAsync(IDisposable resource, bool preferAsync)
        {
            try
            {
                await DisposeResourceAsync(resource, preferAsync).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task CloseActiveLeasesAsync(bool preferAsync)
        {
            var closeTasks = new List<Task>();
            foreach (var lease in _activeLeases)
            {
                closeTasks.Add(lease.Key.ForceCloseAsync(preferAsync));
            }

            if (closeTasks.Count != 0)
            {
                await Task.WhenAll(closeTasks).ConfigureAwait(false);
            }
        }

        private async Task CloseRemainingConnectionsAsync(bool preferAsync)
        {
            List<Task> closeTasks = null;
            foreach (var item in _connections)
            {
                var connection = item.Key;
                if (!_connections.TryRemove(connection, out _))
                {
                    continue;
                }

                var previousState = Interlocked.Exchange(ref connection.State, PooledConnection.StateClosed);
                Interlocked.Exchange(ref connection.Queued, 0);
                if (Interlocked.Exchange(ref connection.ActiveCounted, 0) == 1)
                {
                    Interlocked.Decrement(ref _activeConnections);
                }

                switch (previousState)
                {
                    case PooledConnection.StateIdle:
                        Interlocked.Decrement(ref _idleConnections);
                        break;
                    case PooledConnection.StateMaintenance:
                        Interlocked.Decrement(ref _maintenanceConnections);
                        break;
                }

                if (previousState != PooledConnection.StateClosed)
                {
                    Interlocked.Decrement(ref _totalConnections);
                    Interlocked.Increment(ref _disposedConnectionCount);
                }

                if (closeTasks == null)
                {
                    closeTasks = new List<Task>();
                }

                closeTasks.Add(TryDisposeClientAsync(connection.Client, preferAsync));
            }

            if (closeTasks != null)
            {
                await Task.WhenAll(closeTasks).ConfigureAwait(false);
            }
        }

        private static bool IsClientAvailable(PooledConnection connection)
        {
            if (connection == null || connection.Client == null)
            {
                return false;
            }

            try
            {
                return connection.Client.ConnectionAvailable();
            }
            catch
            {
                return false;
            }
        }

        private bool IsExpired(PooledConnection connection)
        {
            return connection.MaxLifetime > TimeSpan.Zero &&
                   GetElapsed(connection.CreatedTimestamp) >= connection.MaxLifetime;
        }

        private TimeSpan GetJitteredLifetime()
        {
            var lifetime = _options.MaxLifetime;
            if (lifetime <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var maximumReduction = lifetime.Ticks / 40;
            if (maximumReduction <= 0)
            {
                return lifetime;
            }

            var reduction = (long)(maximumReduction * NextJitterFraction());
            return TimeSpan.FromTicks(lifetime.Ticks - reduction);
        }

        private static TimeSpan GetJitteredBackoff(TimeSpan backoff)
        {
            if (backoff <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var jitterRange = backoff.Ticks / 4;
            if (jitterRange <= 0)
            {
                return backoff;
            }

            var minimumTicks = backoff.Ticks - jitterRange;
            var jitterTicks = (long)(jitterRange * NextJitterFraction());
            return TimeSpan.FromTicks(minimumTicks + jitterTicks);
        }

        private static double NextJitterFraction()
        {
            var value = unchecked((uint)Interlocked.Add(ref _jitterState, unchecked((int)0x9e3779b9)));
            value ^= value >> 16;
            value *= 0x7feb352d;
            value ^= value >> 15;
            value *= 0x846ca68b;
            value ^= value >> 16;
            return value / ((double)uint.MaxValue + 1d);
        }

        private static void TracePoolFailure(string operation, Exception exception)
        {
            var error = exception as TDengineError;
            var description = error == null
                ? exception.GetType().Name
                : $"{exception.GetType().Name}/0x{error.Code:x}";
            Trace.TraceWarning(operation + ": " + description);
        }

        private TimeSpan NextBackoff(TimeSpan current)
        {
            if (current == TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var nextTicks = current.Ticks > long.MaxValue / 2 ? long.MaxValue : current.Ticks * 2;
            nextTicks = Math.Min(nextTicks, TimeoutHelper.MaximumTimerTimeout.Ticks);
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
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            while (true)
            {
                var awaiting = Volatile.Read(ref _awaitingConnections);
                var pendingWakeups = Volatile.Read(ref _pendingWakeups);
                if (awaiting <= pendingWakeups)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _pendingWakeups, pendingWakeups + 1, pendingWakeups) !=
                    pendingWakeups)
                {
                    continue;
                }

                try
                {
                    _idleSignal.Release();
                }
                catch (ObjectDisposedException)
                {
                    Interlocked.Decrement(ref _pendingWakeups);
                }
                catch (SemaphoreFullException)
                {
                    Interlocked.Decrement(ref _pendingWakeups);
                }

                return;
            }
        }

        private void BeginPoolOperation()
        {
            lock (_disposeLock)
            {
                ThrowIfDisposed();
                checked
                {
                    _poolOperations++;
                }
            }
        }

        private void BeginReturnOperation()
        {
            lock (_disposeLock)
            {
                checked
                {
                    _poolOperations++;
                }
            }
        }

        private void EndPoolOperation()
        {
            var signalDrained = false;
            lock (_disposeLock)
            {
                if (_poolOperations <= 0)
                {
                    Trace.TraceWarning("WSClientAsyncPool operation counter underflow was prevented.");
                    return;
                }

                _poolOperations--;
                signalDrained = _poolOperations == 0 && Volatile.Read(ref _disposed) != 0;
            }

            if (!signalDrained)
            {
                return;
            }

            try
            {
                _operationsDrainedSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task WaitForPoolOperationsAsync()
        {
            while (true)
            {
                lock (_disposeLock)
                {
                    if (_poolOperations == 0)
                    {
                        return;
                    }
                }

                await _operationsDrainedSignal.WaitAsync().ConfigureAwait(false);
            }
        }

        private async Task<bool> WaitForLeaseReturnAsync(Task returnTask)
        {
            if (returnTask.IsCompleted)
            {
                await returnTask.ConfigureAwait(false);
                return true;
            }

            using (var timeoutCts = new CancellationTokenSource())
            {
                var timeoutTask = Task.Delay(_options.ConnectionTimeout, timeoutCts.Token);
                if (await Task.WhenAny(returnTask, timeoutTask).ConfigureAwait(false) == returnTask)
                {
                    timeoutCts.Cancel();
                    await returnTask.ConfigureAwait(false);
                    return true;
                }
            }

            return false;
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

        private static async Task TryDisposeClientAsync(ITDengineClientAsync client, bool preferAsync)
        {
            try
            {
                await DisposeClientAsync(client, preferAsync).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                TracePoolFailure("WSClientAsyncPool failed to clean up a client", e);
            }
        }

        private static Task CompletedTask()
        {
#if NET45 || NET451
            return Task.FromResult(0);
#else
            return Task.CompletedTask;
#endif
        }

        private sealed class ThreadCacheSlot
        {
            private readonly WeakReference _reference = new WeakReference(null);

            internal PooledConnection GetTarget()
            {
                return _reference.Target as PooledConnection;
            }

            internal void SetTarget(PooledConnection connection)
            {
                _reference.Target = connection;
            }

            internal void Clear()
            {
                _reference.Target = null;
            }
        }

        private sealed class PooledConnection
        {
            internal const int StateIdle = 0;
            internal const int StateActive = 1;
            internal const int StateClosed = 2;
            internal const int StateMaintenance = 3;

            internal readonly ITDengineClientAsync Client;
            internal readonly long CreatedTimestamp;
            internal readonly TimeSpan MaxLifetime;
            internal long LastUsedTimestamp;
            internal int Queued;
            internal int State;
            internal int ActiveCounted;

            internal PooledConnection(ITDengineClientAsync client, int state, TimeSpan maxLifetime)
            {
                Client = client;
                CreatedTimestamp = Stopwatch.GetTimestamp();
                MaxLifetime = maxLifetime;
                LastUsedTimestamp = CreatedTimestamp;
                State = state;
                ActiveCounted = state == StateActive ? 1 : 0;
            }
        }

        private sealed class PoolLease
        {
            private readonly WSClientAsyncPool _pool;
            private readonly object _clientDisposeLock = new object();
            private readonly TaskCompletionSource<bool> _returnCompletion = CreateReturnCompletionSource();
            private int _references = 1;
            private int _clientDisposed;
            private int _returned;
            private int _invalidated;
            private int _leakReported;
            private int _forceCloseRequested;
            private Task _clientDisposeTask;

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

            internal bool IsInvalidated => Volatile.Read(ref _invalidated) == 1;

            internal void MarkConnectionInvalidated()
            {
                Volatile.Write(ref _invalidated, 1);
            }

            internal WebSocketState State
            {
                get
                {
                    if (Volatile.Read(ref _clientDisposed) == 1 || Volatile.Read(ref _returned) == 1)
                    {
                        return WebSocketState.Closed;
                    }

                    try
                    {
                        return Connection.Client.State;
                    }
                    catch
                    {
                        return WebSocketState.Closed;
                    }
                }
            }

            internal bool ConnectionAvailable()
            {
                if (Volatile.Read(ref _clientDisposed) != 0 || Volatile.Read(ref _returned) != 0)
                {
                    return false;
                }

                try
                {
                    return Connection.Client.ConnectionAvailable();
                }
                catch
                {
                    return false;
                }
            }

            internal void ThrowIfClientDisposed()
            {
                if (Volatile.Read(ref _clientDisposed) == 1 || Volatile.Read(ref _returned) == 1)
                {
                    throw new ObjectDisposedException(nameof(PooledTDengineClientAsync));
                }
            }

            internal void ThrowIfReturned(string objectName)
            {
                if (Volatile.Read(ref _returned) == 1 || Volatile.Read(ref _forceCloseRequested) == 1)
                {
                    throw new ObjectDisposedException(objectName);
                }
            }

            internal void ThrowIfReturnedAfterOperation(string objectName)
            {
                if (Volatile.Read(ref _returned) == 1)
                {
                    throw new ObjectDisposedException(objectName);
                }
            }

            internal void AddClientOperationReference()
            {
                lock (_clientDisposeLock)
                {
                    ThrowIfClientDisposed();
                    AddReference();
                }
            }

            internal void AddChildReference()
            {
                // Coordinate with ForceCloseAsync so a child operation cannot
                // acquire a reference after the lease has started draining.
                lock (_clientDisposeLock)
                {
                    AddReference();
                }
            }

            private void AddReference()
            {
                while (true)
                {
                    var current = Volatile.Read(ref _references);
                    if (current <= 0 || Volatile.Read(ref _returned) == 1 ||
                        Volatile.Read(ref _forceCloseRequested) == 1)
                    {
                        throw new ObjectDisposedException(nameof(PooledTDengineClientAsync));
                    }

                    if (current == int.MaxValue)
                    {
                        throw new InvalidOperationException("The WebSocket async pool lease reference limit was reached.");
                    }

                    if (Interlocked.CompareExchange(ref _references, current + 1, current) == current)
                    {
                        return;
                    }
                }
            }

            internal async Task ReleaseReferenceAsync(bool preferAsync)
            {
                while (true)
                {
                    var current = Volatile.Read(ref _references);
                    if (current <= 0)
                    {
                        return;
                    }

                    if (Interlocked.CompareExchange(ref _references, current - 1, current) == current)
                    {
                        if (current != 1)
                        {
                            return;
                        }

                        break;
                    }
                }

                _pool.BeginReturnOperation();
                var ownsReturn = false;
                try
                {
                    if (Interlocked.Exchange(ref _returned, 1) == 1)
                    {
                        return;
                    }

                    ownsReturn = true;
                    await _pool.ReturnConnectionAsync(this, preferAsync).ConfigureAwait(false);
                }
                finally
                {
                    if (ownsReturn)
                    {
                        _returnCompletion.TrySetResult(true);
                    }

                    _pool.EndPoolOperation();
                }
            }

            internal async Task ForceCloseAsync(bool preferAsync)
            {
                Volatile.Write(ref _forceCloseRequested, 1);
                Task rootReleaseTask;
                lock (_clientDisposeLock)
                {
                    Interlocked.Exchange(ref _clientDisposed, 1);
                    if (_clientDisposeTask == null)
                    {
                        _clientDisposeTask = ReleaseReferenceAsync(preferAsync);
                    }

                    rootReleaseTask = _clientDisposeTask;
                }

                await rootReleaseTask.ConfigureAwait(false);
                if (await _pool.WaitForLeaseReturnAsync(_returnCompletion.Task).ConfigureAwait(false))
                {
                    return;
                }

                Trace.TraceWarning(
                    "WSClientAsyncPool timed out waiting for an active lease to drain; the connection will be closed.");
                await ForceCloseNowAsync(preferAsync).ConfigureAwait(false);
                await _returnCompletion.Task.ConfigureAwait(false);
            }

            private async Task ForceCloseNowAsync(bool preferAsync)
            {
                Interlocked.Exchange(ref _references, 0);
                if (Interlocked.Exchange(ref _returned, 1) == 1)
                {
                    return;
                }

                _pool.BeginReturnOperation();
                try
                {
                    await _pool.ForceCloseLeaseAsync(this, preferAsync).ConfigureAwait(false);
                }
                finally
                {
                    _returnCompletion.TrySetResult(true);
                    _pool.EndPoolOperation();
                }
            }

            internal void DisposeClient()
            {
                GetClientDisposeTask(false).GetAwaiter().GetResult();
            }

            internal async Task DisposeClientAsync()
            {
                await GetClientDisposeTask(true).ConfigureAwait(false);
            }

            private Task GetClientDisposeTask(bool preferAsync)
            {
                lock (_clientDisposeLock)
                {
                    if (_clientDisposeTask == null)
                    {
                        Interlocked.Exchange(ref _clientDisposed, 1);
                        _clientDisposeTask = ReleaseReferenceAsync(preferAsync);
                    }

                    return _clientDisposeTask;
                }
            }

            private static TaskCompletionSource<bool> CreateReturnCompletionSource()
            {
#if NET5_0_OR_GREATER || NETSTANDARD2_0_OR_GREATER
                return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#else
                return new TaskCompletionSource<bool>();
#endif
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
                reqId = ReqId.Normalize(reqId, nameof(reqId));
                _lease.AddClientOperationReference();
                var childReferenceAdded = false;
                IStmtAsync stmt = null;
                try
                {
                    stmt = await _lease.Connection.Client.StmtInitAsync(reqId, cancellationToken)
                        .ConfigureAwait(false);
                    if (stmt == null)
                    {
                        throw new InvalidOperationException("The WebSocket async client returned a null statement.");
                    }

                    _lease.AddChildReference();
                    childReferenceAdded = true;
                    var pooledStmt = new PooledStmtAsync(stmt, _lease);
                    stmt = null;
                    return pooledStmt;
                }
                catch
                {
                    if (stmt != null)
                    {
                        if (!await TryDisposeResourceAsync(stmt, true).ConfigureAwait(false))
                        {
                            _lease.MarkConnectionInvalidated();
                        }
                    }

                    if (childReferenceAdded)
                    {
                        await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                    }

                    throw;
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
                reqId = ReqId.Normalize(reqId, nameof(reqId));
                _lease.AddClientOperationReference();
                var childReferenceAdded = false;
                IRowsAsync rows = null;
                try
                {
                    rows = await _lease.Connection.Client.QueryAsync(query, reqId, cancellationToken)
                        .ConfigureAwait(false);
                    if (rows == null)
                    {
                        throw new InvalidOperationException("The WebSocket async client returned a null rows object.");
                    }

                    _lease.AddChildReference();
                    childReferenceAdded = true;
                    var pooledRows = new PooledRowsAsync(rows, _lease);
                    rows = null;
                    return pooledRows;
                }
                catch
                {
                    if (rows != null)
                    {
                        if (!await TryDisposeResourceAsync(rows, true).ConfigureAwait(false))
                        {
                            _lease.MarkConnectionInvalidated();
                        }
                    }

                    if (childReferenceAdded)
                    {
                        await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                    }

                    throw;
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
                reqId = ReqId.Normalize(reqId, nameof(reqId));
                _lease.AddClientOperationReference();
                try
                {
                    var affected = await _lease.Connection.Client.ExecAsync(query, reqId, cancellationToken)
                        .ConfigureAwait(false);
                    _lease.ThrowIfReturnedAfterOperation(nameof(PooledTDengineClientAsync));
                    return affected;
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
                reqId = ReqId.Normalize(reqId, nameof(reqId));
                _lease.AddClientOperationReference();
                try
                {
                    await _lease.Connection.Client.SchemalessInsertAsync(lines, protocol, precision, ttl, reqId,
                        cancellationToken).ConfigureAwait(false);
                    _lease.ThrowIfReturnedAfterOperation(nameof(PooledTDengineClientAsync));
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
            private const int SynchronousAccessDisposedMask = int.MinValue;
            private const int SynchronousAccessCountMask = int.MaxValue;
            private readonly IRowsAsync _inner;
            private readonly PoolLease _lease;
            private readonly object _disposeLock = new object();
            private int _disposed;
            private int _synchronousAccessState;
            private Task _disposeTask;
            private TaskCompletionSource<bool> _synchronousAccessorsDrained;

            internal PooledRowsAsync(IRowsAsync inner, PoolLease lease)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _lease = lease ?? throw new ArgumentNullException(nameof(lease));
            }

            public bool HasRows
            {
                get
                {
                    using (EnterSynchronousAccess())
                    {
                        return _inner.HasRows;
                    }
                }
            }

            public int AffectRows
            {
                get
                {
                    using (EnterSynchronousAccess())
                    {
                        return _inner.AffectRows;
                    }
                }
            }

            public int FieldCount
            {
                get
                {
                    using (EnterSynchronousAccess())
                    {
                        return _inner.FieldCount;
                    }
                }
            }

            public long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
                }
            }

            public char GetChar(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetChar(ordinal);
                }
            }

            public long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
                }
            }

            public string GetDataTypeName(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetDataTypeName(ordinal);
                }
            }

            public object GetValue(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetValue(ordinal);
                }
            }

            public Type GetFieldType(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetFieldType(ordinal);
                }
            }

            public int GetFieldSize(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetFieldSize(ordinal);
                }
            }

            public string GetName(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetName(ordinal);
                }
            }

            public int GetFieldPrecision(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetFieldPrecision(ordinal);
                }
            }

            public int GetFieldScale(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetFieldScale(ordinal);
                }
            }

            public int GetOrdinal(string name)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetOrdinal(name);
                }
            }

            public Task<bool> ReadAsync()
            {
                return ReadAsync(CancellationToken.None);
            }

            public async Task<bool> ReadAsync(CancellationToken cancellationToken)
            {
                EnsureUsable();
                _lease.AddChildReference();
                try
                {
                    EnsureUsable();
                    var hasRows = await _inner.ReadAsync(cancellationToken).ConfigureAwait(false);
                    EnsureUsableAfterOperation();
                    return hasRows;
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }

            public bool IsDBNull(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.IsDBNull(ordinal);
                }
            }

            public byte GetByte(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetByte(ordinal);
                }
            }

            public short GetInt16(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetInt16(ordinal);
                }
            }

            public int GetInt32(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetInt32(ordinal);
                }
            }

            public long GetInt64(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetInt64(ordinal);
                }
            }

            public bool GetBoolean(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetBoolean(ordinal);
                }
            }

            public DateTime GetDateTime(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetDateTime(ordinal);
                }
            }

            public decimal GetDecimal(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetDecimal(ordinal);
                }
            }

            public double GetDouble(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetDouble(ordinal);
                }
            }

            public float GetFloat(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetFloat(ordinal);
                }
            }

            public string GetString(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetString(ordinal);
                }
            }

            public int GetValues(object[] values)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetValues(values);
                }
            }

            public DateTimeOffset GetDateTimeOffset(int ordinal)
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.GetDateTimeOffset(ordinal);
                }
            }

            private SynchronousAccessScope EnterSynchronousAccess()
            {
                while (true)
                {
                    EnsureUsable();
                    var state = Volatile.Read(ref _synchronousAccessState);
                    if ((state & SynchronousAccessDisposedMask) != 0)
                    {
                        throw new ObjectDisposedException(nameof(PooledRowsAsync));
                    }

                    if ((state & SynchronousAccessCountMask) == SynchronousAccessCountMask)
                    {
                        throw new InvalidOperationException(
                            "The pooled rows synchronous accessor limit was reached.");
                    }

                    if (Interlocked.CompareExchange(ref _synchronousAccessState, state + 1, state) == state)
                    {
                        return new SynchronousAccessScope(this);
                    }
                }
            }

            private void ExitSynchronousAccess()
            {
                var state = Interlocked.Decrement(ref _synchronousAccessState);
                if (state == SynchronousAccessDisposedMask)
                {
                    Volatile.Read(ref _synchronousAccessorsDrained)?.TrySetResult(true);
                }
            }

            private Task MarkDisposedAndGetSynchronousDrainTask()
            {
                Volatile.Write(ref _disposed, 1);
                TaskCompletionSource<bool> drained = null;
                while (true)
                {
                    var state = Volatile.Read(ref _synchronousAccessState);
                    if ((state & SynchronousAccessDisposedMask) != 0)
                    {
                        return (state & SynchronousAccessCountMask) == 0
                            ? CompletedTask()
                            : Volatile.Read(ref _synchronousAccessorsDrained).Task;
                    }

                    if ((state & SynchronousAccessCountMask) == 0)
                    {
                        if (Interlocked.CompareExchange(ref _synchronousAccessState,
                                SynchronousAccessDisposedMask, state) == state)
                        {
                            return CompletedTask();
                        }

                        continue;
                    }

                    if (drained == null)
                    {
                        drained = CreateSynchronousAccessCompletionSource();
                        Volatile.Write(ref _synchronousAccessorsDrained, drained);
                    }

                    var disposedState = state | SynchronousAccessDisposedMask;
                    if (Interlocked.CompareExchange(ref _synchronousAccessState, disposedState, state) != state)
                    {
                        continue;
                    }

                    if ((state & SynchronousAccessCountMask) == 0)
                    {
                        drained.TrySetResult(true);
                        return CompletedTask();
                    }

                    return drained.Task;
                }
            }

            private static TaskCompletionSource<bool> CreateSynchronousAccessCompletionSource()
            {
#if NET5_0_OR_GREATER || NETSTANDARD2_0_OR_GREATER
                return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#else
                return new TaskCompletionSource<bool>();
#endif
            }

            private void EnsureUsable()
            {
                ThrowIfDisposed();
                _lease.ThrowIfReturned(nameof(PooledRowsAsync));
            }

            private void EnsureUsableAfterOperation()
            {
                ThrowIfDisposed();
                _lease.ThrowIfReturnedAfterOperation(nameof(PooledRowsAsync));
            }

            private void ThrowIfDisposed()
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    throw new ObjectDisposedException(nameof(PooledRowsAsync));
                }
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

            private Task DisposeCoreAsync(bool preferAsync)
            {
                lock (_disposeLock)
                {
                    if (_disposeTask == null)
                    {
                        _disposeTask = DisposeOnceAsync(preferAsync);
                    }

                    return _disposeTask;
                }
            }

            private async Task DisposeOnceAsync(bool preferAsync)
            {
                await MarkDisposedAndGetSynchronousDrainTask().ConfigureAwait(false);
                try
                {
                    await DisposeResourceAsync(_inner, preferAsync).ConfigureAwait(false);
                }
                catch
                {
                    _lease.MarkConnectionInvalidated();
                    throw;
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(preferAsync).ConfigureAwait(false);
                }
            }

            private struct SynchronousAccessScope : IDisposable
            {
                private readonly PooledRowsAsync _owner;

                internal SynchronousAccessScope(PooledRowsAsync owner)
                {
                    _owner = owner;
                }

                public void Dispose()
                {
                    _owner.ExitSynchronousAccess();
                }
            }
        }

        private sealed class PooledStmtAsync : IStmtAsync
        {
            private const int SynchronousAccessDisposedMask = int.MinValue;
            private const int SynchronousAccessCountMask = int.MaxValue;
            private readonly IStmtAsync _inner;
            private readonly PoolLease _lease;
            private readonly object _disposeLock = new object();
            private int _disposed;
            private int _synchronousAccessState;
            private Task _disposeTask;
            private TaskCompletionSource<bool> _synchronousAccessorsDrained;

            internal PooledStmtAsync(IStmtAsync inner, PoolLease lease)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _lease = lease ?? throw new ArgumentNullException(nameof(lease));
            }

            public Task PrepareAsync(string query)
            {
                return PrepareAsync(query, CancellationToken.None);
            }

            public async Task PrepareAsync(string query, CancellationToken cancellationToken)
            {
                ThrowIfDisposed();
                _lease.AddChildReference();
                try
                {
                    ThrowIfDisposed();
                    await _inner.PrepareAsync(query, cancellationToken).ConfigureAwait(false);
                    ThrowIfDisposedAfterOperation();
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }

            public bool IsInsert()
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.IsInsert();
                }
            }

            public Task SetTableNameAsync(string tableName)
            {
                return SetTableNameAsync(tableName, CancellationToken.None);
            }

            public async Task SetTableNameAsync(string tableName, CancellationToken cancellationToken)
            {
                ThrowIfDisposed();
                _lease.AddChildReference();
                try
                {
                    ThrowIfDisposed();
                    await _inner.SetTableNameAsync(tableName, cancellationToken).ConfigureAwait(false);
                    ThrowIfDisposedAfterOperation();
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }

            public Task SetTagsAsync(object[] tags)
            {
                return SetTagsAsync(tags, CancellationToken.None);
            }

            public async Task SetTagsAsync(object[] tags, CancellationToken cancellationToken)
            {
                ThrowIfDisposed();
                _lease.AddChildReference();
                try
                {
                    ThrowIfDisposed();
                    await _inner.SetTagsAsync(tags, cancellationToken).ConfigureAwait(false);
                    ThrowIfDisposedAfterOperation();
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }

            public Task<TaosFieldE[]> GetTagFieldsAsync()
            {
                return GetTagFieldsAsync(CancellationToken.None);
            }

            public Task<TaosFieldE[]> GetTagFieldsAsync(CancellationToken cancellationToken)
            {
                return GetTagFieldsCoreAsync(cancellationToken);
            }

            public Task<TaosFieldE[]> GetColFieldsAsync()
            {
                return GetColFieldsAsync(CancellationToken.None);
            }

            public Task<TaosFieldE[]> GetColFieldsAsync(CancellationToken cancellationToken)
            {
                return GetColFieldsCoreAsync(cancellationToken);
            }

            private async Task<TaosFieldE[]> GetTagFieldsCoreAsync(CancellationToken cancellationToken)
            {
                ThrowIfDisposed();
                _lease.AddChildReference();
                try
                {
                    ThrowIfDisposed();
                    var fields = await _inner.GetTagFieldsAsync(cancellationToken).ConfigureAwait(false);
                    ThrowIfDisposedAfterOperation();
                    return fields;
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }

            private async Task<TaosFieldE[]> GetColFieldsCoreAsync(CancellationToken cancellationToken)
            {
                ThrowIfDisposed();
                _lease.AddChildReference();
                try
                {
                    ThrowIfDisposed();
                    var fields = await _inner.GetColFieldsAsync(cancellationToken).ConfigureAwait(false);
                    ThrowIfDisposedAfterOperation();
                    return fields;
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }

            public Task BindRowAsync(object[] row)
            {
                return BindRowAsync(row, CancellationToken.None);
            }

            public async Task BindRowAsync(object[] row, CancellationToken cancellationToken)
            {
                ThrowIfDisposed();
                _lease.AddChildReference();
                try
                {
                    ThrowIfDisposed();
                    await _inner.BindRowAsync(row, cancellationToken).ConfigureAwait(false);
                    ThrowIfDisposedAfterOperation();
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }

            public Task BindColumnAsync(TaosFieldE[] fields, params Array[] arrays)
            {
                return BindColumnAsync(fields, CancellationToken.None, arrays);
            }

            public async Task BindColumnAsync(TaosFieldE[] fields, CancellationToken cancellationToken,
                params Array[] arrays)
            {
                ThrowIfDisposed();
                _lease.AddChildReference();
                try
                {
                    ThrowIfDisposed();
                    await _inner.BindColumnAsync(fields, cancellationToken, arrays).ConfigureAwait(false);
                    ThrowIfDisposedAfterOperation();
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }

            public Task AddBatchAsync()
            {
                return AddBatchAsync(CancellationToken.None);
            }

            public async Task AddBatchAsync(CancellationToken cancellationToken)
            {
                ThrowIfDisposed();
                _lease.AddChildReference();
                try
                {
                    ThrowIfDisposed();
                    await _inner.AddBatchAsync(cancellationToken).ConfigureAwait(false);
                    ThrowIfDisposedAfterOperation();
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }

            public Task ExecAsync()
            {
                return ExecAsync(CancellationToken.None);
            }

            public async Task ExecAsync(CancellationToken cancellationToken)
            {
                ThrowIfDisposed();
                _lease.AddChildReference();
                try
                {
                    ThrowIfDisposed();
                    await _inner.ExecAsync(cancellationToken).ConfigureAwait(false);
                    ThrowIfDisposedAfterOperation();
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }

            public long Affected()
            {
                using (EnterSynchronousAccess())
                {
                    return _inner.Affected();
                }
            }

            private SynchronousAccessScope EnterSynchronousAccess()
            {
                while (true)
                {
                    ThrowIfDisposed();
                    var state = Volatile.Read(ref _synchronousAccessState);
                    if ((state & SynchronousAccessDisposedMask) != 0)
                    {
                        throw new ObjectDisposedException(nameof(PooledStmtAsync));
                    }

                    if ((state & SynchronousAccessCountMask) == SynchronousAccessCountMask)
                    {
                        throw new InvalidOperationException(
                            "The pooled statement synchronous accessor limit was reached.");
                    }

                    if (Interlocked.CompareExchange(ref _synchronousAccessState, state + 1, state) == state)
                    {
                        return new SynchronousAccessScope(this);
                    }
                }
            }

            private void ExitSynchronousAccess()
            {
                var state = Interlocked.Decrement(ref _synchronousAccessState);
                if (state == SynchronousAccessDisposedMask)
                {
                    Volatile.Read(ref _synchronousAccessorsDrained)?.TrySetResult(true);
                }
            }

            private Task MarkDisposedAndGetSynchronousDrainTask()
            {
                Volatile.Write(ref _disposed, 1);
                TaskCompletionSource<bool> drained = null;
                while (true)
                {
                    var state = Volatile.Read(ref _synchronousAccessState);
                    if ((state & SynchronousAccessDisposedMask) != 0)
                    {
                        return (state & SynchronousAccessCountMask) == 0
                            ? CompletedTask()
                            : Volatile.Read(ref _synchronousAccessorsDrained).Task;
                    }

                    if ((state & SynchronousAccessCountMask) == 0)
                    {
                        if (Interlocked.CompareExchange(ref _synchronousAccessState,
                                SynchronousAccessDisposedMask, state) == state)
                        {
                            return CompletedTask();
                        }

                        continue;
                    }

                    if (drained == null)
                    {
                        drained = CreateSynchronousAccessCompletionSource();
                        Volatile.Write(ref _synchronousAccessorsDrained, drained);
                    }

                    var disposedState = state | SynchronousAccessDisposedMask;
                    if (Interlocked.CompareExchange(ref _synchronousAccessState, disposedState, state) != state)
                    {
                        continue;
                    }

                    if ((state & SynchronousAccessCountMask) == 0)
                    {
                        drained.TrySetResult(true);
                        return CompletedTask();
                    }

                    return drained.Task;
                }
            }

            private static TaskCompletionSource<bool> CreateSynchronousAccessCompletionSource()
            {
#if NET5_0_OR_GREATER || NETSTANDARD2_0_OR_GREATER
                return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#else
                return new TaskCompletionSource<bool>();
#endif
            }

            public Task<IRowsAsync> ResultAsync()
            {
                return ResultAsync(CancellationToken.None);
            }

            public async Task<IRowsAsync> ResultAsync(CancellationToken cancellationToken)
            {
                ThrowIfDisposed();
                _lease.AddChildReference();
                var resultReferenceAdded = false;
                IRowsAsync rows = null;
                try
                {
                    ThrowIfDisposed();
                    rows = await _inner.ResultAsync(cancellationToken).ConfigureAwait(false);
                    ThrowIfDisposedAfterOperation();
                    if (rows == null)
                    {
                        throw new InvalidOperationException("The WebSocket async statement returned a null rows object.");
                    }

                    _lease.AddChildReference();
                    resultReferenceAdded = true;
                    var pooledRows = new PooledRowsAsync(rows, _lease);
                    rows = null;
                    return pooledRows;
                }
                catch
                {
                    if (rows != null)
                    {
                        if (!await TryDisposeResourceAsync(rows, true).ConfigureAwait(false))
                        {
                            _lease.MarkConnectionInvalidated();
                        }
                    }

                    if (resultReferenceAdded)
                    {
                        await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                    }

                    throw;
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(true).ConfigureAwait(false);
                }
            }

            private void ThrowIfDisposed()
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    throw new ObjectDisposedException(nameof(PooledStmtAsync));
                }

                _lease.ThrowIfReturned(nameof(PooledStmtAsync));
            }

            private void ThrowIfDisposedAfterOperation()
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    throw new ObjectDisposedException(nameof(PooledStmtAsync));
                }

                _lease.ThrowIfReturnedAfterOperation(nameof(PooledStmtAsync));
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

            private Task DisposeCoreAsync(bool preferAsync)
            {
                lock (_disposeLock)
                {
                    if (_disposeTask == null)
                    {
                        _disposeTask = DisposeOnceAsync(preferAsync);
                    }

                    return _disposeTask;
                }
            }

            private async Task DisposeOnceAsync(bool preferAsync)
            {
                await MarkDisposedAndGetSynchronousDrainTask().ConfigureAwait(false);
                try
                {
                    await DisposeResourceAsync(_inner, preferAsync).ConfigureAwait(false);
                }
                catch
                {
                    _lease.MarkConnectionInvalidated();
                    throw;
                }
                finally
                {
                    await _lease.ReleaseReferenceAsync(preferAsync).ConfigureAwait(false);
                }
            }

            private struct SynchronousAccessScope : IDisposable
            {
                private readonly PooledStmtAsync _owner;

                internal SynchronousAccessScope(PooledStmtAsync owner)
                {
                    _owner = owner;
                }

                public void Dispose()
                {
                    _owner.ExitSynchronousAccess();
                }
            }
        }
    }
}
