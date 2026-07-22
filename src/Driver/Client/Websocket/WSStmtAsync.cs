using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;

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
        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
        private readonly object _disposeLock = new object();
        private static readonly TimeSpan DefaultDisposeDrainTimeout = TimeSpan.FromSeconds(3);
        private readonly TimeSpan _disposeDrainTimeout;
        private ConnectionAsync _connection;
        private ulong _stmt;
        private int _disposeRequested;
        private int _connectionInvalidated;
        private Task _disposeTask;

        public WSStmtAsync(WSClientAsync client, ulong stmt, TimeZoneInfo tz, ConnectionAsync connection)
            : this(client, stmt, tz, connection, DefaultDisposeDrainTimeout)
        {
        }

        internal WSStmtAsync(WSClientAsync client, ulong stmt, TimeZoneInfo tz, ConnectionAsync connection,
            TimeSpan disposeDrainTimeout) : base(30)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            if (stmt == 0) throw new ArgumentOutOfRangeException(nameof(stmt));
            _stmt = stmt;
            _tz = tz ?? TimeZoneInfo.Local;
            TimeoutHelper.ValidateTimerTimeout(disposeDrainTimeout, nameof(disposeDrainTimeout), false);
            _disposeDrainTimeout = disposeDrainTimeout;
        }

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
        public async ValueTask DisposeAsync()
        {
            await CloseAsync().ConfigureAwait(false);
        }
