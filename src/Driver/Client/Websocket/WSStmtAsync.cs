using System;
using System.Threading;
using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods;

namespace TDengine.Driver.Client.Websocket
{
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
    public class WSStmtAsync : AbstractStmt, IStmtAsync, IAsyncDisposable
#else
    public class WSStmtAsync : AbstractStmt, IStmtAsync, IDisposable
#endif
    {
        private readonly WSClientAsync _client;
        private readonly TimeZoneInfo _tz;
        private ConnectionAsync _connection;
        private ulong _stmt;
        private int _closed;

        public WSStmtAsync(WSClientAsync client, ulong stmt, TimeZoneInfo tz, ConnectionAsync connection) : base(30)
        {
            _client = client;
            _stmt = stmt;
            _tz = tz;
            _connection = connection;
        }

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
        public async ValueTask DisposeAsync()
        {
            await CloseAsync().ConfigureAwait(false);
        }
#else
        public override void Dispose()
        {
            CloseAsync().GetAwaiter().GetResult();
        }
#endif

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
        public override void Dispose()
        {
            CloseAsync().GetAwaiter().GetResult();
        }
#endif

        private async Task CloseAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1) return;

            if (_connection == null || !_connection.IsAvailable()) return;
            try
            {
                await _connection.Stmt2CloseAsync(_stmt).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
            finally
            {
                _connection = null;
            }
        }

        public Task PrepareAsync(string query)
        {
            return PrepareAsync(query, CancellationToken.None);
        }

        public async Task PrepareAsync(string query, CancellationToken cancellationToken)
        {
            bool isInsert;
            int count;
            TaosFieldAll[] fields;
            try
            {
                var resp = await _connection.Stmt2PrepareAsync(_stmt, query, cancellationToken).ConfigureAwait(false);
                ConvertPrepareResponse(resp, out isInsert, out count, out fields);
            }
            catch (Exception e)
            {
                if (!_client.AutoReconnect || IsConnectionAvailable(e)) throw;
                await ReconnectInternalAsync(cancellationToken).ConfigureAwait(false);
                var resp = await _connection.Stmt2PrepareAsync(_stmt, query, cancellationToken).ConfigureAwait(false);
                ConvertPrepareResponse(resp, out isInsert, out count, out fields);
            }

            ApplyPrepareResult(query, isInsert, count, fields);
        }

        private static void ConvertPrepareResponse(Impl.WebSocketMethods.Protocol.WSStmt2PrepareResp resp,
            out bool isInsert, out int count, out TaosFieldAll[] fields)
        {
            isInsert = resp.IsInsert;
            count = resp.FieldsCount;
            if (!isInsert)
            {
                fields = null;
                return;
            }

            fields = new TaosFieldAll[resp.Fields.Count];
            for (var i = 0; i < resp.Fields.Count; i++)
            {
                fields[i] = new TaosFieldAll
                {
                    name = resp.Fields[i].Name,
                    type = resp.Fields[i].FieldType,
                    precision = resp.Fields[i].Precision,
                    scale = resp.Fields[i].Scale,
                    bytes = resp.Fields[i].Bytes,
                    field_type = resp.Fields[i].BindType
                };
            }
        }

        public Task SetTableNameAsync(string tableName)
        {
            SetTableName(tableName);
            return Task.FromResult(0);
        }

        public Task SetTableNameAsync(string tableName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SetTableNameAsync(tableName);
        }

        public Task SetTagsAsync(object[] tags)
        {
            SetTags(tags);
            return Task.FromResult(0);
        }

        public Task SetTagsAsync(object[] tags, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SetTagsAsync(tags);
        }

        public Task<TaosFieldE[]> GetTagFieldsAsync()
        {
            return Task.FromResult(GetTagFields());
        }

        public Task<TaosFieldE[]> GetColFieldsAsync()
        {
            return Task.FromResult(GetColFields());
        }

        public Task BindRowAsync(object[] row)
        {
            BindRow(row);
            return Task.FromResult(0);
        }

