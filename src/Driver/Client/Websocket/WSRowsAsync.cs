using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;

namespace TDengine.Driver.Client.Websocket
{
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
    public class WSRowsAsync : IRowsAsync, IAsyncDisposable
#else
    public class WSRowsAsync : IRowsAsync, IDisposable
#endif
    {
        private readonly ConnectionAsync _connection;
        private readonly ulong _resultId;
        private readonly bool _isUpdate;
        private readonly List<TDengineMeta> _metas;
        private static readonly Encoding Utf8Encoding = new UTF8Encoding(false, true);
        private readonly BlockReader _blockReader;
        private readonly Func<ulong, CancellationToken, Task<byte[]>> _fetchRawBlockAsync;
        private readonly object _fetchLock = new object();
        private readonly object _disposeLock = new object();
        private readonly object _accessLock = new object();
        private static readonly TimeSpan DefaultDisposeDrainTimeout = TimeSpan.FromSeconds(3);
        private readonly TimeSpan _disposeDrainTimeout;
        private const int MaximumFetchMessageLength = 64 * 1024;
        private FetchOperation _fetchOperation;
        private int _freed;
        private Task _disposeTask;
        private int _currentRow;
        private int _blockSize;
        private byte[] _block;
        private bool _completed;
        private bool _hasCurrentRow;
        private int _readInProgress;
        private int _valueAccessors;
        private int _connectionInvalidated;
        private readonly FetchOutcomeState _fetchOutcomeState = new FetchOutcomeState();
        private TaskCompletionSource<bool> _valueAccessorsDrained;
        private TaskCompletionSource<bool> _readCompleted;

        public bool HasRows => !_isUpdate;
        public int AffectRows { get; }
        public int FieldCount { get; }

        public WSRowsAsync(int affectedRows)
        {
            _isUpdate = true;
            _completed = true;
            AffectRows = affectedRows;
            _metas = new List<TDengineMeta>(0);
            _disposeDrainTimeout = DefaultDisposeDrainTimeout;
        }

        public WSRowsAsync(WSQueryResp result, ConnectionAsync connection, TimeZoneInfo tz)
            : this(GetResultId(result), result, connection, tz, false, DefaultDisposeDrainTimeout)
        {
        }

        public WSRowsAsync(ulong resultId, IWSMetaResp result, ConnectionAsync connection, TimeZoneInfo tz)
            : this(resultId, result, connection, tz, false, DefaultDisposeDrainTimeout)
        {
        }

        internal WSRowsAsync(ulong resultId, IWSMetaResp result, ConnectionAsync connection, TimeZoneInfo tz,
            TimeSpan disposeDrainTimeout)
            : this(resultId, result, connection, tz, false, disposeDrainTimeout)
        {
        }

        private WSRowsAsync(ulong resultId, IWSMetaResp result, ConnectionAsync connection, TimeZoneInfo tz,
            bool allowNullConnection, TimeSpan disposeDrainTimeout)
        {
            if (resultId == 0) throw new ArgumentOutOfRangeException(nameof(resultId));
            if (!allowNullConnection && connection == null) throw new ArgumentNullException(nameof(connection));
            ValidateMetadata(result);
            _connection = connection;
            _resultId = resultId;
            _isUpdate = false;
            AffectRows = -1;
            FieldCount = result.FieldsCount;
            _metas = ParseMetas(result);
            _blockReader = new BlockReader(55, FieldCount, result.Precision, result.FieldsTypes,
                result.FieldsScales, tz);
            TimeoutHelper.ValidateTimerTimeout(disposeDrainTimeout, nameof(disposeDrainTimeout), false);
            _disposeDrainTimeout = disposeDrainTimeout;
        }

        internal WSRowsAsync(ulong resultId, IWSMetaResp result, TestFetchRawBlockAccessor accessor,
            Func<ulong, CancellationToken, Task<byte[]>> fetchRawBlockAsync, TimeZoneInfo tz)
            : this(resultId, result, accessor, fetchRawBlockAsync, tz, DefaultDisposeDrainTimeout)
        {
        }

        internal WSRowsAsync(ulong resultId, IWSMetaResp result, TestFetchRawBlockAccessor accessor,
            Func<ulong, CancellationToken, Task<byte[]>> fetchRawBlockAsync, TimeZoneInfo tz,
            TimeSpan disposeDrainTimeout)
            : this(resultId, result, (ConnectionAsync)null, tz, true, disposeDrainTimeout)
        {
            if (accessor == null) throw new ArgumentNullException(nameof(accessor));
            _fetchRawBlockAsync = fetchRawBlockAsync ?? throw new ArgumentNullException(nameof(fetchRawBlockAsync));
        }

        internal sealed class TestFetchRawBlockAccessor
        {
            public static readonly TestFetchRawBlockAccessor Instance = new TestFetchRawBlockAccessor();

            private TestFetchRawBlockAccessor()
            {
            }
        }

        private List<TDengineMeta> ParseMetas(IWSMetaResp result)
        {
            var metaList = new List<TDengineMeta>(FieldCount);
            for (var i = 0; i < FieldCount; i++)
            {
                metaList.Add(new TDengineMeta
                {
                    name = result.FieldsNames[i],
                    type = result.FieldsTypes[i],
                    size = (int)result.FieldsLengths[i],
                    precision = (result.FieldsPrecisions != null && i < result.FieldsPrecisions.Length)
                        ? result.FieldsPrecisions[i]
                        : (byte)0,
                    scale = (result.FieldsScales != null && i < result.FieldsScales.Length)
                        ? result.FieldsScales[i]
                        : (byte)0
                });
            }

            return metaList;
        }

        private static ulong GetResultId(WSQueryResp result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            return result.ResultId;
        }

        private static void ValidateMetadata(IWSMetaResp result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            if (result.FieldsCount < 0)
            {
                throw new InvalidDataException("The WebSocket result contains a negative field count.");
            }

            if (result.FieldsCount > BlockReader.MaximumColumnCount)
            {
                throw new InvalidDataException(
                    $"The WebSocket result exceeds the supported field count of {BlockReader.MaximumColumnCount}.");
            }

            ValidateRequiredMetadataArray(result.FieldsNames, result.FieldsCount, "field names");
            ValidateRequiredMetadataArray(result.FieldsTypes, result.FieldsCount, "field types");
            ValidateRequiredMetadataArray(result.FieldsLengths, result.FieldsCount, "field lengths");
            ValidateOptionalMetadataArray(result.FieldsPrecisions, result.FieldsCount, "field precisions");
            ValidateOptionalMetadataArray(result.FieldsScales, result.FieldsCount, "field scales");

            for (var i = 0; i < result.FieldsCount; i++)
            {
                if (result.FieldsNames[i] == null)
                {
                    throw new InvalidDataException($"The WebSocket result field name at index {i} is null.");
                }

                if (!TDengineConstant.IsSupportedDataType(result.FieldsTypes[i]))
                {
                    throw new InvalidDataException(
                        $"The WebSocket result field type at index {i} is unsupported.");
                }

                if (result.FieldsLengths[i] < 0 || result.FieldsLengths[i] > int.MaxValue)
                {
                    throw new InvalidDataException(
                        $"The WebSocket result field length at index {i} is outside the supported range.");
                }

                if (result.FieldsTypes[i] == (byte)TDengineDataType.TSDB_DATA_TYPE_TIMESTAMP &&
                    result.FieldsPrecisions != null && i < result.FieldsPrecisions.Length &&
                    !TDengineConstant.IsValidTimestampPrecision(result.FieldsPrecisions[i]))
                {
                    throw new InvalidDataException(
                        $"The WebSocket result timestamp precision at index {i} is invalid.");
                }

                if (result.FieldsTypes[i] == (byte)TDengineDataType.TSDB_DATA_TYPE_DECIMAL ||
                    result.FieldsTypes[i] == (byte)TDengineDataType.TSDB_DATA_TYPE_DECIMAL64)
                {
                    if (result.FieldsPrecisions == null || i >= result.FieldsPrecisions.Length ||
                        result.FieldsScales == null || i >= result.FieldsScales.Length ||
                        !TDengineConstant.IsValidDecimalMetadata(result.FieldsTypes[i],
                            result.FieldsPrecisions[i], result.FieldsScales[i]))
                    {
                        throw new InvalidDataException(
                            $"The WebSocket result decimal metadata at index {i} is invalid.");
                    }
                }
            }

            if (!TDengineConstant.IsValidTimestampPrecision(result.Precision))
            {
                throw new InvalidDataException("The WebSocket result timestamp precision is invalid.");
            }
        }

        private static void ValidateRequiredMetadataArray<T>(T[] values, int fieldCount, string name)
        {
            if (values == null || values.Length != fieldCount)
            {
                throw new InvalidDataException(
                    $"The WebSocket result {name} length does not match fields_count ({fieldCount}).");
            }
        }

        private static void ValidateOptionalMetadataArray<T>(T[] values, int fieldCount, string name)
        {
            if (values != null && values.Length > fieldCount)
            {
                throw new InvalidDataException(
                    $"The WebSocket result {name} length exceeds fields_count ({fieldCount}).");
            }
        }

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
        public async ValueTask DisposeAsync()
        {
            await FreeAsync(CancellationToken.None).ConfigureAwait(false);
        }

        public void Dispose()
        {
            FreeAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
#else
        public void Dispose()
        {
            FreeAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
#endif

        private async Task FreeAsync(CancellationToken cancellationToken)
        {
            Task disposeTask;
            lock (_disposeLock)
            {
                if (_disposeTask == null)
                {
                    _disposeTask = FreeOnceAsync(cancellationToken);
                }

                disposeTask = _disposeTask;
            }

            await disposeTask.ConfigureAwait(false);
        }

        private async Task FreeOnceAsync(CancellationToken cancellationToken)
        {
            using (var cleanupCts = cancellationToken.CanBeCanceled
                       ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                       : new CancellationTokenSource())
            {
                cleanupCts.CancelAfter(_disposeDrainTimeout);
                await FreeOnceCoreAsync(cleanupCts.Token).ConfigureAwait(false);
            }
        }

        private async Task FreeOnceCoreAsync(CancellationToken cleanupToken)
        {
            lock (_accessLock)
            {
                Volatile.Write(ref _freed, 1);
            }

            FetchOperation fetchOperationToCancel;
            lock (_fetchLock)
            {
                fetchOperationToCancel = _fetchOperation;
            }

            fetchOperationToCancel?.Cancel();

            var readCompletion = WaitForReadCompletionAsync();
            var accessorCompletion = WaitForValueAccessorsAsync();
            var drained = await WaitForDrainAsync(Task.WhenAll(readCompletion, accessorCompletion), cleanupToken)
                .ConfigureAwait(false);
            var invalidateConnection = !drained || IsFetchOutcomeUnknown();
            if (!drained)
            {
                Trace.TraceWarning("WSRowsAsync timed out waiting for an active read or value accessor.");
            }

            FetchOperation pendingFetch;
            lock (_fetchLock)
            {
                pendingFetch = _fetchOperation;
                _fetchOperation = null;
            }

            if (pendingFetch != null)
            {
                var fetchCompleted = await WaitForDrainAsync(pendingFetch.Task, cleanupToken).ConfigureAwait(false);
                if (!fetchCompleted)
                {
                    Trace.TraceWarning("WSRowsAsync timed out waiting for a pending fetch.");
                    invalidateConnection = true;
                    ObserveFetchCompletion(pendingFetch, true);
                    pendingFetch = null;
                }
                else
                {
                    try
                    {
                        await pendingFetch.Task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (pendingFetch.Token.IsCancellationRequested)
                    {
                        // The fetch token is owned by the rows object and is
                        // canceled during disposal. A cancellation does not
                        // prove that the server did not receive the request,
                        // so do not reuse this physical connection.
                        invalidateConnection = true;
                    }
                    catch (Exception e)
                    {
                        MarkFetchOutcomeUnknown(e);
                        invalidateConnection = invalidateConnection || IsFetchOutcomeUnknown();
                        Trace.TraceWarning("WSRowsAsync pending fetch failed during cleanup: " +
                                           e.GetType().Name);
                    }

                    pendingFetch.DisposeCancellationSource();
                }
            }

            invalidateConnection = invalidateConnection || IsFetchOutcomeUnknown();

            if (drained)
            {
                ClearRowsState();
            }
            else
            {
                var deferredCleanup = ClearRowsStateWhenDrainedAsync(readCompletion, accessorCompletion);
                ObserveFaultedTask(deferredCleanup);
            }

            if (invalidateConnection)
            {
                await InvalidateConnectionAsync().ConfigureAwait(false);
            }
            else if (_connection != null && !_isUpdate)
            {
                try
                {
                    if (_connection.IsAvailable())
                    {
                        await _connection.FreeResultAsync(_resultId, cleanupToken).ConfigureAwait(false);
                    }
                }
                catch (Exception e)
                {
                    Trace.TraceWarning("WSRowsAsync failed to release the server result: " + e.GetType().Name);
                    await InvalidateConnectionAsync().ConfigureAwait(false);
                }
            }
        }

        private async Task InvalidateConnectionAsync()
        {
            if (_connection == null || Interlocked.Exchange(ref _connectionInvalidated, 1) != 0)
            {
                return;
            }

            try
            {
                await _connection.InvalidateAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Trace.TraceWarning("WSRowsAsync failed to invalidate the WebSocket connection: " +
                                   e.GetType().Name);
            }
        }

        public long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.GetBytes(_currentRow, ordinal, dataOffset, buffer, bufferOffset, length);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public char GetChar(int ordinal)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.GetChar(_currentRow, ordinal);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.GetChars(_currentRow, ordinal, dataOffset, buffer, bufferOffset, length);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public string GetDataTypeName(int ordinal)
        {
            ValidateOrdinal(ordinal);
            return _metas[ordinal].TypeName();
        }

        public object GetValue(int ordinal)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.Read(_currentRow, ordinal);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public Type GetFieldType(int ordinal)
        {
            ValidateOrdinal(ordinal);
            return _metas[ordinal].ScanType();
        }

        public int GetFieldSize(int ordinal)
        {
            ValidateOrdinal(ordinal);
            return _metas[ordinal].size;
        }

        public string GetName(int ordinal)
        {
            ValidateOrdinal(ordinal);
            return _metas[ordinal].name;
        }

        public int GetFieldPrecision(int ordinal)
        {
            ValidateOrdinal(ordinal);
            return _metas[ordinal].precision;
        }

        public int GetFieldScale(int ordinal)
        {
            ValidateOrdinal(ordinal);
            return _metas[ordinal].scale;
        }

        public int GetOrdinal(string name)
        {
            ThrowIfFreed();
            if (name == null) throw new ArgumentNullException(nameof(name));
            for (var i = 0; i < _metas.Count; i++)
            {
                if (string.Equals(_metas[i].name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        public Task<bool> ReadAsync()
        {
            return ReadAsync(CancellationToken.None);
        }

        public async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            lock (_accessLock)
            {
                ThrowIfFreed();
                if (_readInProgress != 0)
                {
                    throw new InvalidOperationException(
                        "Concurrent ReadAsync calls on the same rows object are not supported.");
                }

                if (_valueAccessors != 0)
                {
                    throw new InvalidOperationException(
                        "ReadAsync cannot run while row values are being accessed.");
                }

                _readInProgress = 1;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_isUpdate || _completed)
                {
                    _hasCurrentRow = false;
                    return false;
                }

                if (_block == null)
                {
                    _hasCurrentRow = false;
                    await FetchBlockAsync(cancellationToken).ConfigureAwait(false);
                    return _hasCurrentRow;
                }

                var nextRow = _currentRow + 1;
                if (nextRow < _blockSize)
                {
                    _currentRow = nextRow;
                    _hasCurrentRow = true;
                    return true;
                }

                _hasCurrentRow = false;
                ReleaseCurrentBlock();
                await FetchBlockAsync(cancellationToken).ConfigureAwait(false);
                return _hasCurrentRow;
            }
            finally
            {
                TaskCompletionSource<bool> completed = null;
                lock (_accessLock)
                {
                    _readInProgress = 0;
                    if (_freed != 0)
                    {
                        completed = _readCompleted;
                    }
                }

                completed?.TrySetResult(true);
            }
        }

        public bool IsDBNull(int ordinal)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.IsDBNull(_currentRow, ordinal);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public byte GetByte(int ordinal)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.GetByte(_currentRow, ordinal);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public short GetInt16(int ordinal)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.GetInt16(_currentRow, ordinal);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public int GetInt32(int ordinal)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.GetInt32(_currentRow, ordinal);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public long GetInt64(int ordinal)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.GetInt64(_currentRow, ordinal);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public bool GetBoolean(int ordinal)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.GetBoolean(_currentRow, ordinal);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public DateTime GetDateTime(int ordinal)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.GetDateTime(_currentRow, ordinal);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public decimal GetDecimal(int ordinal)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.GetDecimal(_currentRow, ordinal);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public double GetDouble(int ordinal)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.GetDouble(_currentRow, ordinal);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public float GetFloat(int ordinal)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.GetFloat(_currentRow, ordinal);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public string GetString(int ordinal)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.GetString(_currentRow, ordinal);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public int GetValues(object[] values)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.GetValues(_currentRow, values);
            }
            finally
            {
                EndValueAccess();
            }
        }

        public DateTimeOffset GetDateTimeOffset(int ordinal)
        {
            BeginValueAccess();
            try
            {
                EnsureCurrentRow();
                return _blockReader.GetDateTimeOffset(_currentRow, ordinal);
            }
            finally
            {
                EndValueAccess();
            }
        }

        private void BeginValueAccess()
        {
            lock (_accessLock)
            {
                ThrowIfFreed();
                if (_readInProgress != 0)
                {
                    throw new InvalidOperationException(
                        "Row values cannot be accessed while ReadAsync is running.");
                }

                checked
                {
                    _valueAccessors++;
                }
            }
        }

        private void EndValueAccess()
        {
            TaskCompletionSource<bool> drained = null;
            lock (_accessLock)
            {
                _valueAccessors--;
                if (_valueAccessors == 0 && _freed != 0)
                {
                    drained = _valueAccessorsDrained;
                }
            }

            drained?.TrySetResult(true);
        }

        private Task WaitForValueAccessorsAsync()
        {
            lock (_accessLock)
            {
                if (_valueAccessors == 0)
                {
                    return CompletedTask();
                }

                if (_valueAccessorsDrained == null)
                {
                    _valueAccessorsDrained = CreateValueAccessorCompletionSource();
                }

                return _valueAccessorsDrained.Task;
            }
        }

        private Task WaitForReadCompletionAsync()
        {
            lock (_accessLock)
            {
                if (_readInProgress == 0)
                {
                    return CompletedTask();
                }

                if (_readCompleted == null)
                {
                    _readCompleted = CreateValueAccessorCompletionSource();
                }

                return _readCompleted.Task;
            }
        }

        private static TaskCompletionSource<bool> CreateValueAccessorCompletionSource()
        {
#if NET5_0_OR_GREATER || NETSTANDARD2_0_OR_GREATER
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
#else
            return new TaskCompletionSource<bool>();
#endif
        }

        private static Task CompletedTask()
        {
#if NET45 || NET451
            return Task.FromResult(0);
#else
            return Task.CompletedTask;
#endif
        }

        private static async Task<bool> WaitForDrainAsync(Task task, CancellationToken timeoutToken)
        {
            if (task.IsCompleted)
            {
                return true;
            }

            var timeoutTask = Task.Delay(Timeout.Infinite, timeoutToken);
            if (await Task.WhenAny(task, timeoutTask).ConfigureAwait(false) != task)
            {
                return false;
            }

            return true;
        }

        private async Task ClearRowsStateWhenDrainedAsync(Task readCompletion, Task accessorCompletion)
        {
            try
            {
                await Task.WhenAll(readCompletion, accessorCompletion).ConfigureAwait(false);
            }
            finally
            {
                ClearRowsState();
            }
        }

        private void ClearRowsState()
        {
            lock (_accessLock)
            {
                _block = null;
                _blockSize = 0;
                _currentRow = 0;
                _hasCurrentRow = false;
                _blockReader?.ClearBlock();
            }
        }

        private void ObserveFetchCompletion(FetchOperation operation, bool assumeOutcomeUnknown)
        {
            if (!operation.TryBeginObservation())
            {
                return;
            }

            var observation = new FetchCompletionObservation(_fetchOutcomeState, operation,
                assumeOutcomeUnknown);
            operation.Task.ContinueWith((completed, state) =>
                {
                    var fetchObservation = (FetchCompletionObservation)state;
                    if (fetchObservation.AssumeOutcomeUnknown && completed.IsCanceled)
                    {
                        Volatile.Write(ref fetchObservation.OutcomeState.Unknown, 1);
                    }

                    if (completed.IsFaulted)
                    {
                        MarkFetchOutcomeUnknown(fetchObservation.OutcomeState, completed.Exception);
                    }

                    GC.KeepAlive(completed.Exception);
                    fetchObservation.Operation.DisposeCancellationSource();
                }, observation, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task FetchBlockAsync(CancellationToken cancellationToken)
        {
            var fetchOperation = GetOrStartFetchOperation(cancellationToken);
            byte[] fetchRawBlockResult;
            try
            {
                fetchRawBlockResult = await WaitWithCancellationAsync(fetchOperation.Task, cancellationToken,
                        fetchOperation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The fetch itself continues on its private token. Observe its
                // eventual result so a late transport failure cannot be lost
                // while the rows object remains alive.
                ObserveFetchCompletion(fetchOperation, true);
                throw;
            }
            catch (OperationCanceledException) when (fetchOperation.Token.IsCancellationRequested)
            {
                ThrowIfFreed();
                throw;
            }
            catch (Exception e)
            {
                MarkFetchOutcomeUnknown(e);
                ClearFetchOperation(fetchOperation, observeFault: false);
                ReleaseCurrentBlock();
                throw;
            }

            ClearFetchOperation(fetchOperation, observeFault: false);
            ThrowIfFreed();
            try
            {
                ApplyFetchBlock(fetchRawBlockResult);
            }
            catch (TDengineError)
            {
                ReleaseCurrentBlock();
                throw;
            }
            catch
            {
                ReleaseCurrentBlock();
                await InvalidateConnectionAsync().ConfigureAwait(false);
                throw;
            }
        }

        private FetchOperation GetOrStartFetchOperation(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_fetchLock)
            {
                ThrowIfFreed();
                if (_fetchOperation == null)
                {
                    var fetchCts = new CancellationTokenSource();
                    try
                    {
                        var fetchToken = fetchCts.Token;
                        var fetchTask = FetchRawBlockAsync(fetchToken);
                        if (fetchTask == null)
                        {
                            throw new InvalidOperationException("The fetch delegate returned a null task.");
                        }

                        _fetchOperation = new FetchOperation(fetchTask, fetchCts, fetchToken);
                    }
                    catch
                    {
                        fetchCts.Dispose();
                        throw;
                    }
                }

                return _fetchOperation;
            }
        }

        private Task<byte[]> FetchRawBlockAsync(CancellationToken cancellationToken)
        {
            if (_fetchRawBlockAsync != null)
            {
                return _fetchRawBlockAsync(_resultId, cancellationToken);
            }

            if (_connection == null)
            {
                throw new InvalidOperationException("This rows object does not have a WebSocket connection.");
            }

            return _connection.FetchRawBlockBinaryAsync(_resultId, cancellationToken);
        }

        private void MarkFetchOutcomeUnknown(Exception exception)
        {
            MarkFetchOutcomeUnknown(_fetchOutcomeState, exception);
        }

        private static void MarkFetchOutcomeUnknown(FetchOutcomeState outcomeState, Exception exception)
        {
            if (exception is AggregateException aggregateException)
            {
                foreach (var innerException in aggregateException.Flatten().InnerExceptions)
                {
                    MarkFetchOutcomeUnknown(outcomeState, innerException);
                }

                return;
            }

            if (exception is TDengineWebSocketRequestException requestException &&
                requestException.RequestMayHaveBeenSent)
            {
                Volatile.Write(ref outcomeState.Unknown, 1);
            }
        }

        private bool IsFetchOutcomeUnknown()
        {
            return Volatile.Read(ref _fetchOutcomeState.Unknown) != 0;
        }

        private void ClearFetchOperation(FetchOperation operation = null, bool observeFault = false)
        {
            FetchOperation oldOperation = null;
            lock (_fetchLock)
            {
                if (operation == null || ReferenceEquals(_fetchOperation, operation))
                {
                    oldOperation = _fetchOperation;
                    _fetchOperation = null;
                }
            }

            oldOperation?.DisposeCancellationSource();

            if (observeFault && oldOperation != null)
            {
                ObserveFaultedTask(oldOperation.Task);
            }
        }

        private static async Task<T> WaitWithCancellationAsync<T>(Task<T> task,
            CancellationToken cancellationToken, CancellationToken fetchCancellationToken)
        {
            if ((!cancellationToken.CanBeCanceled && !fetchCancellationToken.CanBeCanceled) || task.IsCompleted)
            {
                return await task.ConfigureAwait(false);
            }

            var cancellationTask = CreateCancellationTask(cancellationToken, fetchCancellationToken,
                out var callerRegistration, out var fetchRegistration);
            using (callerRegistration)
            using (fetchRegistration)
            {
                if (await Task.WhenAny(task, cancellationTask).ConfigureAwait(false) != task)
                {
                    var canceledToken = cancellationToken.IsCancellationRequested
                        ? cancellationToken
                        : fetchCancellationToken;
                    throw new OperationCanceledException(canceledToken);
                }
            }

            return await task.ConfigureAwait(false);
        }

        private static Task CreateCancellationTask(CancellationToken cancellationToken,
            CancellationToken fetchCancellationToken, out CancellationTokenRegistration callerRegistration,
            out CancellationTokenRegistration fetchRegistration)
        {
            var tcs = CreateCancellationTaskCompletionSource();
            callerRegistration = RegisterCancellation(cancellationToken, tcs);
            fetchRegistration = RegisterCancellation(fetchCancellationToken, tcs);
            return tcs.Task;
        }

        private static CancellationTokenRegistration RegisterCancellation(CancellationToken cancellationToken,
            TaskCompletionSource<bool> completion)
        {
            return cancellationToken.CanBeCanceled
                ? cancellationToken.Register(state =>
                {
                    ((TaskCompletionSource<bool>)state).TrySetResult(true);
                }, completion)
                : default(CancellationTokenRegistration);
        }

        private static TaskCompletionSource<bool> CreateCancellationTaskCompletionSource()
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

        private sealed class FetchOutcomeState
        {
            internal int Unknown;
        }

        private sealed class FetchOperation
        {
            private readonly CancellationTokenSource _cancellationSource;
            private int _cancellationSourceDisposed;
            private int _observationStarted;

            internal FetchOperation(Task<byte[]> task, CancellationTokenSource cancellationSource,
                CancellationToken token)
            {
                Task = task;
                _cancellationSource = cancellationSource;
                Token = token;
            }

            internal Task<byte[]> Task { get; }

            internal CancellationToken Token { get; }

            internal void Cancel()
            {
                try
                {
                    _cancellationSource.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // A late-completion observer may already have released the source.
                }
                catch (Exception e)
                {
                    Trace.TraceWarning("WSRowsAsync failed to cancel a pending fetch: " + e.GetType().Name);
                }
            }

            internal bool TryBeginObservation()
            {
                return Interlocked.Exchange(ref _observationStarted, 1) == 0;
            }

            internal void DisposeCancellationSource()
            {
                if (Interlocked.Exchange(ref _cancellationSourceDisposed, 1) == 0)
                {
                    _cancellationSource.Dispose();
                }
            }
        }

        private sealed class FetchCompletionObservation
        {
            internal FetchCompletionObservation(FetchOutcomeState outcomeState,
                FetchOperation operation, bool assumeOutcomeUnknown)
            {
                OutcomeState = outcomeState;
                Operation = operation;
                AssumeOutcomeUnknown = assumeOutcomeUnknown;
            }

            internal FetchOutcomeState OutcomeState { get; }

            internal FetchOperation Operation { get; }

            internal bool AssumeOutcomeUnknown { get; }
        }

        private void ApplyFetchBlock(byte[] fetchRawBlockResult)
        {
            ValidateFetchBlockLength(fetchRawBlockResult, 42, "header");
            var version = ReadUInt16(fetchRawBlockResult, 16);
            if (version != 1)
                throw new InvalidDataException("Unsupported fetch raw block version " + version);

            var code = ReadUInt32(fetchRawBlockResult, 34);
            var messageLen = ReadUInt32(fetchRawBlockResult, 38);
            if (messageLen > MaximumFetchMessageLength)
                throw new InvalidDataException("Invalid fetch raw block message length");

            var messageOffset = 42;
            var messageEndOffset = (long)messageOffset + messageLen;
            ValidateFetchBlockLength(fetchRawBlockResult, messageEndOffset, "message");
            if (messageEndOffset > int.MaxValue)
                throw new InvalidDataException("Invalid fetch raw block message length");
            if (code != 0)
            {
                if (code > int.MaxValue)
                {
                    throw new InvalidDataException("Invalid fetch raw block error code");
                }

                var message = Utf8Encoding.GetString(fetchRawBlockResult, messageOffset, (int)messageLen);
                throw new TDengineError((int)code, message);
            }

            var resultIdOffset = messageEndOffset;
            ValidateFetchBlockLength(fetchRawBlockResult, resultIdOffset + sizeof(ulong), "result id");
            if (resultIdOffset > int.MaxValue ||
                ReadUInt64(fetchRawBlockResult, (int)resultIdOffset) != _resultId)
            {
                throw new InvalidDataException("Invalid fetch raw block result id");
            }

            var completedOffset = resultIdOffset + sizeof(ulong);
            ValidateFetchBlockLength(fetchRawBlockResult, completedOffset + 1, "completed flag");
            if (completedOffset > int.MaxValue)
                throw new InvalidDataException("Invalid fetch raw block completed flag offset");
            var completedValue = fetchRawBlockResult[(int)completedOffset];
            if (completedValue > 1)
            {
                throw new InvalidDataException("Invalid fetch raw block completed flag.");
            }

            _completed = completedValue == 1;
            if (_completed)
            {
                if (fetchRawBlockResult.Length != completedOffset + 1)
                {
                    throw new InvalidDataException("Invalid completed fetch raw block result length");
                }

                _hasCurrentRow = false;
                ReleaseCurrentBlock();
                return;
            }

            var rawBlockLengthOffset = completedOffset + 1;
            ValidateFetchBlockLength(fetchRawBlockResult, rawBlockLengthOffset + sizeof(uint), "raw block length");
            if (rawBlockLengthOffset > int.MaxValue)
                throw new InvalidDataException("Invalid fetch raw block length offset");
            var rawBlockLength = ReadUInt32(fetchRawBlockResult, (int)rawBlockLengthOffset);
            var expectedLength = (long)rawBlockLengthOffset + sizeof(uint) + rawBlockLength;
            if (expectedLength > int.MaxValue || fetchRawBlockResult.Length != expectedLength)
                throw new InvalidDataException("Invalid fetch raw block result length");

            _block = fetchRawBlockResult;
            _blockReader.SetBlock(_block, checked((int)rawBlockLengthOffset + sizeof(uint)));
            _blockSize = _blockReader.GetRows();
            if (_blockSize <= 0)
            {
                throw new InvalidDataException("A non-completed fetch response contains no rows.");
            }

            _currentRow = 0;
            _hasCurrentRow = true;
        }

        private void ReleaseCurrentBlock()
        {
            _block = null;
            _blockSize = 0;
            _currentRow = 0;
            _blockReader?.ClearBlock();
        }

        private static void ValidateFetchBlockLength(byte[] bytes, long requiredLength, string segment)
        {
            if (bytes == null || requiredLength > int.MaxValue || bytes.Length < requiredLength)
            {
                throw new InvalidDataException($"Invalid fetch raw block {segment} length");
            }
        }

        private void ThrowIfFreed()
        {
            if (Volatile.Read(ref _freed) == 1)
            {
                throw new ObjectDisposedException(nameof(WSRowsAsync));
            }
        }

        private void EnsureCurrentRow()
        {
            ThrowIfFreed();
            if (!_hasCurrentRow)
            {
                throw new InvalidOperationException("ReadAsync must return true before row values can be accessed.");
            }
        }

        private void ValidateOrdinal(int ordinal)
        {
            ThrowIfFreed();
            if (ordinal < 0 || ordinal >= FieldCount)
            {
                throw new ArgumentOutOfRangeException(nameof(ordinal),
                    $"Value must be between 0 and {FieldCount - 1}.");
            }
        }

        private static ushort ReadUInt16(byte[] source, int offset)
        {
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
                source.AsSpan(offset, sizeof(ushort)));
#else
            return (ushort)(source[offset] | (source[offset + 1] << 8));
#endif
        }

        private static uint ReadUInt32(byte[] source, int offset)
        {
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                source.AsSpan(offset, sizeof(uint)));
#else
            return (uint)(source[offset]
                          | (source[offset + 1] << 8)
                          | (source[offset + 2] << 16)
                          | (source[offset + 3] << 24));
#endif
        }

        private static ulong ReadUInt64(byte[] source, int offset)
        {
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                source.AsSpan(offset, sizeof(ulong)));
#else
            return (ulong)source[offset]
                   | ((ulong)source[offset + 1] << 8)
                   | ((ulong)source[offset + 2] << 16)
                   | ((ulong)source[offset + 3] << 24)
                   | ((ulong)source[offset + 4] << 32)
                   | ((ulong)source[offset + 5] << 40)
                   | ((ulong)source[offset + 6] << 48)
                   | ((ulong)source[offset + 7] << 56);
#endif
        }
    }
}
