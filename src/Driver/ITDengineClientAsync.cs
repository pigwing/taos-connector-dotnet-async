using System;
using System.Net.WebSockets;
using System.Threading.Tasks;

namespace TDengine.Driver
{
#if NETSTANDARD2_1_OR_GREATER
public interface ITDengineClientAsync : IAsyncDisposable
#else
    public interface ITDengineClientAsync : IDisposable
#endif
    {
        WebSocketState State { get; }

        Task<IStmtAsync> StmtInitAsync();

        Task<IStmtAsync> StmtInitAsync(long reqId);

        Task<IRowsAsync> QueryAsync(string query);

        Task<IRowsAsync> QueryAsync(string query, long reqId);

        Task<long> ExecAsync(string query);

        Task<long> ExecAsync(string query, long reqId);

        Task SchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol,
            TDengineSchemalessPrecision precision, int ttl, long reqId);
    }
}


