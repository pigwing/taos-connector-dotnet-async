using System;
using System.Net.WebSockets;
using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods;

namespace TDengine.Driver.Client.Websocket
{
#if NETSTANDARD2_1_OR_GREATER
public class WSClientAsync : ITDengineClientAsync, IAsyncDisposable
#else
    public class WSClientAsync : ITDengineClientAsync, IDisposable
#endif
    {
        private ConnectionAsync _connection;

        private readonly TimeZoneInfo _tz;

        private readonly ConnectionStringBuilder _builder;

        public WebSocketState State => _connection.State;

        public WSClientAsync(ConnectionStringBuilder builder)
        {
            _tz = builder.Timezone;
            _connection = new ConnectionAsync(GetUrl(builder), builder.Username, builder.Password, builder.Database, builder.ConnTimeout, builder.ReadTimeout, builder.WriteTimeout, builder.EnableCompression);
            _builder = builder;
        }

        private static string GetUrl(ConnectionStringBuilder builder)
        {
            var schema = "ws";
            var port = builder.Port;
            if (builder.UseSSL)
            {
                schema = "wss";
                if (builder.Port == 0)
                {
                    port = 443;
                }
            }
            else
            {
                if (builder.Port == 0)
                {
                    port = 6041;
                }
            }

            if (string.IsNullOrEmpty(builder.Token))
            {
                return $"{schema}://{builder.Host}:{port}/ws";
            }
            else
            {
                return $"{schema}://{builder.Host}:{port}/ws?token={builder.Token}";
            }
        }


#if NETSTANDARD2_1_OR_GREATER
     public async ValueTask DisposeAsync()
        {
            if (_connection != null)
            {
                await _connection.CloseAsync().ConfigureAwait(false);
                _connection = null;
            }
        }   
#else
        public void Dispose()
        {
            if (_connection != null)
            {
                _ = _connection.CloseAsync();
                _connection = null;
            }
        }
#endif


        public async Task ConnectAsync()
        {
            await _connection.ConnectAsync();
        }

        private async Task ReconnectAsync()
        {
            if (!_builder.AutoReconnect)
                return;

            ConnectionAsync connection = null;
            for (int i = 0; i < _builder.ReconnectRetryCount; i++)
            {
                try
                {
                    // sleep
                    await Task.Delay(_builder.ReconnectIntervalMs);
                    connection = new ConnectionAsync(GetUrl(_builder), _builder.Username, _builder.Password,
                        _builder.Database, _builder.ConnTimeout, _builder.ReadTimeout, _builder.WriteTimeout,
                        _builder.EnableCompression);
                    await connection.ConnectAsync();
                    break;
                }
                catch (Exception)
                {
                    if (connection != null)
                    {
                        await connection.CloseAsync();
                        connection = null;
                    }
                }
            }

            if (connection == null)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_RECONNECT_FAILED,
                    "websocket connection reconnect failed");
            }

            if (_connection != null)
            {
                await _connection.CloseAsync();
            }

            _connection = connection;
        }

        public async Task<IStmtAsync> StmtInitAsync()
        {
            return await StmtInitAsync(ReqId.GetReqId());
        }

        public async Task<IStmtAsync> StmtInitAsync(long reqId)
        {
            try
            {
                return await DoStmtInitAsync(reqId);
            }
            catch (Exception e)
            {
                if (_connection.IsAvailable(e))
                {
                    throw;
                }

                await ReconnectAsync();
                return await DoStmtInitAsync(reqId);
            }
        }

        private async Task<IStmtAsync> DoStmtInitAsync(long reqId)
        {
            var resp = await _connection.StmtInitAsync((ulong)reqId);
            return new WSStmtAsync(resp.StmtId, _tz, _connection);
        }

        public async Task<IRowsAsync> QueryAsync(string query)
        {
            return await QueryAsync(query, ReqId.GetReqId());
        }

        public async Task<IRowsAsync> QueryAsync(string query, long reqId)
        {
            try
            {
                return await DoQueryAsync(query, reqId);
            }
            catch (Exception e)
            {
                if (_connection.IsAvailable(e))
                {
                    throw;
                }

                await ReconnectAsync();
                return await DoQueryAsync(query, reqId);
            }
        }

        private async Task<IRowsAsync> DoQueryAsync(string query, long reqId)
        {
            var resp = await _connection.BinaryQueryAsync(query, (ulong)reqId);
            if (resp.IsUpdate)
            {
                return new WSRowsAsync(resp.AffectedRows);
            }
            return new WSRowsAsync(resp, _connection, _tz);
        }

        public async Task<long> ExecAsync(string query)
        {
            return await ExecAsync(query, ReqId.GetReqId());
        }

        public async Task<long> ExecAsync(string query, long reqId)
        {
            try
            {
                return await DoExecAsync(query, reqId);
            }
            catch (Exception e)
            {
                if (_connection.IsAvailable(e))
                {
                    throw;
                }

                await ReconnectAsync();
                return await DoExecAsync(query, reqId);
            }
        }

        private async Task<long> DoExecAsync(string query, long reqId)
        {
            var resp = await _connection.BinaryQueryAsync(query, (ulong)reqId);
            if (!resp.IsUpdate)
            {
                await _connection.FreeResultAsync(resp.ResultId);
            }
            return resp.AffectedRows;
        }

        public async Task SchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol, TDengineSchemalessPrecision precision, int ttl, long reqId)
        {
            try
            {
                await DoSchemalessInsertAsync(lines, protocol, precision, ttl, reqId);
            }
            catch (Exception e)
            {
                if (_connection.IsAvailable(e))
                {
                    throw;
                }

                await ReconnectAsync();
                await DoSchemalessInsertAsync(lines, protocol, precision, ttl, reqId);
            }
        }

        private async Task DoSchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol, TDengineSchemalessPrecision precision, int ttl, long reqId)
        {
            var line = string.Join("\n", lines);
            await _connection.SchemalessInsertAsync(line, protocol, precision, ttl, reqId);
        }
    }
}


