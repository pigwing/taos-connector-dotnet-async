using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;

namespace TDengine.Driver.Client.Websocket
{
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
    public class WSClientAsync : ITDengineClientAsync, IAsyncDisposable
#else
    public class WSClientAsync : ITDengineClientAsync, IDisposable
#endif
    {
        private volatile ConnectionAsync _connection;
        private volatile FailoverAddressLease _addressLease;
        private int _disposed;
        private readonly TimeZoneInfo _tz;
        private readonly ConnectionStringBuilder _builder;
        private List<FailoverAddress> _failoverAddresses;
        private readonly SemaphoreSlim _reconnectLock = new SemaphoreSlim(1, 1);
        private readonly object _addressLock = new object();
        private readonly object _disposeLock = new object();
        private readonly object _disposeCtsLock = new object();
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        private readonly CancellationToken _disposeToken;
        private Task _disposeTask;
        private int _disposeOperationCount;
        private int _disposeCtsDisposed;

        internal bool AutoReconnect => _builder.AutoReconnect;
        internal bool AdapterHA => _builder.AdapterHA;
        public WebSocketState State
        {
            get
            {
                if (IsDisposed())
                {
                    return WebSocketState.Closed;
                }

                var connection = _connection;
                if (connection == null)
                {
                    return WebSocketState.Closed;
                }

                try
                {
                    return connection.State;
                }
                catch (ObjectDisposedException)
                {
                    return WebSocketState.Closed;
                }
            }
        }

        public WSClientAsync(ConnectionStringBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (builder.Protocol != TDengineConstant.ProtocolWebSocket)
            {
                throw new ArgumentException("WSClientAsync only supports WebSocket protocol.", nameof(builder));
            }

            Debug.Assert(builder.Protocol == TDengineConstant.ProtocolWebSocket);
            _disposeToken = _disposeCts.Token;
            _builder = builder.CreateSnapshot();
            _tz = _builder.GetTimeZone();
            var seedAddresses = _builder.GetFailoverAddresses();
            IReadOnlyList<FailoverAddress> initialAddresses = seedAddresses;
            if (AdapterHA)
            {
                initialAddresses = AdapterClusterRegistry.ExpandIfKnown(seedAddresses);
            }

            _failoverAddresses = new List<FailoverAddress>(initialAddresses);
        }

        public static string GetUrl(ConnectionStringBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            var addresses = builder.GetFailoverAddresses();
            if (addresses.Count == 0)
            {
                throw new ArgumentException("failover addresses is empty", nameof(builder));
            }

            var address = addresses[0];
            return GetUrl(builder, address.Host, address.Port);
        }

        internal static string GetUrl(ConnectionStringBuilder builder, string host, int port)
        {
            var schema = builder.UseSSL ? "wss" : "ws";
            if (port == 0)
            {
                port = builder.UseSSL ? 443 : 6041;
            }

            var uriBuilder = new UriBuilder
            {
                Scheme = schema,
                Host = host,
                Port = port,
                Path = "/ws"
            };

            if (!string.IsNullOrEmpty(builder.Token))
            {
                uriBuilder.Query = "token=" + Uri.EscapeDataString(builder.Token);
            }

            return uriBuilder.ToString();
        }

        private ConnectionAsync CreateConnection(FailoverAddress address)
        {
            return new ConnectionAsync(GetUrl(_builder, address.Host, address.Port), _builder.Username,
                _builder.Password, _builder.Database, _builder.BearerToken, _builder.ConnTimeout,
                _builder.ReadTimeout, _builder.WriteTimeout, _builder.EnableCompression,
                _builder.ConnectionTimezone);
        }

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
        public async ValueTask DisposeAsync()
        {
            await DisposeCoreAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            DisposeCoreAsync().GetAwaiter().GetResult();
        }
#else
        public void Dispose()
        {
            DisposeCoreAsync().GetAwaiter().GetResult();
        }
#endif

        private async Task DisposeCoreAsync()
        {
            Task disposeTask;
            lock (_disposeLock)
            {
                if (_disposeTask == null)
                {
                    _disposeTask = DisposeOnceAsync();
                }

                disposeTask = _disposeTask;
            }

            await disposeTask.ConfigureAwait(false);
        }

        private async Task DisposeOnceAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            try
            {
                _disposeCts.Cancel();
            }
            catch (Exception e)
            {
                Trace.TraceWarning("WSClientAsync dispose cancellation callback failed: " + e.GetType().Name);
            }

            await _reconnectLock.WaitAsync().ConfigureAwait(false);
            ConnectionAsync oldConnection;
            FailoverAddressLease oldLease;
            try
            {
                oldConnection = _connection;
                oldLease = _addressLease;
                _connection = null;
                _addressLease = null;
            }
            finally
            {
                _reconnectLock.Release();
            }

            try
            {
                if (oldConnection != null)
                {
                    await oldConnection.CloseAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                oldLease?.Dispose();
                TryDisposeDisposeToken();
            }
        }

        private bool IsDisposed()
        {
            return Volatile.Read(ref _disposed) == 1;
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposed())
            {
                throw new ObjectDisposedException(nameof(WSClientAsync));
            }
        }