        public Task BindRowAsync(object[] row, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return BindRowAsync(row);
        }

        public Task BindColumnAsync(TaosFieldE[] fields, params Array[] arrays)
        {
            BindColumn(fields, arrays);
            return Task.FromResult(0);
        }

        public Task BindColumnAsync(TaosFieldE[] fields, CancellationToken cancellationToken, params Array[] arrays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return BindColumnAsync(fields, arrays);
        }

        public Task AddBatchAsync()
        {
            AddBatch();
            return Task.FromResult(0);
        }

        public Task AddBatchAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return AddBatchAsync();
        }

        public Task ExecAsync()
        {
            return ExecAsync(CancellationToken.None);
        }

        public async Task ExecAsync(CancellationToken cancellationToken)
        {
            var buffer = GenerateBindBinaryForExecution();
            int affectedRows;
            try
            {
                affectedRows = await BindBinaryInternalAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (!_client.AutoReconnect || IsConnectionAvailable(e)) throw;
                await ReconnectInternalAsync(cancellationToken).ConfigureAwait(false);
                var resp = await _connection.Stmt2PrepareAsync(_stmt, PreparedSql, cancellationToken)
                    .ConfigureAwait(false);
                ConvertPrepareResponse(resp, out var insert, out var count, out var fields);
                ValidateRePrepareResult(insert, count, fields);
                affectedRows = await BindBinaryInternalAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            CompleteExecution(affectedRows);
        }

        public Task<IRowsAsync> ResultAsync()
        {
            return ResultAsync(CancellationToken.None);
        }

        public async Task<IRowsAsync> ResultAsync(CancellationToken cancellationToken)
        {
            CheckExecuted();
            if (IsInsert())
            {
                return new WSRowsAsync((int)Affected());
            }

            var resp = await _connection.Stmt2UseResultAsync(_stmt, cancellationToken).ConfigureAwait(false);
            return new WSRowsAsync(resp.ResultId, resp, _connection, _tz);
        }

        protected override void PrepareInternal(string query, out bool isInsert, out int count, out TaosFieldAll[] fields)
        {
            var resp = _connection.Stmt2PrepareAsync(_stmt, query).GetAwaiter().GetResult();
            ConvertPrepareResponse(resp, out isInsert, out count, out fields);
        }

        protected override void BindBinaryInternal(byte[] data, out int affectedRows)
        {
            affectedRows = BindBinaryInternalAsync(data, CancellationToken.None).GetAwaiter().GetResult();
        }

        private async Task<int> BindBinaryInternalAsync(byte[] data, CancellationToken cancellationToken)
        {
            await _connection.Stmt2BindAsync(_stmt, data, cancellationToken).ConfigureAwait(false);
            var resp = await _connection.Stmt2ExecAsync(_stmt, cancellationToken).ConfigureAwait(false);
            return resp.Affected;
        }

        protected override bool IsConnectionAvailable(Exception exception)
        {
            return _connection != null && _connection.IsAvailable(exception);
        }

        protected override void ReconnectInternal()
        {
            ReconnectInternalAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        private async Task ReconnectInternalAsync(CancellationToken cancellationToken)
        {
            _stmt = 0;
            var newConnection = await _client.TryReconnectOrGetConnectionAsync(_connection, cancellationToken)
                .ConfigureAwait(false);
            _connection = newConnection;
            var resp = await _connection.Stmt2InitAsync((ulong)ReqId.GetReqId(), cancellationToken)
                .ConfigureAwait(false);
            _stmt = resp.StmtId;
        }

        protected override bool AutoReconnectInternal()
        {
            return _client.AutoReconnect;
        }

        protected override IRows QueryResultInternal()
        {
            throw new NotSupportedException("Use ResultAsync for asynchronous statements.");
        }

        protected override IRows InsertResultInternal(int affectedRows)
        {
            throw new NotSupportedException("Use ResultAsync for asynchronous statements.");
        }
    }
}
