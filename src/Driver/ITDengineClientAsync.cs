using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace TDengine.Driver
{
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
public interface ITDengineClientAsync : IAsyncDisposable, IDisposable
#else
    public interface ITDengineClientAsync : IDisposable
#endif
    {
        WebSocketState State { get; }

        Task<IStmtAsync> StmtInitAsync();

        Task<IStmtAsync> StmtInitAsync(long reqId);

        Task<IStmtAsync> StmtInitAsync(CancellationToken cancellationToken);

        Task<IStmtAsync> StmtInitAsync(long reqId, CancellationToken cancellationToken);

        Task<IRowsAsync> QueryAsync(string query);

        Task<IRowsAsync> QueryAsync(string query, long reqId);

        Task<IRowsAsync> QueryAsync(string query, CancellationToken cancellationToken);

        Task<IRowsAsync> QueryAsync(string query, long reqId, CancellationToken cancellationToken);

        Task<long> ExecAsync(string query);

        Task<long> ExecAsync(string query, long reqId);

        Task<long> ExecAsync(string query, CancellationToken cancellationToken);

        Task<long> ExecAsync(string query, long reqId, CancellationToken cancellationToken);

        Task SchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol,
            TDengineSchemalessPrecision precision, int ttl, long reqId);

        Task SchemalessInsertAsync(string[] lines, TDengineSchemalessProtocol protocol,
            TDengineSchemalessPrecision precision, int ttl, long reqId, CancellationToken cancellationToken);

        bool ConnectionAvailable();
    }
}


