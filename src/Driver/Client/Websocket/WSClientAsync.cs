using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods;

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
        private readonly IReadOnlyList<FailoverAddress> _failoverAddresses;
        private readonly SemaphoreSlim _reconnectLock = new SemaphoreSlim(1, 1);

        internal bool AutoReconnect => _builder.AutoReconnect;
        public WebSocketState State => _connection == null ? WebSocketState.Closed : _connection.State;

        public WSClientAsync(ConnectionStringBuilder builder)
        {
            Debug.Assert(builder.Protocol == TDengineConstant.ProtocolWebSocket);
            _builder = builder;
            _tz = builder.GetTimeZone();
            _failoverAddresses = builder.GetFailoverAddresses();
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
                uriBuilder.Query = $"token={builder.Token}";
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
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
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

            if (oldConnection != null)
            {
                await oldConnection.CloseAsync().ConfigureAwait(false);
            }

            oldLease?.Dispose();
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

        public async Task ConnectAsync()
        {
            await ConnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!await TryOpenAsync(_failoverAddresses, 1, 0, false, null, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECT_FAILED,
                    "websocket connection failed");
            }
        }

        private async Task<bool> TryOpenAsync(IReadOnlyList<FailoverAddress> addresses, int retryCount,
            int retryIntervalMs, bool delayBeforeFirstAttempt, FailoverAddress preferredAddress,
            CancellationToken cancellationToken)
        {
            return await TryOpenAsync(addresses, retryCount, retryIntervalMs, delayBeforeFirstAttempt,
                preferredAddress, cancellationToken, null, false).ConfigureAwait(false);
        }

        private async Task<bool> TryOpenAsync(IReadOnlyList<FailoverAddress> addresses, int retryCount,
            int retryIntervalMs, bool delayBeforeFirstAttempt, FailoverAddress preferredAddress,
            CancellationToken cancellationToken, ConnectionAsync old, bool force)
        {
            Exception lastException = null;
            var excluded = new HashSet<string>();

            for (var i = 0; i < Math.Max(1, retryCount); i++)
            {
                if (delayBeforeFirstAttempt || i > 0)
                {
                    await Task.Delay(retryIntervalMs, cancellationToken).ConfigureAwait(false);
                }

                var candidates = new List<FailoverAddress>();
                if (preferredAddress != null && !excluded.Contains(preferredAddress.CacheKey))
                {
                    candidates.Add(preferredAddress);
                }

                foreach (var address in addresses)
                {
                    if (!excluded.Contains(address.CacheKey) &&
                        (preferredAddress == null || address.CacheKey != preferredAddress.CacheKey))
                    {
                        candidates.Add(address);
                    }
                }

                foreach (var address in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var lease = FailoverAddressCache.AcquireLeast(new[] { address }, excluded);
                    if (lease == null) continue;

                    ConnectionAsync connection = null;
                    try
                    {
                        connection = CreateConnection(address);
                        await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
                        if (await TrySetConnectionAsync(connection, lease, old, force).ConfigureAwait(false))
                        {
                            return true;
                        }

                        connection = null;
                        lease = null;
                        return true;
                    }
                    catch (OperationCanceledException)
                    {
                        if (connection != null)
                        {
                            await connection.CloseAsync().ConfigureAwait(false);
                        }

                        lease.Dispose();
                        throw;
                    }
                    catch (Exception e)
                    {
                        lastException = e;
                        excluded.Add(address.CacheKey);
                        if (connection != null)
                        {
                            await connection.CloseAsync().ConfigureAwait(false);
                        }

                        lease.Dispose();
                    }
                }

                excluded.Clear();
            }

            if (lastException != null)
            {
                Debug.WriteLine(lastException);
            }

            return false;
        }

        private async Task<bool> TrySetConnectionAsync(ConnectionAsync connection, FailoverAddressLease lease,
            ConnectionAsync old, bool force)
        {
            await _reconnectLock.WaitAsync().ConfigureAwait(false);
            ConnectionAsync oldConnection;
            FailoverAddressLease oldLease;
            var discardNewConnection = false;
            try
            {
                ThrowIfDisposed();
                if (_connection != null && _connection.IsAvailable())
                {
                    if (!force || (old != null && _connection != old))
                    {
                        discardNewConnection = true;
                    }
                }

                if (discardNewConnection)
                {
                    oldConnection = null;
                    oldLease = null;
                }
                else
                {
                    oldConnection = _connection;
                    oldLease = _addressLease;
                    _connection = connection;
                    _addressLease = lease;
                }
            }
            finally
            {
                _reconnectLock.Release();
            }

            if (discardNewConnection)
            {
                await connection.CloseAsync().ConfigureAwait(false);
                lease.Dispose();
                return false;
            }

            if (oldConnection != null)
            {
                await oldConnection.CloseAsync().ConfigureAwait(false);
            }

            oldLease?.Dispose();
            return true;
        }

        private async Task ReconnectAsync(bool force = false, ConnectionAsync old = null,
            CancellationToken cancellationToken = default)
        {
            if (!AutoReconnect)
            {
                return;
            }

            ThrowIfDisposed();
            await _reconnectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
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
            }
            finally
            {
                _reconnectLock.Release();
            }

            var preferredAddress = _addressLease == null ? null : _addressLease.Address;
            if (!await TryOpenAsync(_failoverAddresses, _builder.ReconnectRetryCount,
                    _builder.ReconnectIntervalMs, true, preferredAddress, cancellationToken, old, force)
                    .ConfigureAwait(false))
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_RECONNECT_FAILED,
                    "websocket connection reconnect failed");
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
            try
            {
                return await DoStmtInitAsync(reqId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                var currentConnection = _connection;
                if (currentConnection != null && currentConnection.IsAvailable(e))
                {
                    throw;
                }

                ThrowIfDisposed();
                await ReconnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return await DoStmtInitAsync(reqId, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<IStmtAsync> DoStmtInitAsync(long reqId, CancellationToken cancellationToken)
        {
            var connection = _connection;
            if (connection == null) throw new ObjectDisposedException(nameof(WSClientAsync));
            var resp = await connection.Stmt2InitAsync((ulong)reqId, cancellationToken).ConfigureAwait(false);
            return new WSStmtAsync(this, resp.StmtId, _tz, connection);
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
            try
            {
                return await DoQueryAsync(query, reqId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                var currentConnection = _connection;
                if (currentConnection != null && currentConnection.IsAvailable(e))
                {
                    throw;
                }

                ThrowIfDisposed();
                await ReconnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return await DoQueryAsync(query, reqId, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<IRowsAsync> DoQueryAsync(string query, long reqId, CancellationToken cancellationToken)
        {
            var connection = _connection;
            if (connection == null) throw new ObjectDisposedException(nameof(WSClientAsync));
            var resp = await connection.BinaryQueryAsync(query, (ulong)reqId, cancellationToken)
                .ConfigureAwait(false);
            if (resp.IsUpdate)
            {
                return new WSRowsAsync(resp.AffectedRows);
            }

            return new WSRowsAsync(resp, connection, _tz);
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
            try
            {
                return await DoExecAsync(query, reqId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                var currentConnection = _connection;
                if (currentConnection != null && currentConnection.IsAvailable(e))
                {
                    throw;
                }

                ThrowIfDisposed();
                await ReconnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return await DoExecAsync(query, reqId, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<long> DoExecAsync(string query, long reqId, CancellationToken cancellationToken)
        {
            var connection = _connection;
            if (connection == null) throw new ObjectDisposedException(nameof(WSClientAsync));
            var resp = await connection.BinaryQueryAsync(query, (ulong)reqId, cancellationToken)
                .ConfigureAwait(false);
            if (!resp.IsUpdate)
            {
                await connection.FreeResultAsync(resp.ResultId).ConfigureAwait(false);
            }

            return resp.AffectedRows;
        }

        public Task SchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol,
            TDengineSchemalessPrecision precision, int ttl, long reqId)
        {
            return SchemalessInsertAsync(lines, protocol, precision, ttl, reqId, CancellationToken.None);
        }

        public async Task SchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol,
            TDengineSchemalessPrecision precision, int ttl, long reqId, CancellationToken cancellationToken)
        {
            try
            {
                await DoSchemalessInsertAsync(lines, protocol, precision, ttl, reqId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                var currentConnection = _connection;
                if (currentConnection != null && currentConnection.IsAvailable(e))
                {
                    throw;
                }

                ThrowIfDisposed();
                await ReconnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                await DoSchemalessInsertAsync(lines, protocol, precision, ttl, reqId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private async Task DoSchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol,
            TDengineSchemalessPrecision precision, int ttl, long reqId, CancellationToken cancellationToken)
        {
            var connection = _connection;
            if (connection == null) throw new ObjectDisposedException(nameof(WSClientAsync));
            var line = string.Join("\n", lines);
            await connection.SchemalessInsertAsync(line, protocol, precision, ttl, reqId, cancellationToken)
                .ConfigureAwait(false);
        }

        public bool ConnectionAvailable()
        {
            var connection = _connection;
            return connection != null && connection.IsAvailable();
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
    }
}