        private bool ShouldRetryRequest(Exception exception, ConnectionAsync attemptedConnection)
        {
            if (!AutoReconnect || IsDisposed() || exception is OperationCanceledException)
            {
                return false;
            }

            if (exception is TDengineWebSocketRequestException requestException)
            {
                return !requestException.RequestMayHaveBeenSent &&
                       (attemptedConnection == null || !attemptedConnection.IsAvailable());
            }

            return attemptedConnection == null && exception is ObjectDisposedException;
        }

        public async Task ConnectAsync()
        {
            await ConnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            return ConnectCoreAsync(cancellationToken);
        }

        private async Task ConnectCoreAsync(CancellationToken cancellationToken)
        {
            if (!TryEnterDisposeOperation())
            {
                throw new ObjectDisposedException(nameof(WSClientAsync));
            }

            try
            {
                ThrowIfDisposed();
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeToken))
                {
                    await _reconnectLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                    try
                    {
                        ThrowIfDisposed();
                        if (_connection != null && _connection.IsAvailable())
                        {
                            return;
                        }

                        var context = new TryOpenContext();
                        if (!await TryOpenAsync(GetFailoverAddresses(), 1, 0, false, null, linkedCts.Token,
                                    context)
                                .ConfigureAwait(false))
                        {
                            if (context.LastException != null)
                            {
                                ExceptionDispatchInfo.Capture(context.LastException).Throw();
                            }

                            throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECT_FAILED,
                                "websocket connection failed");
                        }
                    }
                    finally
                    {
                        _reconnectLock.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (_disposeToken.IsCancellationRequested &&
                                                     !cancellationToken.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(WSClientAsync));
            }
            finally
            {
                ExitDisposeOperation();
            }
        }

        private bool TryEnterDisposeOperation()
        {
            lock (_disposeCtsLock)
            {
                if (_disposeCtsDisposed != 0)
                {
                    return false;
                }

                checked
                {
                    _disposeOperationCount++;
                }

                return true;
            }
        }

        private void ExitDisposeOperation()
        {
            var disposeToken = false;
            lock (_disposeCtsLock)
            {
                if (_disposeOperationCount > 0)
                {
                    _disposeOperationCount--;
                }

                if (_disposeOperationCount == 0 && IsDisposed() && _disposeCtsDisposed == 0)
                {
                    _disposeCtsDisposed = 1;
                    disposeToken = true;
                }
            }

            if (disposeToken)
            {
                _disposeCts.Dispose();
            }
        }

        private void TryDisposeDisposeToken()
        {
            var disposeToken = false;
            lock (_disposeCtsLock)
            {
                if (_disposeOperationCount == 0 && _disposeCtsDisposed == 0)
                {
                    _disposeCtsDisposed = 1;
                    disposeToken = true;
                }
            }

            if (disposeToken)
            {
                _disposeCts.Dispose();
            }
        }

        private async Task<bool> TryOpenAsync(IReadOnlyList<FailoverAddress> addresses, int retryCount,
            int retryIntervalMs, bool delayBeforeFirstAttempt, FailoverAddress preferredAddress,
            CancellationToken cancellationToken, TryOpenContext context = null)
        {
            Exception lastException = null;
            if (retryCount <= 0)
            {
                return false;
            }

            for (var i = 0; i < retryCount; i++)
            {
                if (delayBeforeFirstAttempt || i > 0)
                {
                    await Task.Delay(retryIntervalMs, cancellationToken).ConfigureAwait(false);
                }

                var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (preferredAddress != null)
                {
                    var preferredLease = FailoverAddressCache.AcquireLeast(new[] { preferredAddress }, excluded);
                    if (preferredLease != null)
                    {
                        if (await TryOpenWithLeaseAsync(preferredLease, cancellationToken, context)
                                .ConfigureAwait(false))
                        {
                            return true;
                        }

                        lastException = context?.LastException;
                        excluded.Add(preferredAddress.CacheKey);
                    }
                }

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var lease = FailoverAddressCache.AcquireLeast(addresses, excluded);
                    if (lease == null)
                    {
                        break;
                    }

                    var address = lease.Address;
                    if (await TryOpenWithLeaseAsync(lease, cancellationToken, context).ConfigureAwait(false))
                    {
                        return true;
                    }

                    lastException = context?.LastException;
                    excluded.Add(address.CacheKey);
                }
            }

            if (lastException != null)
            {
                if (context != null)
                {
                    context.LastException = lastException;
                }

            }

            return false;
        }

        private async Task<bool> TryOpenWithLeaseAsync(FailoverAddressLease lease,
            CancellationToken cancellationToken, TryOpenContext context)
        {
            ConnectionAsync connection = null;
            try
            {
                connection = CreateConnection(lease.Address);
                var response = await connection.ConnectAsync(AdapterHA, cancellationToken).ConfigureAwait(false);
                ThrowIfDisposed();
                if (AdapterHA && response?.ListInstances != null && response.ListInstances.Length > 0)
                {
                    SyncDiscoveredAddresses(response.ListInstances);
                }

                await ReplaceConnectionAsync(connection, lease).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                await CloseConnectionIgnoringErrorsAsync(connection).ConfigureAwait(false);
                lease.Dispose();
                throw;
            }
            catch (Exception e)
            {
                if (context != null)
                {
                    context.LastException = e;
                }

                await CloseConnectionIgnoringErrorsAsync(connection).ConfigureAwait(false);
                lease.Dispose();
                return false;
            }
        }

        private IReadOnlyList<FailoverAddress> GetFailoverAddresses()
        {
            if (AdapterHA)
            {
                var seeds = _builder.GetFailoverAddresses();
                var known = AdapterClusterRegistry.ExpandIfKnown(seeds);
                lock (_addressLock)
                {
                    _failoverAddresses = new List<FailoverAddress>(known);
                }
            }

            lock (_addressLock)
            {
                return _failoverAddresses.ToArray();
            }
        }

        private void SyncDiscoveredAddresses(string[] instances)
        {
            var discovered = AdapterHAHelper.ParseInstances(instances, TDengineConstant.ProtocolWebSocket,
                _builder.UseSSL);
            if (discovered == null)
            {
                return;
            }

            var seeds = _builder.GetFailoverAddresses();
            IReadOnlyList<FailoverAddress> cluster;
            lock (_addressLock)
            {
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var merged = new List<FailoverAddress>(seeds.Count + discovered.Count);
                AddDistinctAddresses(seeds, keys, merged);
                AddDistinctAddresses(discovered, keys, merged);
                _failoverAddresses = merged;
                cluster = merged.ToArray();
            }

            AdapterClusterRegistry.RegisterCluster(seeds, cluster);
        }

        private static void AddDistinctAddresses(IReadOnlyList<FailoverAddress> source, ISet<string> keys,
            ICollection<FailoverAddress> destination)
        {
            for (var i = 0; i < source.Count; i++)
            {
                var address = source[i];
                if (address != null && keys.Add(address.CacheKey))
                {
                    destination.Add(address);
                }
            }
        }

        private async Task ReplaceConnectionAsync(ConnectionAsync connection, FailoverAddressLease lease)
        {
            ThrowIfDisposed();
            var oldConnection = _connection;
            var oldLease = _addressLease;
            _connection = connection;
            _addressLease = lease;
            try
            {
                if (oldConnection != null && !ReferenceEquals(oldConnection, connection))
                {
                    await CloseConnectionIgnoringErrorsAsync(oldConnection).ConfigureAwait(false);
                }
            }
            finally
            {
                oldLease?.Dispose();
            }
        }

        private static async Task CloseConnectionIgnoringErrorsAsync(ConnectionAsync connection)
        {
            if (connection == null)
            {
                return;
            }

            try
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
            catch
            {
                // The connection is already being discarded; cleanup failure must not replace the primary error.
            }
        }

        private static async Task InvalidateConnectionIgnoringErrorsAsync(ConnectionAsync connection,
            string warning)
        {
            if (connection == null)
            {
                return;
            }

            try
            {
                await connection.InvalidateAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Trace.TraceWarning(warning + ": " + e.GetType().Name);
            }
        }

        private static bool IsProtocolViolation(Exception exception)
        {
            return exception is InvalidDataException ||
                   exception is TDengineError error &&
                   error.Code == (int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE;
        }

        private Task ReconnectAsync(bool force = false, ConnectionAsync old = null,
            CancellationToken cancellationToken = default)
        {
            return ReconnectCoreAsync(force, old, cancellationToken);
        }

        private async Task ReconnectCoreAsync(bool force, ConnectionAsync old,
            CancellationToken cancellationToken)
        {
            if (!AutoReconnect)
            {
                return;
            }

            if (!TryEnterDisposeOperation())
            {
                throw new ObjectDisposedException(nameof(WSClientAsync));
            }

            try
            {
                ThrowIfDisposed();
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeToken))
                {
                    await _reconnectLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                    try
                    {
                        ThrowIfDisposed();
                        if (_connection != null && _connection.IsAvailable())
                        {
                            if (!force)
                            {
                                return;
                            }

                            if (old != null && _connection != old)
                            {
                                return;
                            }
                        }

                        var preferredAddress = _addressLease?.Address;
                        var context = new TryOpenContext();
                        if (!await TryOpenAsync(GetFailoverAddresses(), _builder.ReconnectRetryCount,
                                    _builder.ReconnectIntervalMs, true, preferredAddress, linkedCts.Token, context)
                                .ConfigureAwait(false))
                        {
                            throw context.LastException == null
                                ? new TDengineError((int)TDengineError.InternalErrorCode.WS_RECONNECT_FAILED,
                                    "websocket connection reconnect failed")
                                : new TDengineError((int)TDengineError.InternalErrorCode.WS_RECONNECT_FAILED,
                                    "websocket connection reconnect failed", context.LastException);
                        }
                    }
                    finally
                    {
                        _reconnectLock.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (_disposeToken.IsCancellationRequested &&
                                                     !cancellationToken.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(WSClientAsync));
            }
            finally
            {
                ExitDisposeOperation();
            }
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
            ThrowIfDisposed();
            var connection = _connection;
            try
            {
                return await DoStmtInitAsync(connection, reqId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (!ShouldRetryRequest(e, connection))
                {
                    throw;
                }

                await ReconnectAsync(true, connection, cancellationToken).ConfigureAwait(false);
                return await DoStmtInitAsync(_connection, reqId, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<IStmtAsync> DoStmtInitAsync(ConnectionAsync connection, long reqId,
            CancellationToken cancellationToken)
        {
            if (connection == null) throw new ObjectDisposedException(nameof(WSClientAsync));
            var resp = await connection.Stmt2InitAsync((ulong)reqId, cancellationToken).ConfigureAwait(false);
            if (resp == null || resp.StmtId == 0)
            {
                await InvalidateConnectionIgnoringErrorsAsync(connection,
                    "WSClientAsync failed to invalidate a connection after an invalid statement response")
                    .ConfigureAwait(false);
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "stmt2 init returned an invalid statement id");
            }

            try
            {
                return new WSStmtAsync(this, resp.StmtId, _tz, connection);
            }
            catch (Exception constructionException)
            {
                try
                {
                    await connection.Stmt2CloseAsync(resp.StmtId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    await InvalidateConnectionIgnoringErrorsAsync(connection,
                        "WSClientAsync failed to invalidate a connection after statement cleanup failure")
                        .ConfigureAwait(false);
                    throw new AggregateException("Failed to construct a statement and release the server statement.",
                        constructionException, cleanupException);
                }

                throw;
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

        public async Task<IRowsAsync> QueryAsync(string query, long reqId, CancellationToken cancellationToken)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            reqId = ReqId.Normalize(reqId, nameof(reqId));
            ThrowIfDisposed();
            var connection = _connection;
            try
            {
                return await DoQueryAsync(connection, query, reqId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (!ShouldRetryRequest(e, connection))
                {
                    throw;
                }

                await ReconnectAsync(true, connection, cancellationToken).ConfigureAwait(false);
                return await DoQueryAsync(_connection, query, reqId, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<IRowsAsync> DoQueryAsync(ConnectionAsync connection, string query, long reqId,
            CancellationToken cancellationToken)
        {
            if (connection == null) throw new ObjectDisposedException(nameof(WSClientAsync));
            var resp = await connection.BinaryQueryAsync(query, (ulong)reqId, cancellationToken)
                .ConfigureAwait(false);
            if (resp == null)
            {
                await InvalidateConnectionIgnoringErrorsAsync(connection,
                    "WSClientAsync failed to invalidate a connection after an empty query response")
                    .ConfigureAwait(false);
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "websocket query returned an empty response");
            }

            if (resp.IsUpdate)
            {
                return new WSRowsAsync(resp.AffectedRows);
            }

            if (resp.ResultId == 0)
            {
                await InvalidateConnectionIgnoringErrorsAsync(connection,
                    "WSClientAsync failed to invalidate a connection after an invalid query result")
                    .ConfigureAwait(false);
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "websocket query returned an invalid result id");
            }

            try
            {
                return new WSRowsAsync(resp, connection, _tz);
            }
            catch (Exception constructionException)
            {
                try
                {
                    await connection.FreeResultAsync(resp.ResultId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    await InvalidateConnectionIgnoringErrorsAsync(connection,
                        "WSClientAsync failed to invalidate a connection after query result cleanup failure")
                        .ConfigureAwait(false);
                    throw new AggregateException("Failed to construct rows and release the server result.",
                        constructionException, cleanupException);
                }

                if (IsProtocolViolation(constructionException))
                {
                    await InvalidateConnectionIgnoringErrorsAsync(connection,
                        "WSClientAsync failed to invalidate a connection after invalid query metadata")
                        .ConfigureAwait(false);
                }

                ExceptionDispatchInfo.Capture(constructionException).Throw();
                throw;
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
            if (query == null) throw new ArgumentNullException(nameof(query));
            reqId = ReqId.Normalize(reqId, nameof(reqId));
            ThrowIfDisposed();
            var connection = _connection;
            WSQueryResp response;
            try
            {
                response = await SendExecRequestAsync(connection, query, reqId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (!ShouldRetryRequest(e, connection))
                {
                    throw;
                }

                await ReconnectAsync(true, connection, cancellationToken).ConfigureAwait(false);
                connection = _connection;
                response = await SendExecRequestAsync(connection, query, reqId, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await CompleteExecResponseAsync(connection, response).ConfigureAwait(false);
        }

        private async Task<WSQueryResp> SendExecRequestAsync(ConnectionAsync connection, string query,
            long reqId,
            CancellationToken cancellationToken)
        {
            if (connection == null) throw new ObjectDisposedException(nameof(WSClientAsync));
            var resp = await connection.BinaryQueryAsync(query, (ulong)reqId, cancellationToken)
                .ConfigureAwait(false);
            if (resp == null)
            {
                await InvalidateConnectionIgnoringErrorsAsync(connection,
                    "WSClientAsync failed to invalidate a connection after an empty exec response")
                    .ConfigureAwait(false);
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "websocket exec returned an empty response");
            }

            return resp;
        }

        private async Task<long> CompleteExecResponseAsync(ConnectionAsync connection, WSQueryResp response)
        {
            if (!response.IsUpdate)
            {
                if (response.ResultId == 0)
                {
                    await InvalidateConnectionIgnoringErrorsAsync(connection,
                        "WSClientAsync failed to invalidate a connection after an invalid exec result")
                        .ConfigureAwait(false);
                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                        "websocket exec returned an invalid result id");
                }

                try
                {
                    await connection.FreeResultAsync(response.ResultId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    await InvalidateConnectionIgnoringErrorsAsync(connection,
                        "WSClientAsync failed to invalidate a connection after exec result cleanup failure")
                        .ConfigureAwait(false);
                    throw;
                }
            }

            return response.AffectedRows;
        }

        public Task SchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol,
            TDengineSchemalessPrecision precision, int ttl, long reqId)
        {
            return SchemalessInsertAsync(lines, protocol, precision, ttl, reqId, CancellationToken.None);
        }

        public async Task SchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol,
            TDengineSchemalessPrecision precision, int ttl, long reqId, CancellationToken cancellationToken)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            reqId = ReqId.Normalize(reqId, nameof(reqId));
            ThrowIfDisposed();
            var connection = _connection;
            try
            {
                await DoSchemalessInsertAsync(connection, lines, protocol, precision, ttl, reqId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (!ShouldRetryRequest(e, connection))
                {
                    throw;
                }

                await ReconnectAsync(true, connection, cancellationToken).ConfigureAwait(false);
                await DoSchemalessInsertAsync(_connection, lines, protocol, precision, ttl, reqId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private static async Task DoSchemalessInsertAsync(ConnectionAsync connection, string[] lines,
            TDengineSchemalessProtocol protocol, TDengineSchemalessPrecision precision, int ttl, long reqId,
            CancellationToken cancellationToken)
        {
            if (connection == null) throw new ObjectDisposedException(nameof(WSClientAsync));
            var line = string.Join("\n", lines);
            await connection.SchemalessInsertAsync(line, protocol, precision, ttl, reqId, cancellationToken)
                .ConfigureAwait(false);
        }

        public bool ConnectionAvailable()
        {
            if (IsDisposed())
            {
                return false;
            }

            var connection = _connection;
            return connection != null && connection.IsAvailable();
        }

        internal async Task<bool> ValidateConnectionAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var connection = _connection;
            if (connection == null || !connection.IsAvailable())
            {
                return false;
            }

            await connection.ValidateConnectionAsync(cancellationToken).ConfigureAwait(false);
            return connection.IsAvailable();
        }

        public async Task<ConnectionAsync> TryReconnectOrGetConnectionAsync(ConnectionAsync old,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var currentConnection = _connection;
            if (currentConnection != old && currentConnection != null && currentConnection.IsAvailable())
            {
                return currentConnection;
            }

            await ReconnectAsync(true, old, cancellationToken).ConfigureAwait(false);
            currentConnection = _connection;
            if (currentConnection != null && currentConnection.IsAvailable())
            {
                return currentConnection;
            }

            throw new TDengineError((int)TDengineError.InternalErrorCode.WS_RECONNECT_FAILED,
                "websocket connection reconnect failed");
        }

        private sealed class TryOpenContext
        {
            internal Exception LastException { get; set; }
        }
    }
}
