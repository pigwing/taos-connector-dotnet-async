using System;
using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;

namespace TDengine.Driver.Client.Websocket
{
#if NETSTANDARD2_1_OR_GREATER
public class WSStmtAsync : IStmtAsync, IAsyncDisposable
#else
    public class WSStmtAsync : IStmtAsync, IDisposable
#endif

    {
        private ulong _stmt;

        private readonly TimeZoneInfo _tz;

        private ConnectionAsync _connection;

        private bool _closed;

        private long _lastAffected;

        private bool _isInsert;

        public WSStmtAsync(ulong stmt, TimeZoneInfo tz, ConnectionAsync connection)
        {
            _stmt = stmt;
            _tz = tz;
            _connection = connection;
        }



#if NETSTANDARD2_1_OR_GREATER
public async ValueTask DisposeAsync()
        {
            if (!_closed)
            {
                await _connection.StmtCloseAsync(_stmt);
                _closed = true;
            }
        }
#else
        public void Dispose()
        {
            if (!_closed)
            {
                _ = _connection.StmtCloseAsync(_stmt);
                _closed = true;
            }
        }
#endif


        public async Task PrepareAsync(string query)
        {
            var resp = await _connection.StmtPrepareAsync(_stmt, query);
            _isInsert = resp.IsInsert;
        }

        public bool IsInsert()
        {
            return _isInsert;
        }

        public async Task SetTableNameAsync(string tableName)
        {
            await _connection.StmtSetTableNameAsync(_stmt, tableName);
        }

        public async Task SetTagsAsync(object[] tags)
        {
            var fields = await GetTagFieldsAsync();
            await _connection.StmtSetTagsAsync(_stmt, fields, tags);
        }

        public async Task<TaosFieldE[]> GetTagFieldsAsync()
        {
            var resp = await _connection.StmtGetTagFieldsAsync(_stmt);
            TaosFieldE[] fields = new TaosFieldE[resp.Fields.Count];
            for (int i = 0; i < resp.Fields.Count; i++)
            {
                fields[i] = new TaosFieldE
                {
                    name = resp.Fields[i].Name,
                    type = resp.Fields[i].FieldType,
                    precision = resp.Fields[i].Precision,
                    scale = resp.Fields[i].Scale,
                    bytes = resp.Fields[i].Bytes
                };
            }

            return fields;
        }

        public async Task<TaosFieldE[]> GetColFieldsAsync()
        {
            var resp = await _connection.StmtGetColFieldsAsync(_stmt);
            TaosFieldE[] fields = new TaosFieldE[resp.Fields.Count];
            for (int i = 0; i < resp.Fields.Count; i++)
            {
                fields[i] = new TaosFieldE
                {
                    name = resp.Fields[i].Name,
                    type = resp.Fields[i].FieldType,
                    precision = resp.Fields[i].Precision,
                    scale = resp.Fields[i].Scale,
                    bytes = resp.Fields[i].Bytes
                };
            }

            return fields;
        }

        public async Task BindRowAsync(object[] row)
        {
            if (IsInsert())
            {
                await _connection.StmtBindAsync(_stmt, await GetColFieldsAsync(), row);
            }
            else
            {
                var tmpRow = new object[row.Length];
                Array.Copy(row, tmpRow, row.Length);
                await _connection.StmtBindAsync(_stmt, GenerateStmtQueryColFields(tmpRow), tmpRow);
            }
        }

        private TaosFieldE[] GenerateStmtQueryColFields(object[] row)
        {
            var result = new TaosFieldE[row.Length];
            for (int i = 0; i < row.Length; i++)
            {
                switch (row[i])
                {
                    case bool _:
                        result[i] = new TaosFieldE
                        {
                            type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_BOOL
                        };
                        break;
                    case sbyte _:
                        result[i] = new TaosFieldE
                        {
                            type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_TINYINT
                        };
                        break;
                    case short _:
                        result[i] = new TaosFieldE
                        {
                            type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_SMALLINT
                        };
                        break;
                    case int _:
                        result[i] = new TaosFieldE
                        {
                            type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_INT
                        };
                        break;
                    case long _:
                        result[i] = new TaosFieldE
                        {
                            type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_BIGINT
                        };
                        break;
                    case byte _:
                        result[i] = new TaosFieldE
                        {
                            type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_UTINYINT
                        };
                        break;
                    case ushort _:
                        result[i] = new TaosFieldE
                        {
                            type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_USMALLINT
                        };
                        break;
                    case uint _:
                        result[i] = new TaosFieldE
                        {
                            type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_UINT
                        };
                        break;
                    case ulong _:
                        result[i] = new TaosFieldE
                        {
                            type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_UBIGINT
                        };
                        break;
                    case float _:
                        result[i] = new TaosFieldE
                        {
                            type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_FLOAT
                        };
                        break;
                    case double _:
                        result[i] = new TaosFieldE
                        {
                            type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_DOUBLE
                        };
                        break;
                    case byte[] _:
                        result[i] = new TaosFieldE
                        {
                            type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_BINARY
                        };
                        break;
                    case DateTime val:
                        result[i] = new TaosFieldE
                        {
                            type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_BINARY
                        };
                        var time = val.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK");
                        row[i] = time;
                        break;
                    case string _:
                        result[i] = new TaosFieldE
                        {
                            type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_BINARY
                        };
                        break;
                    default:
                        throw new ArgumentException("Unsupported type, only support basic types and DateTime");
                }
            }

            return result;
        }

        public async Task BindColumnAsync(TaosFieldE[] field, params Array[] arrays)
        {
            await _connection.StmtBindAsync(_stmt, field, arrays);
        }

        public async Task AddBatchAsync()
        {
            await _connection.StmtAddBatchAsync(_stmt);
        }

        public async Task ExecAsync()
        {
            var resp = await _connection.StmtExecAsync(_stmt);
            _lastAffected = resp.Affected;
        }

        public long Affected()
        {
            return _lastAffected;
        }

        public async Task<IRowsAsync> ResultAsync()
        {
            if (IsInsert())
            {
                return new WSRowsAsync((int)_lastAffected);
            }
            var resp = await _connection.StmtUseResultAsync(_stmt);
            return new WSRowsAsync(resp, _connection, _tz);
        }
    }
}
