using System;
using System.Collections.Generic;
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
        private int _freed;
        private int _currentRow;
        private int _blockSize;
        private byte[] _block;
        private bool _completed;

        public bool HasRows => !_isUpdate;
        public int AffectRows { get; }
        public int FieldCount { get; }

        public WSRowsAsync(int affectedRows)
        {
            _isUpdate = true;
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

        private List<TDengineMeta> ParseMetas(IWSMetaResp result)
        {
            var metaList = new List<TDengineMeta>();
            for (var i = 0; i < FieldCount; i++)
            {
                metaList.Add(new TDengineMeta
                {
                    name = result.FieldsNames[i],
                    type = result.FieldsTypes[i],
                    scale = result.FieldsScales[i],
                    size = (int)result.FieldsLengths[i]
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

            try
            {
                if (_connection != null && _connection.IsAvailable() && !_isUpdate)
                {
                    await _connection.FreeResultAsync(_resultId).ConfigureAwait(false);
                }
            }
            finally
            {
                _block = null;
            }
        }

        public long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length)
        {
            return _blockReader.GetBytes(_currentRow, ordinal, dataOffset, buffer, bufferOffset, length);
        }

        public char GetChar(int ordinal)
        {
            return _blockReader.GetChar(_currentRow, ordinal);
        }

        public long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length)
        {
            return _blockReader.GetChars(_currentRow, ordinal, dataOffset, buffer, bufferOffset, length);
        }

        public string GetDataTypeName(int ordinal) => _metas[ordinal].TypeName();

        public object GetValue(int ordinal)
        {
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
            return _blockReader.IsDBNull(_currentRow, ordinal);
        }

        public byte GetByte(int ordinal)
        {
            return _blockReader.GetByte(_currentRow, ordinal);
        }

        public short GetInt16(int ordinal)
        {
            return _blockReader.GetInt16(_currentRow, ordinal);
        }

        public int GetInt32(int ordinal)
        {
            return _blockReader.GetInt32(_currentRow, ordinal);
        }

        public long GetInt64(int ordinal)
        {
            return _blockReader.GetInt64(_currentRow, ordinal);
        }

        public bool GetBoolean(int ordinal)
        {
            return _blockReader.GetBoolean(_currentRow, ordinal);
        }

        public DateTime GetDateTime(int ordinal)
        {
            return _blockReader.GetDateTime(_currentRow, ordinal);
        }

        public decimal GetDecimal(int ordinal)
        {
            return _blockReader.GetDecimal(_currentRow, ordinal);
        }

        public double GetDouble(int ordinal)
        {
            return _blockReader.GetDouble(_currentRow, ordinal);
        }

        public float GetFloat(int ordinal)
        {
            return _blockReader.GetFloat(_currentRow, ordinal);
        }

        public string GetString(int ordinal)
        {
            return _blockReader.GetString(_currentRow, ordinal);
        }

        public int GetValues(object[] values)
        {
            return _blockReader.GetValues(_currentRow, values);
        }

        public DateTimeOffset GetDateTimeOffset(int ordinal)
        {
            return _blockReader.GetDateTimeOffset(_currentRow, ordinal);
        }

        private async Task FetchBlockAsync(CancellationToken cancellationToken)
        {
            var fetchRawBlockResult = await _connection.FetchRawBlockBinaryAsync(_resultId, cancellationToken)
                .ConfigureAwait(false);
            var version = ReadUInt16(fetchRawBlockResult, 16);
            if (version != 1)
                throw new Exception("Unsupported fetch raw block version " + version);
            var code = ReadUInt32(fetchRawBlockResult, 34);
            var messageLen = ReadUInt32(fetchRawBlockResult, 38);
            var message = _encoding.GetString(fetchRawBlockResult, 42, (int)messageLen);
            if (code != 0)
                throw new TDengineError((int)code, message);
            _completed = BitConverter.ToBoolean(fetchRawBlockResult, 50 + (int)messageLen);
            if (_completed)
            {
                _block = null;
                return;
            }
            var rawBlockLength = ReadUInt32(fetchRawBlockResult, 51 + (int)messageLen);
            if (fetchRawBlockResult.Length != 55 + (int)messageLen + rawBlockLength)
                throw new Exception("Invalid fetch raw block result length");
            _block = fetchRawBlockResult;
            _blockReader.SetBlock(_block);
            _blockSize = _blockReader.GetRows();
            _currentRow = 0;
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
