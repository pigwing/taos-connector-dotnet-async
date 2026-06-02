using System;
using System.Collections.Generic;
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
        private readonly Encoding _encoding;
        private readonly BlockReader _blockReader;
        private readonly Func<ulong, CancellationToken, Task<byte[]>> _fetchRawBlockAsync;
        private readonly object _fetchLock = new object();
        private CancellationTokenSource _fetchCts;
        private int _freed;
        private int _currentRow;
        private int _blockSize;
        private byte[] _block;
        private Task<byte[]> _fetchTask;
        private bool _completed;

        public bool HasRows => !_isUpdate;
        public int AffectRows { get; }
        public int FieldCount { get; }

        public WSRowsAsync(int affectedRows)
        {
            _isUpdate = true;
            _completed = true;
            AffectRows = affectedRows;
        }

        public WSRowsAsync(WSQueryResp result, ConnectionAsync connection, TimeZoneInfo tz)
            : this(result.ResultId, result, connection, tz)
        {
        }

        public WSRowsAsync(ulong resultId, IWSMetaResp result, ConnectionAsync connection, TimeZoneInfo tz)
        {
            _connection = connection;
            _resultId = resultId;
            _isUpdate = false;
            AffectRows = -1;
            FieldCount = result.FieldsCount;
            _metas = ParseMetas(result);
            _encoding = Encoding.UTF8;
            _blockReader = new BlockReader(55, FieldCount, result.Precision, result.FieldsTypes,
                result.FieldsScales, tz);
        }

        internal WSRowsAsync(ulong resultId, IWSMetaResp result, TestFetchRawBlockAccessor accessor,
            Func<ulong, CancellationToken, Task<byte[]>> fetchRawBlockAsync, TimeZoneInfo tz)
            : this(resultId, result, (ConnectionAsync)null, tz)
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
            if (Interlocked.Exchange(ref _freed, 1) == 1) return;
            Task<byte[]> pendingFetchTask;
            CancellationTokenSource pendingFetchCts;
            lock (_fetchLock)
            {
                pendingFetchTask = _fetchTask;
                pendingFetchCts = _fetchCts;
                _fetchTask = null;
                _fetchCts = null;
            }

            try
            {
                if (pendingFetchCts != null)
                {
                    pendingFetchCts.Cancel();
                }

                if (_connection != null && _connection.IsAvailable() && !_isUpdate)
                {
                    await _connection.FreeResultAsync(_resultId).ConfigureAwait(false);
                }
            }
            finally
            {
                ObserveFetchTaskAndDisposeCancellationSource(pendingFetchTask, pendingFetchCts);

                _block = null;
                if (_blockReader != null)
                {
                    _blockReader.ClearBlock();
                }
            }
        }

        private static void ObserveFetchTaskAndDisposeCancellationSource(Task task,
            CancellationTokenSource cancellationTokenSource)
        {
            if (task == null)
            {
                cancellationTokenSource?.Dispose();
                return;
            }

            task.ContinueWith(t =>
            {
                try
                {
                    if (t.IsFaulted)
                    {
                        GC.KeepAlive(t.Exception);
                    }
                }
                finally
                {
                    cancellationTokenSource?.Dispose();
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length)
        {
            ThrowIfFreed();
            return _blockReader.GetBytes(_currentRow, ordinal, dataOffset, buffer, bufferOffset, length);
        }

        public char GetChar(int ordinal)
        {
            ThrowIfFreed();
            return _blockReader.GetChar(_currentRow, ordinal);
        }

        public long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length)
        {
            ThrowIfFreed();
            return _blockReader.GetChars(_currentRow, ordinal, dataOffset, buffer, bufferOffset, length);
        }

        public string GetDataTypeName(int ordinal) => _metas[ordinal].TypeName();

        public object GetValue(int ordinal)
        {
            ThrowIfFreed();
            return _blockReader.Read(_currentRow, ordinal);
        }

        public Type GetFieldType(int ordinal) => _metas[ordinal].ScanType();

        public int GetFieldSize(int ordinal) => _metas[ordinal].size;

        public string GetName(int ordinal) => _metas[ordinal].name;

        public int GetFieldPrecision(int ordinal) => _metas[ordinal].precision;

        public int GetFieldScale(int ordinal) => _metas[ordinal].scale;

        public int GetOrdinal(string name) => _metas.FindIndex(m => m.name == name);

        public Task<bool> ReadAsync()
        {
            return ReadAsync(CancellationToken.None);
        }

        public async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            ThrowIfFreed();
            cancellationToken.ThrowIfCancellationRequested();
            if (_isUpdate) return false;
            if (_completed) return false;
            if (_block == null)
            {
                await FetchBlockAsync(cancellationToken).ConfigureAwait(false);
                return !_completed;
            }

            _currentRow += 1;
            if (_currentRow != _blockSize) return true;
            await FetchBlockAsync(cancellationToken).ConfigureAwait(false);
            return !_completed;
        }

        public bool IsDBNull(int ordinal)
        {
            ThrowIfFreed();
            return _blockReader.IsDBNull(_currentRow, ordinal);
        }

        public byte GetByte(int ordinal)
        {
            ThrowIfFreed();
            return _blockReader.GetByte(_currentRow, ordinal);
        }

        public short GetInt16(int ordinal)
        {
            ThrowIfFreed();
            return _blockReader.GetInt16(_currentRow, ordinal);
        }

        public int GetInt32(int ordinal)
        {
            ThrowIfFreed();
            return _blockReader.GetInt32(_currentRow, ordinal);
        }

        public long GetInt64(int ordinal)
        {
            ThrowIfFreed();
            return _blockReader.GetInt64(_currentRow, ordinal);
        }

        public bool GetBoolean(int ordinal)
        {
            ThrowIfFreed();
            return _blockReader.GetBoolean(_currentRow, ordinal);
        }

        public DateTime GetDateTime(int ordinal)
        {
            ThrowIfFreed();
            return _blockReader.GetDateTime(_currentRow, ordinal);
        }

        public decimal GetDecimal(int ordinal)
        {
            ThrowIfFreed();
            return _blockReader.GetDecimal(_currentRow, ordinal);
        }

        public double GetDouble(int ordinal)
        {
            ThrowIfFreed();
            return _blockReader.GetDouble(_currentRow, ordinal);
        }

        public float GetFloat(int ordinal)
        {
            ThrowIfFreed();
            return _blockReader.GetFloat(_currentRow, ordinal);
        }

        public string GetString(int ordinal)
        {
            ThrowIfFreed();
            return _blockReader.GetString(_currentRow, ordinal);
        }

        public int GetValues(object[] values)
        {
            ThrowIfFreed();
            return _blockReader.GetValues(_currentRow, values);
        }

        public DateTimeOffset GetDateTimeOffset(int ordinal)
        {
            ThrowIfFreed();
            return _blockReader.GetDateTimeOffset(_currentRow, ordinal);
        }

        private async Task FetchBlockAsync(CancellationToken cancellationToken)
        {
            var fetchTask = GetOrStartFetchTask(cancellationToken);
            byte[] fetchRawBlockResult;
            try
            {
                fetchRawBlockResult = await WaitWithCancellationAsync(fetchTask, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                ClearFetchTask(fetchTask, observeFault: false);
                _block = null;
                _blockReader.ClearBlock();
                throw;
            }

            ClearFetchTask(fetchTask, observeFault: false);
            ThrowIfFreed();
            try
            {
                ApplyFetchBlock(fetchRawBlockResult);
            }
            catch
            {
                _block = null;
                _blockReader.ClearBlock();
                throw;
            }
        }

        private Task<byte[]> GetOrStartFetchTask(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_fetchLock)
            {
                ThrowIfFreed();
                if (_fetchTask == null)
                {
                    _fetchCts = new CancellationTokenSource();
                    _fetchTask = FetchRawBlockAsync(_fetchCts.Token);
                }

                return _fetchTask;
            }
        }

        private Task<byte[]> FetchRawBlockAsync(CancellationToken cancellationToken)
        {
            if (_fetchRawBlockAsync != null)
            {
                return _fetchRawBlockAsync(_resultId, cancellationToken);
            }

            return _connection.FetchRawBlockBinaryAsync(_resultId, cancellationToken);
        }

        private void ClearFetchTask(Task<byte[]> task = null, bool observeFault = false)
        {
            Task<byte[]> oldTask = null;
            CancellationTokenSource oldCts = null;
            lock (_fetchLock)
            {
                if (task == null || ReferenceEquals(_fetchTask, task))
                {
                    oldTask = _fetchTask;
                    oldCts = _fetchCts;
                    _fetchTask = null;
                    _fetchCts = null;
                }
            }

            if (oldCts != null)
            {
                oldCts.Dispose();
            }

            if (observeFault && oldTask != null)
            {
                ObserveFaultedTask(oldTask);
            }
        }

        private static async Task<T> WaitWithCancellationAsync<T>(Task<T> task, CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled || task.IsCompleted)
            {
                return await task.ConfigureAwait(false);
            }

            var cancellationTask = CreateCancellationTask(cancellationToken, out var registration);
            using (registration)
            {
                if (await Task.WhenAny(task, cancellationTask).ConfigureAwait(false) != task)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            return await task.ConfigureAwait(false);
        }

        private static Task CreateCancellationTask(CancellationToken cancellationToken,
            out CancellationTokenRegistration registration)
        {
            var tcs = CreateCancellationTaskCompletionSource();
            registration = cancellationToken.Register(state =>
            {
                ((TaskCompletionSource<bool>)state).TrySetResult(true);
            }, tcs);
            return tcs.Task;
        }

        private static TaskCompletionSource<bool> CreateCancellationTaskCompletionSource()
        {
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
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

        private void ApplyFetchBlock(byte[] fetchRawBlockResult)
        {
            ValidateFetchBlockLength(fetchRawBlockResult, 42, "header");
            var version = ReadUInt16(fetchRawBlockResult, 16);
            if (version != 1)
                throw new InvalidDataException("Unsupported fetch raw block version " + version);

            var code = ReadUInt32(fetchRawBlockResult, 34);
            var messageLen = ReadUInt32(fetchRawBlockResult, 38);
            if (messageLen > int.MaxValue)
                throw new InvalidDataException("Invalid fetch raw block message length");

            var messageOffset = 42;
            var messageEndOffset = (long)messageOffset + messageLen;
            ValidateFetchBlockLength(fetchRawBlockResult, messageEndOffset, "message");
            if (messageEndOffset > int.MaxValue)
                throw new InvalidDataException("Invalid fetch raw block message length");
            var message = _encoding.GetString(fetchRawBlockResult, messageOffset, (int)messageLen);
            if (code != 0)
                throw new TDengineError((int)code, message);

            var completedOffset = messageEndOffset + 8;
            ValidateFetchBlockLength(fetchRawBlockResult, completedOffset + 1, "completed flag");
            if (completedOffset > int.MaxValue)
                throw new InvalidDataException("Invalid fetch raw block completed flag offset");
            _completed = BitConverter.ToBoolean(fetchRawBlockResult, (int)completedOffset);
            if (_completed)
            {
                _block = null;
                _blockReader.ClearBlock();
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
            _blockReader.SetBlock(_block);
            _blockSize = _blockReader.GetRows();
            _currentRow = 0;
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
    }
}
