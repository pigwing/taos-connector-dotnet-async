using System;
using System.Threading;
using System.Threading.Tasks;

namespace TDengine.Driver
{
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
public interface IStmtAsync : IAsyncDisposable, IDisposable
#else
    public interface IStmtAsync : IDisposable
#endif
    {
        Task PrepareAsync(string query);

        Task PrepareAsync(string query, CancellationToken cancellationToken);

        bool IsInsert();

        Task SetTableNameAsync(string tableName);

        Task SetTableNameAsync(string tableName, CancellationToken cancellationToken);

        Task SetTagsAsync(object[] tags);

        Task SetTagsAsync(object[] tags, CancellationToken cancellationToken);

        Task<TaosFieldE[]> GetTagFieldsAsync();

        Task<TaosFieldE[]> GetColFieldsAsync();

        Task BindRowAsync(object[] row);

        Task BindRowAsync(object[] row, CancellationToken cancellationToken);

        Task BindColumnAsync(TaosFieldE[] fields, params Array[] arrays);

        Task BindColumnAsync(TaosFieldE[] fields, CancellationToken cancellationToken, params Array[] arrays);

        Task AddBatchAsync();

        Task AddBatchAsync(CancellationToken cancellationToken);

        Task ExecAsync();

        Task ExecAsync(CancellationToken cancellationToken);

        long Affected();

        Task<IRowsAsync> ResultAsync();

        Task<IRowsAsync> ResultAsync(CancellationToken cancellationToken);
    }
}


