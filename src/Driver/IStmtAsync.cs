using System;
using System.Threading.Tasks;

namespace TDengine.Driver
{
#if NETSTANDARD2_1_OR_GREATER
public interface IStmtAsync : IAsyncDisposable
#else
    public interface IStmtAsync : IDisposable
#endif
    {
        Task PrepareAsync(string query);

        bool IsInsert();

        Task SetTableNameAsync(string tableName);

        Task SetTagsAsync(object[] tags);

        Task<TaosFieldE[]> GetTagFieldsAsync();

        Task<TaosFieldE[]> GetColFieldsAsync();

        Task BindRowAsync(object[] row);

        Task BindColumnAsync(TaosFieldE[] fields, params Array[] arrays);

        Task AddBatchAsync();

        Task ExecAsync();

        long Affected();

        Task<IRowsAsync> ResultAsync();
    }
}


