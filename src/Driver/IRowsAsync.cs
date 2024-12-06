using System;
using System.Threading.Tasks;

namespace TDengine.Driver
{
#if NETSTANDARD2_1_OR_GREATER
public interface IRowsAsync : IAsyncDisposable
#else
    public interface IRowsAsync : IDisposable
#endif
    {
        bool HasRows { get; }

        int AffectRows { get; }

        int FieldCount { get; }

        long GetBytes(int ordinal, long dataOffset, byte[] buffer, int bufferOffset, int length);

        char GetChar(int ordinal);

        long GetChars(int ordinal, long dataOffset, char[] buffer, int bufferOffset, int length);

        string GetDataTypeName(int ordinal);

        object GetValue(int ordinal);

        Type GetFieldType(int ordinal);

        int GetFieldSize(int ordinal);

        string GetName(int ordinal);

        int GetOrdinal(string name);

        Task<bool> ReadAsync();
    }
}