#endif

        public override void Dispose()
        {
            CloseAsync().GetAwaiter().GetResult();
        }

        private Task CloseAsync()
        {
            lock (_disposeLock)
            {
                if (_disposeTask == null)
                {
                    _disposeTask = CloseOnceAsync();
                }

                return _disposeTask;
            }
        }

        private async Task CloseOnceAsync()
        {
            Interlocked.Exchange(ref _disposeRequested, 1);
            using (var cleanupCts = new CancellationTokenSource())
            {
                cleanupCts.CancelAfter(_disposeDrainTimeout);
                var acquired = false;
                try
                {
                    await _operationGate.WaitAsync(cleanupCts.Token).ConfigureAwait(false);
                    acquired = true;
                }
                catch (OperationCanceledException) when (cleanupCts.IsCancellationRequested)
                {
                    Trace.TraceWarning("WSStmtAsync timed out waiting for an active operation during disposal.");
                    acquired = await _operationGate.WaitAsync(TimeSpan.Zero).ConfigureAwait(false);
                }

                if (!acquired)
                {
                    await InvalidateConnectionAfterDrainTimeoutAsync().ConfigureAwait(false);
                    return;
                }

                try
                {
                    await CloseStatementBestEffortAsync(cleanupCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _operationGate.Release();
                }
            }
        }

        private async Task InvalidateConnectionAfterDrainTimeoutAsync()
        {
            var connection = _connection;
            _connection = null;
            _stmt = 0;
            if (connection == null || Interlocked.Exchange(ref _connectionInvalidated, 1) != 0)
            {
                return;
            }

            await InvalidateConnectionIgnoringErrorsAsync(connection,
                "WSStmtAsync failed to invalidate the WebSocket connection").ConfigureAwait(false);
        }

        private async Task CloseStatementBestEffortWithTimeoutAsync()
        {
            using (var cleanupCts = new CancellationTokenSource())
            {
                cleanupCts.CancelAfter(_disposeDrainTimeout);
                await CloseStatementBestEffortAsync(cleanupCts.Token).ConfigureAwait(false);
            }
        }

        private async Task CloseStatementBestEffortAsync(CancellationToken cleanupToken)
        {
            var connection = _connection;
            var stmt = _stmt;
            _connection = null;
            _stmt = 0;
            ClearStatementCache();
            if (connection == null || stmt == 0)
            {
                return;
            }

            try
            {
                if (connection.IsAvailable())
                {
                    await connection.Stmt2CloseAsync(stmt, cleanupToken).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Trace.TraceWarning("WSStmtAsync failed to close the server statement: " + e.GetType().Name);
                Interlocked.Exchange(ref _connectionInvalidated, 1);
                await InvalidateConnectionIgnoringErrorsAsync(connection,
                    "WSStmtAsync failed to invalidate the connection after statement close failure")
                    .ConfigureAwait(false);
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

        private async Task ExitOperationAsync()
        {
            try
            {
                if (Volatile.Read(ref _disposeRequested) != 0)
                {
                    await CloseStatementBestEffortWithTimeoutAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }

        protected override void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
            {
                throw new ObjectDisposedException(nameof(WSStmtAsync));
            }
        }

        private async Task EnterOperationAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
            }
            catch
            {
                _operationGate.Release();
                throw;
            }
        }

        private SynchronousOperationScope EnterSynchronousOperation()
        {
            ThrowIfDisposed();
            if (!_operationGate.Wait(TimeSpan.Zero))
            {
                throw new InvalidOperationException(
                    "A statement operation is already running. Concurrent operations are not supported.");
            }

            try
            {
                ThrowIfDisposed();
            }
            catch
            {
                _operationGate.Release();
                throw;
            }

            return new SynchronousOperationScope(this);
        }

        private void ExitSynchronousOperation()
        {
            try
            {
                if (Volatile.Read(ref _disposeRequested) != 0)
                {
                    CloseStatementBestEffortWithTimeoutAsync().GetAwaiter().GetResult();
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public override void Prepare(string query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            using (EnterSynchronousOperation())
            {
                base.Prepare(query);
            }
        }

        public override bool IsInsert()
        {
            using (EnterSynchronousOperation())
            {
                return base.IsInsert();
            }
        }

        public override void SetTableName(string tableName)
        {
            using (EnterSynchronousOperation())
            {
                base.SetTableName(tableName);
            }
        }

        public override void SetTags(object[] tags)
        {
            using (EnterSynchronousOperation())
            {
                base.SetTags(tags);
            }
        }

        public override TaosFieldE[] GetTagFields()
        {
            using (EnterSynchronousOperation())
            {
                return CloneFields(base.GetTagFields());
            }
        }

        public override TaosFieldE[] GetColFields()
        {
            using (EnterSynchronousOperation())
            {
                return CloneFields(base.GetColFields());
            }
        }

        public override void BindRow(object[] row)
        {
            using (EnterSynchronousOperation())
            {
                base.BindRow(row);
            }
        }

        public override void BindColumn(TaosFieldE[] fields, params Array[] arrays)
        {
            using (EnterSynchronousOperation())
            {
                base.BindColumn(fields, arrays);
            }
        }

        public override void AddBatch()
        {
            using (EnterSynchronousOperation())
            {
                base.AddBatch();
            }
        }

        public override void Exec()
        {
            using (EnterSynchronousOperation())
            {
                base.Exec();
            }
        }

        public override long Affected()
        {
            using (EnterSynchronousOperation())
            {
                return base.Affected();
            }
        }

        public override IRows Result()
        {
            using (EnterSynchronousOperation())
            {
                return base.Result();
            }
        }

        public Task PrepareAsync(string query)
        {
            return PrepareAsync(query, CancellationToken.None);
        }

        public async Task PrepareAsync(string query, CancellationToken cancellationToken)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ClearStatementCache();
                var connection = _connection;
                WSStmt2PrepareResp response;
                try
                {
                    response = await connection.Stmt2PrepareAsync(_stmt, query, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    if (!CanRecoverNonMutatingFailure(e, connection)) throw;
                    await ReconnectInternalAsync(cancellationToken).ConfigureAwait(false);
                    connection = _connection;
                    response = await connection.Stmt2PrepareAsync(_stmt, query, cancellationToken)
                        .ConfigureAwait(false);
                }

                try
                {
                    ConvertPrepareResponse(response, _stmt, out var isInsert, out var count, out var fields);
                    ApplyPrepareResult(query, isInsert, count, fields);
                }
                catch (Exception e) when (IsProtocolViolation(e))
                {
                    await InvalidateConnectionIgnoringErrorsAsync(connection,
                        "WSStmtAsync failed to invalidate a connection after an invalid prepare response")
                        .ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                await ExitOperationAsync().ConfigureAwait(false);
            }
        }

        private static void ConvertPrepareResponse(WSStmt2PrepareResp response, ulong expectedStmtId,
            out bool isInsert,
            out int count, out TaosFieldAll[] fields)
        {
            if (response == null)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "stmt2 prepare returned an empty response");
            }

            ValidateStatementResponse("stmt2 prepare", response, response.StmtId, expectedStmtId);

            if (response.FieldsCount < 0)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "stmt2 prepare returned a negative field count");
            }

            if (response.FieldsCount > BlockReader.MaximumColumnCount)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    $"stmt2 prepare exceeds the supported field count of {BlockReader.MaximumColumnCount}");
            }

            isInsert = response.IsInsert;
            count = response.FieldsCount;
            if (!isInsert)
            {
                fields = null;
                return;
            }

            if (response.Fields == null || response.Fields.Count != count)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "stmt2 prepare field metadata does not match fields_count");
            }

            fields = new TaosFieldAll[count];
            for (var i = 0; i < count; i++)
            {
                var field = response.Fields[i];
                if (field == null)
                {
                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                        $"stmt2 prepare field metadata at index {i} is null");
                }

                if (field.Name == null || !TDengineConstant.IsSupportedDataType(field.FieldType) ||
                    field.FieldType == (sbyte)TDengineDataType.TSDB_DATA_TYPE_NULL ||
                    field.FieldType == (sbyte)TDengineDataType.TSDB_DATA_TYPE_MEDIUMBLOB)
                {
                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                        $"stmt2 prepare field metadata at index {i} has an invalid data type or name");
                }

                if (field.BindType < (byte)TaosFieldType.TAOS_FIELD_COL ||
                    field.BindType > (byte)TaosFieldType.TAOS_FIELD_TBNAME)
                {
                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                        $"stmt2 prepare field metadata at index {i} has an invalid bind type");
                }

                if (field.Bytes < 0)
                {
                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                        $"stmt2 prepare field metadata at index {i} has a negative byte length");
                }

                if (field.FieldType == (sbyte)TDengineDataType.TSDB_DATA_TYPE_TIMESTAMP &&
                    !TDengineConstant.IsValidTimestampPrecision(field.Precision))
                {
                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                        $"stmt2 prepare timestamp precision at index {i} is invalid");
                }

                if ((field.FieldType == (sbyte)TDengineDataType.TSDB_DATA_TYPE_DECIMAL ||
                     field.FieldType == (sbyte)TDengineDataType.TSDB_DATA_TYPE_DECIMAL64) &&
                    !TDengineConstant.IsValidDecimalMetadata(field.FieldType, field.Precision, field.Scale))
                {
                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                        $"stmt2 prepare decimal metadata at index {i} is invalid");
                }

                fields[i] = new TaosFieldAll
                {
                    name = field.Name,
                    type = field.FieldType,
                    precision = field.Precision,
                    scale = field.Scale,
                    bytes = field.Bytes,
                    field_type = field.BindType
                };
            }
        }

        public Task SetTableNameAsync(string tableName)
        {
            return SetTableNameAsync(tableName, CancellationToken.None);
        }

        public async Task SetTableNameAsync(string tableName, CancellationToken cancellationToken)
        {
            await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                base.SetTableName(tableName);
            }
            finally
            {
                await ExitOperationAsync().ConfigureAwait(false);
            }
        }

        public Task SetTagsAsync(object[] tags)
        {
            return SetTagsAsync(tags, CancellationToken.None);
        }

        public async Task SetTagsAsync(object[] tags, CancellationToken cancellationToken)
        {
            await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                base.SetTags(tags);
            }
            finally
            {
                await ExitOperationAsync().ConfigureAwait(false);
            }
        }

        public Task<TaosFieldE[]> GetTagFieldsAsync()
        {
            return GetTagFieldsAsync(CancellationToken.None);
        }

        public async Task<TaosFieldE[]> GetTagFieldsAsync(CancellationToken cancellationToken)
        {
            await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return CloneFields(base.GetTagFields());
            }
            finally
            {
                await ExitOperationAsync().ConfigureAwait(false);
            }
        }

        public Task<TaosFieldE[]> GetColFieldsAsync()
        {
            return GetColFieldsAsync(CancellationToken.None);
        }

        public async Task<TaosFieldE[]> GetColFieldsAsync(CancellationToken cancellationToken)
        {
            await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return CloneFields(base.GetColFields());
            }
            finally
            {
                await ExitOperationAsync().ConfigureAwait(false);
            }
        }

        private static TaosFieldE[] CloneFields(TaosFieldE[] fields)
        {
            return fields == null ? null : (TaosFieldE[])fields.Clone();
        }

        public Task BindRowAsync(object[] row)
        {
            return BindRowAsync(row, CancellationToken.None);
        }

        public async Task BindRowAsync(object[] row, CancellationToken cancellationToken)
        {
            await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                base.BindRow(row);
            }
            finally
            {
                await ExitOperationAsync().ConfigureAwait(false);
            }
        }

        public Task BindColumnAsync(TaosFieldE[] fields, params Array[] arrays)
        {
            return BindColumnAsync(fields, CancellationToken.None, arrays);
        }

        public async Task BindColumnAsync(TaosFieldE[] fields, CancellationToken cancellationToken,
            params Array[] arrays)
        {
            await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                base.BindColumn(fields, arrays);
            }
            finally
            {
                await ExitOperationAsync().ConfigureAwait(false);
            }
        }

        public Task AddBatchAsync()
        {
            return AddBatchAsync(CancellationToken.None);
        }

        public async Task AddBatchAsync(CancellationToken cancellationToken)
        {
            await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                base.AddBatch();
            }
            finally
            {
                await ExitOperationAsync().ConfigureAwait(false);
            }
        }

        public Task ExecAsync()
        {
            return ExecAsync(CancellationToken.None);
        }

        public async Task ExecAsync(CancellationToken cancellationToken)
        {
            await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                byte[] buffer = null;
                var bufferLength = 0;
                try
                {
                    buffer = RentBindBinaryForExecution(out bufferLength);
                    var affectedRows = await BindAndExecuteAsync(buffer, bufferLength, cancellationToken)
                        .ConfigureAwait(false);
                    CompleteExecution(affectedRows);
                }
                catch (Exception e)
                {
                    if (IsProtocolViolation(e))
                    {
                        await InvalidateConnectionIgnoringErrorsAsync(_connection,
                            "WSStmtAsync failed to invalidate a connection after an invalid execute response")
                            .ConfigureAwait(false);
                    }

                    ResetExecutionState();
                    throw;
                }
                finally
                {
                    ReturnPooledBindBinary(buffer, bufferLength);
                }
            }
            finally
            {
                await ExitOperationAsync().ConfigureAwait(false);
            }
        }

        private async Task<int> BindAndExecuteAsync(byte[] buffer, int bufferLength,
            CancellationToken cancellationToken)
        {
            var connection = _connection;
            try
            {
                await BindOnceAsync(connection, buffer, bufferLength, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (!CanRecoverNonMutatingFailure(e, connection)) throw;
                await ReconnectAndReprepareAsync(cancellationToken).ConfigureAwait(false);
                connection = _connection;
                await BindOnceAsync(connection, buffer, bufferLength, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var response = await connection.Stmt2ExecAsync(_stmt, cancellationToken).ConfigureAwait(false);
                ValidateStatementResponse("stmt2 exec", response, response == null ? 0 : response.StmtId, _stmt);
                return response.Affected;
            }
            catch (Exception e)
            {
                if (!CanRetryPotentialWrite(e, connection)) throw;
                await ReconnectAndReprepareAsync(cancellationToken).ConfigureAwait(false);
                connection = _connection;
                await BindOnceAsync(connection, buffer, bufferLength, cancellationToken).ConfigureAwait(false);
                var response = await connection.Stmt2ExecAsync(_stmt, cancellationToken).ConfigureAwait(false);
                ValidateStatementResponse("stmt2 exec", response, response == null ? 0 : response.StmtId, _stmt);
                return response.Affected;
            }
        }

        private async Task BindOnceAsync(ConnectionAsync connection, byte[] buffer, int bufferLength,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await connection.Stmt2BindAsync(_stmt, buffer, bufferLength, cancellationToken)
                    .ConfigureAwait(false);
                ValidateStatementResponse("stmt2 bind", response, response == null ? 0 : response.StmtId, _stmt);
            }
            catch (TDengineWebSocketRequestException e) when (e.RequestMayHaveBeenSent)
            {
                await InvalidateConnectionIgnoringErrorsAsync(connection,
                    "WSStmtAsync failed to invalidate a connection after an uncertain bind outcome")
                    .ConfigureAwait(false);
                throw;
            }
        }

        private bool CanRecoverNonMutatingFailure(Exception exception, ConnectionAsync connection)
        {
            if (!_client.AutoReconnect || exception is OperationCanceledException)
            {
                return false;
            }

            if (exception is TDengineWebSocketRequestException requestException &&
                !requestException.RequestMayHaveBeenSent)
            {
                return connection == null || !connection.IsAvailable();
            }

            return connection != null && !connection.IsAvailable(exception);
        }

        private bool CanRetryPotentialWrite(Exception exception, ConnectionAsync connection)
        {
            return _client.AutoReconnect &&
                   exception is TDengineWebSocketRequestException requestException &&
                   !requestException.RequestMayHaveBeenSent &&
                   (connection == null || !connection.IsAvailable());
        }

        private async Task ReconnectAndReprepareAsync(CancellationToken cancellationToken)
        {
            await ReconnectInternalAsync(cancellationToken).ConfigureAwait(false);
            var connection = _connection;
            var stmt = _stmt;
            WSStmt2PrepareResp response;
            bool insert;
            int count;
            TaosFieldAll[] fields;
            try
            {
                response = await connection.Stmt2PrepareAsync(stmt, PreparedSql, cancellationToken)
                    .ConfigureAwait(false);
                ConvertPrepareResponse(response, stmt, out insert, out count, out fields);
            }
            catch
            {
                await CleanupFailedReprepareAsync(connection, stmt).ConfigureAwait(false);
                throw;
            }

            // A valid response with different metadata is an expected schema-
            // change signal. Keep the newly prepared statement so the caller
            // can explicitly call PrepareAsync again.
            ValidateRePrepareResult(insert, count, fields);
        }

        private async Task CleanupFailedReprepareAsync(ConnectionAsync connection, ulong stmt)
        {
            var invalidateConnection = connection == null || !connection.IsAvailable();
            if (connection != null && stmt != 0 && !invalidateConnection)
            {
                try
                {
                    await connection.Stmt2CloseAsync(stmt, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    invalidateConnection = true;
                    Trace.TraceWarning("WSStmtAsync failed to close a statement after re-prepare failure: " +
                                       cleanupException.GetType().Name);
                }
            }

            _connection = null;
            _stmt = 0;
            ClearStatementCache();
            Interlocked.Exchange(ref _disposeRequested, 1);

            if (invalidateConnection)
            {
                Interlocked.Exchange(ref _connectionInvalidated, 1);
                await InvalidateConnectionIgnoringErrorsAsync(connection,
                    "WSStmtAsync failed to invalidate the connection after re-prepare failure")
                    .ConfigureAwait(false);
            }
        }

        public Task<IRowsAsync> ResultAsync()
        {
            return ResultAsync(CancellationToken.None);
        }

        public async Task<IRowsAsync> ResultAsync(CancellationToken cancellationToken)
        {
            await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CheckExecuted();
                if (base.IsInsert())
                {
                    return new WSRowsAsync(checked((int)base.Affected()));
                }

                var connection = _connection;
                var response = await connection.Stmt2UseResultAsync(_stmt, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    ValidateStatementResponse("stmt2 use result", response,
                        response == null ? 0 : response.StmtId, _stmt);
                    if (response.ResultId == 0)
                    {
                        throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                            "stmt2 use result returned an invalid result id");
                    }
                }
                catch (Exception e) when (IsProtocolViolation(e))
                {
                    await InvalidateConnectionIgnoringErrorsAsync(connection,
                        "WSStmtAsync failed to invalidate a connection after an invalid result response")
                        .ConfigureAwait(false);
                    throw;
                }

                try
                {
                    return new WSRowsAsync(response.ResultId, response, connection, _tz);
                }
                catch (Exception constructionException)
                {
                    try
                    {
                        await connection.FreeResultAsync(response.ResultId, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception cleanupException)
                    {
                        await InvalidateConnectionIgnoringErrorsAsync(connection,
                            "WSStmtAsync failed to invalidate a connection after result cleanup failure")
                            .ConfigureAwait(false);
                        throw new AggregateException("Failed to construct rows and release the statement result.",
                            constructionException, cleanupException);
                    }

                    if (IsProtocolViolation(constructionException))
                    {
                        await InvalidateConnectionIgnoringErrorsAsync(connection,
                            "WSStmtAsync failed to invalidate a connection after invalid result metadata")
                            .ConfigureAwait(false);
                    }

                    throw;
                }
            }
            finally
            {
                await ExitOperationAsync().ConfigureAwait(false);
            }
        }

        protected override void PrepareInternal(string query, out bool isInsert, out int count,
            out TaosFieldAll[] fields)
        {
            ThrowIfDisposed();
            var response = _connection.Stmt2PrepareAsync(_stmt, query).GetAwaiter().GetResult();
            ConvertPrepareResponse(response, _stmt, out isInsert, out count, out fields);
        }

        protected override void BindBinaryInternal(byte[] data, out int affectedRows)
        {
            ThrowIfDisposed();
            affectedRows = BindAndExecuteAsync(data, data.Length, CancellationToken.None).GetAwaiter().GetResult();
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
            var oldConnection = _connection;
            var newConnection = await _client.TryReconnectOrGetConnectionAsync(oldConnection, cancellationToken)
                .ConfigureAwait(false);
            ulong initializedStmtId = 0;
            try
            {
                ThrowIfDisposed();
                var response = await newConnection.Stmt2InitAsync((ulong)ReqId.GetReqId(), cancellationToken)
                    .ConfigureAwait(false);
                if (response == null || response.StmtId == 0)
                {
                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                        "stmt2 init returned an invalid statement id");
                }

                initializedStmtId = response.StmtId;
                ThrowIfDisposed();
                _connection = newConnection;
                _stmt = initializedStmtId;
            }
            catch (Exception e)
            {
                if (initializedStmtId != 0)
                {
                    try
                    {
                        await newConnection.Stmt2CloseAsync(initializedStmtId, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception cleanupException)
                    {
                        Trace.TraceWarning("WSStmtAsync failed to close a newly initialized statement: " +
                                           cleanupException.GetType().Name);
                        await InvalidateConnectionIgnoringErrorsAsync(newConnection,
                            "WSStmtAsync failed to invalidate the connection after new statement cleanup failure")
                            .ConfigureAwait(false);
                    }
                }
                else if (IsProtocolViolation(e) ||
                         e is TDengineWebSocketRequestException requestException &&
                         requestException.RequestMayHaveBeenSent)
                {
                    await InvalidateConnectionIgnoringErrorsAsync(newConnection,
                        "WSStmtAsync failed to invalidate the connection after uncertain statement initialization")
                        .ConfigureAwait(false);
                }

                throw;
            }
        }

        private static void ValidateStatementResponse(string operation, IWSBaseResp response,
            ulong responseStmtId, ulong expectedStmtId)
        {
            if (response == null)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    operation + " returned an empty response");
            }

            if (expectedStmtId == 0 || responseStmtId == 0 || responseStmtId != expectedStmtId)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    operation + " returned an invalid statement id");
            }
        }

        private static bool IsProtocolViolation(Exception exception)
        {
            return exception is InvalidDataException ||
                   exception is TDengineError error &&
                   error.Code == (int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE;
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

        private struct SynchronousOperationScope : IDisposable
        {
            private readonly WSStmtAsync _owner;

            internal SynchronousOperationScope(WSStmtAsync owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                _owner.ExitSynchronousOperation();
            }
        }
    }
}
