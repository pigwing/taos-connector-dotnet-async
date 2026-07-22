using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER || NETCOREAPP2_1_OR_GREATER
using System.Buffers.Binary;
#endif

namespace TDengine.Driver.Client
{
    public abstract partial class AbstractStmt
    {
        private static readonly Encoding StmtUtf8Encoding = new UTF8Encoding(false, true);

        public virtual void Exec()
        {
            ThrowIfDisposed();
            if (!_addBatched)
            {
                throw new InvalidOperationException("No batch added. Call AddBatch() before Exec().");
            }

            try
            {
                var buffer = GenerateBindBinary(false, out _);

                // print buffer
                // StringBuilder sb = new StringBuilder();
                // for (int i = 0; i < buffer.Length; i++)
                // {
                //     sb.Append($"0x{buffer[i]:X2}");
                //     if (i < buffer.Length - 1)
                //         sb.Append(", ");
                //     if (i % 16 == 15)
                //         sb.AppendLine();
                // }
                // Console.WriteLine(sb.ToString());
                int affectedRows;
                try
                {
                    BindBinaryInternal(buffer, out affectedRows);
                }
                catch (Exception e)
                {
                    // if the connection is available, throw the exception directly
                    if (!AutoReconnectInternal() || IsConnectionAvailable(e)) throw;
                    // try reconnect
                    ReconnectInternal();
                    // prepare again
                    RePrepare();
                    // bind and execute again
                    BindBinaryInternal(buffer, out affectedRows);
                }

                if (_isInsert)
                {
                    _affectedRows = affectedRows;
                }
            }
            finally
            {
                CleanExec();
            }
        }


        private const int TotalLengthOffset = 0;
        private const int DataTypeOffset = 4;
        private const int NumOffset = 8;
        private const int IsNullOffset = 12;
        private const int HaveLengthOffset = 13;
        private const int FixedBufferLengthOffset = 14;
        private const int FixedBufferOffset = 18;
        internal const int MaximumBindBinarySize = 256 * 1024 * 1024;

        [StructLayout(LayoutKind.Explicit)]
        private struct Int32SingleUnion
        {
            [FieldOffset(0)] internal int Int32;
            [FieldOffset(0)] internal float Single;
        }

        private int WriteBindTag(TaosFieldE[] tagFields, object[] tags, byte[] buffer, int offset)
        {
            var startOffset = offset;
            for (var i = 0; i < tags.Length; i++)
            {
                uint totalLength;
                // write DataType
                WriteU32(buffer, startOffset + DataTypeOffset, (uint)tagFields[i].type);
                // write Num
                WriteU32(buffer, startOffset + NumOffset, 1);
                // hasLength
                bool isVarData = TDengineConstant.IsStmtVarDataType((byte)tagFields[i].type);

                // isNull
                if (tags[i] == null || Convert.IsDBNull(tags[i]))
                {
                    buffer[startOffset + IsNullOffset] = 1;
                    if (isVarData)
                    {
                        // have length
                        buffer[startOffset + HaveLengthOffset] = 1;
                        // length
                        WriteU32(buffer, startOffset + HaveLengthOffset + 1, 0);
                        WriteU32(buffer, startOffset + HaveLengthOffset + 1 + sizeof(uint), 0);
                        // write TotalLength
                        totalLength = 4 + // TotalLength field length
                                      4 + // DataType field length
                                      4 + // Num field length
                                      1 + // IsNull field length
                                      1 + // HaveLength field length
                                      4 + // Length field length, each length is 4 bytes
                                      4; // BufferLength field length
                    }
                    else
                    {
                        // write TotalLength
                        var dataLength = (uint)TDengineConstant.TypeLengthMap[(TDengineDataType)tagFields[i].type];
                        totalLength = 4 + // TotalLength field length
                                      4 + // DataType field length
                                      4 + // Num field length
                                      1 + // IsNull field length
                                      1 + // HaveLength field length
                                      4 + // BufferLength field length
                                      dataLength;
                        WriteU32(buffer, startOffset + FixedBufferLengthOffset, dataLength);
                    }

                    WriteU32(buffer, startOffset + TotalLengthOffset, totalLength);
                }
                else
                {
                    if (!isVarData)
                    {
                        var dataLength = (uint)TDengineConstant.TypeLengthMap[(TDengineDataType)tagFields[i].type];

                        switch (tags[i])
                        {
                            case bool boolVal:
                                buffer[startOffset + FixedBufferOffset] = boolVal ? (byte)1 : (byte)0;
                                break;
                            case sbyte sbyteVal:
                                buffer[startOffset + FixedBufferOffset] = (byte)sbyteVal;
                                break;
                            case byte byteVal:
                                buffer[startOffset + FixedBufferOffset] = byteVal;
                                break;
                            case short shortVal:
                                WriteU16(buffer, startOffset + FixedBufferOffset, (ushort)shortVal);
                                break;
                            case ushort ushortVal:
                                WriteU16(buffer, startOffset + FixedBufferOffset, ushortVal);
                                break;
                            case int intVal:
                                WriteU32(buffer, startOffset + FixedBufferOffset, (uint)intVal);
                                break;
                            case uint uintVal:
                                WriteU32(buffer, startOffset + FixedBufferOffset, uintVal);
                                break;
                            case long longVal:
                                WriteU64(buffer, startOffset + FixedBufferOffset, (ulong)longVal);
                                break;
                            case ulong ulongVal:
                                WriteU64(buffer, startOffset + FixedBufferOffset, ulongVal);
                                break;
                            case float floatVal:
                                WriteU32(buffer, startOffset + FixedBufferOffset, SingleToUInt32Bits(floatVal));
                                break;
                            case double doubleVal:
                                // write BufferLength
                                var doubleInt = BitConverter.DoubleToInt64Bits(doubleVal);
                                WriteU64(buffer, startOffset + FixedBufferOffset, (ulong)doubleInt);
                                break;
                            case DateTime dt:
                                var ts = TDengineConstant.ConvertDateTimeToTimestamp(dt,
                                    (TDenginePrecision)tagFields[i].precision);
                                WriteU64(buffer, startOffset + FixedBufferOffset, (ulong)ts);
                                break;
                            case DateTimeOffset dto:
                                var timestamp =
                                    TDengineConstant.ConvertDateTimeOffsetToTimestamp(dto,
                                        (TDenginePrecision)tagFields[i].precision);
                                WriteU64(buffer, startOffset + FixedBufferOffset, (ulong)timestamp);
                                break;
                            default:
                                throw new ArgumentException(
                                    $"tag fields type not support: {(TDengineDataType)tagFields[i].type}, value: {tags[i]}");
                        }

                        totalLength = 4 + // TotalLength field length
                                      4 + // DataType field length
                                      4 + // Num field length
                                      1 + // IsNull field length
                                      1 + // HaveLength field length
                                      4 + // BufferLength field length
                                      dataLength; // Buffer field length
                        WriteU32(buffer, startOffset + TotalLengthOffset, totalLength);
                        // write BufferLength
                        WriteU32(buffer, startOffset + FixedBufferLengthOffset, dataLength);
                    }
                    else
                    {
                        uint dataLength;
                        switch (tags[i])
                        {
                            case string strVal:
                                dataLength = (uint)StmtUtf8Encoding.GetByteCount(strVal);
                                // write Buffer
                                StmtUtf8Encoding.GetBytes(strVal, 0, strVal.Length, buffer,
                                    startOffset + HaveLengthOffset + 1 + 4 + 4);
                                break;
                            case byte[] binVal:
                                dataLength = (uint)binVal.Length;
                                // write Buffer
                                CopyBytes(binVal, buffer, startOffset + HaveLengthOffset + 1 + 4 + 4);
                                break;
                            default:
                                throw new ArgumentException(
                                    $"tag fields type not support: {(TDengineDataType)tagFields[i].type}, value: {tags[i]}");
                        }

                        totalLength = 4 + // TotalLength field length
                                      4 + // DataType field length
                                      4 + // Num field length
                                      1 + // IsNull field length
                                      1 + // HaveLength field length
                                      4 + // Length field length, each length is 4 bytes
                                      4 + // BufferLength field length
                                      dataLength; // Buffer field length
                        WriteU32(buffer, startOffset + TotalLengthOffset, totalLength);
                        buffer[startOffset + HaveLengthOffset] = 1;
                        // write LengthField
                        WriteU32(buffer, startOffset + HaveLengthOffset + 1, dataLength);
                        // write BufferLength
                        WriteU32(buffer, startOffset + HaveLengthOffset + 1 + 4, dataLength);
                    }
                }

                startOffset += (int)totalLength;
            }

            return startOffset;
        }

        private int WriteBindCol(TaosFieldE[] colFields, List<object>[] cols, int rows, byte[] buffer, int offset)
        {
            var startOffset = offset;
            var haveLengthOffset = IsNullOffset + rows;
            var fixedBufferLengthOffset = haveLengthOffset + 1;
            var fixedBufferOffset = fixedBufferLengthOffset + 4;
            var variableLengthOffset = haveLengthOffset + 1;
            var variableBufferLengthOffset = variableLengthOffset + (4 * rows);
            var variableBufferOffset = variableBufferLengthOffset + 4;
            for (var colIndex = 0; colIndex < cols.Length; colIndex++)
            {
                var colData = cols[colIndex];
                int totalLength;
                // write DataType
                WriteU32(buffer, startOffset + DataTypeOffset, (uint)colFields[colIndex].type);
                // write Num
                WriteU32(buffer, startOffset + NumOffset, (uint)rows);
                // hasLength
                var isVarData = TDengineConstant.IsStmtVarDataType((byte)colFields[colIndex].type);
                if (isVarData)
                {
                    buffer[startOffset + haveLengthOffset] = 1;
                    var variableOffset = startOffset + variableBufferOffset;
                    // variable length data
                    var totalVarBufferLength = 0;
                    for (var rowIndex = 0; rowIndex < rows; rowIndex++)
                    {
                        var value = colData[rowIndex];
                        if (value == null || Convert.IsDBNull(value))
                        {
                            // is null
                            buffer[startOffset + IsNullOffset + rowIndex] = 1;
                            // length
                            // WriteU32(buffer, startOffset + variableLengthOffset + rowIndex * 4, 0);
                        }
                        else
                        {
                            switch (value)
                            {
                                case string strVal:
                                {
                                    var length = StmtUtf8Encoding.GetByteCount(strVal);
                                    WriteU32(buffer, startOffset + variableLengthOffset + rowIndex * 4, (uint)length);
                                    StmtUtf8Encoding.GetBytes(strVal, 0, strVal.Length, buffer, variableOffset);
                                    totalVarBufferLength += length;
                                    variableOffset += length;
                                    break;
                                }
                                case byte[] binVal:
                                {
                                    WriteU32(buffer, startOffset + variableLengthOffset + rowIndex * 4,
                                        (uint)binVal.Length);
                                    CopyBytes(binVal, buffer, variableOffset);
                                    totalVarBufferLength += binVal.Length;
                                    variableOffset += binVal.Length;
                                    break;
                                }
                                default:
                                    throw new NotSupportedException(
                                        $"col field type not support: {(TDengineDataType)colFields[colIndex].type}, value: {value}");
                            }
                        }
                    }

                    totalLength = 4 + // TotalLength field length
                                  4 + // DataType field length
                                  4 + // Num field length
                                  (1 * rows) + // IsNull field length
                                  1 + // HaveLength field length
                                  (4 * rows) + // Length field length, each length is 4 bytes
                                  4 + // BufferLength field length
                                  totalVarBufferLength; // Buffer field length
                    // write TotalLength
                    WriteU32(buffer, startOffset + TotalLengthOffset, (uint)totalLength);
                    // write BufferLength
                    WriteU32(buffer, startOffset + variableBufferLengthOffset, (uint)totalVarBufferLength);
                }
                else
                {
                    var totalFixedBufferLength = 0;
                    var typeLength = TDengineConstant.TypeLengthMap[(TDengineDataType)colFields[colIndex].type];
                    var fixedOffset = startOffset + fixedBufferOffset;
                    for (var rowIndex = 0; rowIndex < rows; rowIndex++)
                    {
                        var value = colData[rowIndex];
                        if (value == null || Convert.IsDBNull(value))
                        {
                            buffer[startOffset + IsNullOffset + rowIndex] = 1;
                        }
                        else
                        {
                            switch (value)
                            {
                                case DateTimeOffset dto:
                                    var timestamp = TDengineConstant.ConvertDateTimeOffsetToTimestamp(dto,
                                        (TDenginePrecision)colFields[colIndex].precision);
                                    WriteU64(buffer, fixedOffset, (ulong)timestamp);
                                    break;
                                case DateTime dt:
                                    var ts = TDengineConstant.ConvertDateTimeToTimestamp(dt,
                                        (TDenginePrecision)colFields[colIndex].precision);
                                    WriteU64(buffer, fixedOffset, (ulong)ts);
                                    break;
                                case bool boolVal:
                                    buffer[fixedOffset] = boolVal ? (byte)1 : (byte)0;
                                    break;
                                case sbyte sbyteVal:
                                    buffer[fixedOffset] = (byte)sbyteVal;
                                    break;
                                case byte byteVal:
                                    buffer[fixedOffset] = byteVal;
                                    break;
                                case short shortVal:
                                    WriteU16(buffer, fixedOffset, (ushort)shortVal);
                                    break;
                                case ushort ushortVal:
                                    WriteU16(buffer, fixedOffset, ushortVal);
                                    break;
                                case int intVal:
                                    WriteU32(buffer, fixedOffset, (uint)intVal);
                                    break;
                                case uint uintVal:
                                    WriteU32(buffer, fixedOffset, uintVal);
                                    break;
                                case long longVal:
                                    WriteU64(buffer, fixedOffset, (ulong)longVal);
                                    break;
                                case ulong ulongVal:
                                    WriteU64(buffer, fixedOffset, ulongVal);
                                    break;
                                case float floatVal:
                                    WriteU32(buffer, fixedOffset, SingleToUInt32Bits(floatVal));
                                    break;
                                case double doubleVal:
                                    var doubleInt = BitConverter.DoubleToInt64Bits(doubleVal);
                                    WriteU64(buffer, fixedOffset, (ulong)doubleInt);
                                    break;
                                default:
                                    throw new NotSupportedException(
                                        $"col field type not support: {(TDengineDataType)colFields[colIndex].type}");
                            }
                        }

                        totalFixedBufferLength += typeLength;
                        fixedOffset += typeLength;
                    }

                    totalLength = 4 + // TotalLength field length
                                  4 + // DataType field length
                                  4 + // Num field length
                                  (1 * rows) + // IsNull field length
                                  1 + // HaveLength field length
                                  4 + // BufferLength field length
                                  totalFixedBufferLength; // Buffer field length
                    // write TotalLength
                    WriteU32(buffer, startOffset + TotalLengthOffset, (uint)totalLength);
                    // write BufferLength
                    WriteU32(buffer, startOffset + fixedBufferLengthOffset, (uint)totalFixedBufferLength);
                }

                startOffset += totalLength;
            }

            return startOffset;
        }


        private byte[] GenerateBindBinary(bool rentFromPool, out int bufferLength)
        {
            bufferLength = 0;
            checked
            {
                var tableCount = _tableInfos.Count;
                if (tableCount == 0)
                {
                    throw new InvalidOperationException("No statement batches are available for execution.");
                }

                var colCount = _isInsert ? _colFields.Length : _fieldsCount;
                var colFields = _isInsert ? _colFields : _queryFields;
                if (colFields == null || colFields.Length != colCount)
                {
                    throw new InvalidOperationException("Statement column metadata is incomplete.");
                }

                const int fixedHeaderLength = 28;
                var tableNameLengthsLength = _needTableName ? tableCount * sizeof(ushort) : 0;
                var tableNameBufferLength = 0;
                var tagsLengthsLength = NeedTags ? tableCount * sizeof(uint) : 0;
                var tagsBufferLength = 0;
                var columnsLengthsLength = tableCount * sizeof(uint);
                var columnsBufferLength = 0;

                var tableNameLengths = new ushort[_needTableName ? tableCount : 0];
                var tableTagLengths = new int[NeedTags ? tableCount : 0];
                var tableColumnLengths = new int[tableCount];
                var tableNames = new string[tableCount];
                var tableIndex = 0;
                foreach (var tableEntry in _tableInfos)
                {
                    var tableName = tableEntry.Key;
                    tableNames[tableIndex] = tableName;
                    if (_needTableName)
                    {
                        var byteLengthWithTerminator = StmtUtf8Encoding.GetByteCount(tableName) + 1;
                        if (byteLengthWithTerminator > ushort.MaxValue)
                        {
                            throw new ArgumentException(
                                $"The UTF-8 table name at index {tableIndex} exceeds {ushort.MaxValue - 1} bytes.");
                        }

                        tableNameLengths[tableIndex] = (ushort)byteLengthWithTerminator;
                        tableNameBufferLength += byteLengthWithTerminator;
                    }

                    if (NeedTags)
                    {
                        if (tableEntry.Value.Tags == null || tableEntry.Value.Tags.Length != _tagFields.Length)
                        {
                            throw new InvalidOperationException($"Tags for table '{tableName}' are incomplete.");
                        }

                        var tableTagLength = 0;
                        for (var tagIndex = 0; tagIndex < _tagFields.Length; tagIndex++)
                        {
                            int valueLength;
                            if (TDengineConstant.IsStmtVarDataType((byte)_tagFields[tagIndex].type))
                            {
                                valueLength = GetVariableValueLength(tableEntry.Value.Tags[tagIndex], _tagFields[tagIndex],
                                    "tag");
                                tableTagLength += 22 + valueLength;
                            }
                            else
                            {
                                valueLength = TDengineConstant.TypeLengthMap[
                                    (TDengineDataType)_tagFields[tagIndex].type];
                                tableTagLength += 18 + valueLength;
                            }
                        }

                        tableTagLengths[tableIndex] = tableTagLength;
                        tagsBufferLength += tableTagLength;
                    }

                    var rows = tableEntry.Value.Rows;
                    var tableColumnLength = 0;
                    for (var columnIndex = 0; columnIndex < colCount; columnIndex++)
                    {
                        if (TDengineConstant.IsStmtVarDataType((byte)colFields[columnIndex].type))
                        {
                            var valuesLength = 0;
                            for (var rowIndex = 0; rowIndex < rows; rowIndex++)
                            {
                                valuesLength += GetVariableValueLength(
                                    tableEntry.Value.Cols[columnIndex][rowIndex], colFields[columnIndex], "column");
                            }

                            tableColumnLength += 17 + (5 * rows) + valuesLength;
                        }
                        else
                        {
                            var typeLength = TDengineConstant.TypeLengthMap[
                                (TDengineDataType)colFields[columnIndex].type];
                            tableColumnLength += 17 + rows + (typeLength * rows);
                        }
                    }

                    tableColumnLengths[tableIndex] = tableColumnLength;
                    columnsBufferLength += tableColumnLength;
                    tableIndex++;
                }

                var tableNamesLength = tableNameLengthsLength + tableNameBufferLength;
                var tagsLength = tagsLengthsLength + tagsBufferLength;
                var columnsLength = columnsLengthsLength + columnsBufferLength;
                var totalBufferLength = fixedHeaderLength + tableNamesLength + tagsLength + columnsLength;
                var allocationLength = totalBufferLength + _binaryHeaderLength;
                if (allocationLength > MaximumBindBinarySize)
                {
                    throw new InvalidOperationException(
                        $"Statement bind payload exceeds the {MaximumBindBinarySize} byte limit.");
                }

                var tableNamesOffset = fixedHeaderLength;
                var tagsOffset = tableNamesOffset + tableNamesLength;
                var columnsOffset = tagsOffset + tagsLength;
                var buffer = rentFromPool
                    ? ArrayPool<byte>.Shared.Rent(Math.Max(1, allocationLength))
                    : new byte[allocationLength];
                if (rentFromPool)
                {
                    Array.Clear(buffer, 0, allocationLength);
                }

                try
                {
                    WriteU32(buffer, _binaryHeaderLength, (uint)totalBufferLength);
                    WriteU32(buffer, _binaryHeaderLength + 4, (uint)tableCount);
                    WriteU32(buffer, _binaryHeaderLength + 8, NeedTags ? (uint)_tagFields.Length : 0);
                    WriteU32(buffer, _binaryHeaderLength + 12, (uint)colCount);
                    WriteU32(buffer, _binaryHeaderLength + 16, _needTableName ? (uint)fixedHeaderLength : 0);
                    WriteU32(buffer, _binaryHeaderLength + 20, NeedTags ? (uint)tagsOffset : 0);
                    WriteU32(buffer, _binaryHeaderLength + 24, (uint)columnsOffset);

                var tableNameLengthOffset = _binaryHeaderLength + tableNamesOffset;
                var tableNameBufferOffset = tableNameLengthOffset + tableNameLengthsLength;
                var tagsLengthOffset = _binaryHeaderLength + tagsOffset;
                var tagsBufferOffset = tagsLengthOffset + tagsLengthsLength;
                var columnsLengthOffset = _binaryHeaderLength + columnsOffset;
                var columnsBufferOffset = columnsLengthOffset + columnsLengthsLength;

                for (var i = 0; i < tableTagLengths.Length; i++)
                {
                    WriteU32(buffer, tagsLengthOffset + (i * sizeof(uint)), (uint)tableTagLengths[i]);
                }

                for (var i = 0; i < tableColumnLengths.Length; i++)
                {
                    WriteU32(buffer, columnsLengthOffset + (i * sizeof(uint)), (uint)tableColumnLengths[i]);
                }

                for (var i = 0; i < tableNameLengths.Length; i++)
                {
                    WriteU16(buffer, tableNameLengthOffset + (i * sizeof(ushort)), tableNameLengths[i]);
                }

                var nextTableNameOffset = tableNameBufferOffset;
                var nextTagOffset = tagsBufferOffset;
                var nextColumnOffset = columnsBufferOffset;
                for (var i = 0; i < tableCount; i++)
                {
                    var tableName = tableNames[i];
                    if (_needTableName)
                    {
                        StmtUtf8Encoding.GetBytes(tableName, 0, tableName.Length, buffer, nextTableNameOffset);
                        nextTableNameOffset += tableNameLengths[i];
                    }

                    var bindData = _tableInfos[tableName];
                    if (NeedTags)
                    {
                        nextTagOffset = WriteBindTag(_tagFields, bindData.Tags, buffer, nextTagOffset);
                    }

                    nextColumnOffset = WriteBindCol(colFields, bindData.Cols, bindData.Rows, buffer,
                        nextColumnOffset);
                }

                if (nextTableNameOffset != tableNameBufferOffset + tableNameBufferLength ||
                    nextTagOffset != tagsBufferOffset + tagsBufferLength ||
                    nextColumnOffset != columnsBufferOffset + columnsBufferLength)
                {
                    throw new InvalidOperationException("Statement bind payload length calculation is inconsistent.");
                }

                    bufferLength = allocationLength;
                    return buffer;
                }
                catch
                {
                    if (rentFromPool)
                    {
                        ClearAndReturnPooledBindBinary(buffer, allocationLength);
                    }

                    throw;
                }
            }
        }

        private static int GetVariableValueLength(object value, TaosFieldE field, string valueKind)
        {
            if (value == null || Convert.IsDBNull(value))
            {
                return 0;
            }

            if (value is string stringValue)
            {
                return StmtUtf8Encoding.GetByteCount(stringValue);
            }

            if (value is byte[] bytes)
            {
                return bytes.Length;
            }

            throw new NotSupportedException(
                $"{valueKind} field type not support: {(TDengineDataType)field.type}, value: {value}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteU32(byte[] buffer, int offset, uint value)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER || NETCOREAPP2_1_OR_GREATER
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, sizeof(uint)), value);
#else
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteU64(byte[] buffer, int offset, ulong value)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER || NETCOREAPP2_1_OR_GREATER
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, sizeof(ulong)), value);
#else
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
            buffer[offset + 4] = (byte)(value >> 32);
            buffer[offset + 5] = (byte)(value >> 40);
            buffer[offset + 6] = (byte)(value >> 48);
            buffer[offset + 7] = (byte)(value >> 56);
#endif
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteU16(byte[] buffer, int offset, ushort value)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER || NETCOREAPP2_1_OR_GREATER
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, sizeof(ushort)), value);
#else
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint SingleToUInt32Bits(float value)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER || NETCOREAPP2_0_OR_GREATER
            return (uint)BitConverter.SingleToInt32Bits(value);
#else
            return (uint)new Int32SingleUnion { Single = value }.Int32;
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyBytes(byte[] source, byte[] destination, int destinationOffset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER || NETCOREAPP2_1_OR_GREATER
            source.AsSpan().CopyTo(destination.AsSpan(destinationOffset, source.Length));
#else
            Buffer.BlockCopy(source, 0, destination, destinationOffset, source.Length);
#endif
        }

        protected abstract void BindBinaryInternal(byte[] data, out int affectedRows);

        protected byte[] RentBindBinaryForExecution(out int bufferLength)
        {
            if (!_addBatched)
            {
                throw new InvalidOperationException("No batch added. Call AddBatch() before Exec().");
            }

            return GenerateBindBinary(true, out bufferLength);
        }

        protected static void ReturnPooledBindBinary(byte[] buffer, int bufferLength)
        {
            ClearAndReturnPooledBindBinary(buffer, bufferLength);
        }

        private static void ClearAndReturnPooledBindBinary(byte[] buffer, int bufferLength)
        {
            if (buffer == null)
            {
                return;
            }

            var clearLength = Math.Max(0, Math.Min(bufferLength, buffer.Length));
            if (clearLength != 0)
            {
                Array.Clear(buffer, 0, clearLength);
            }

            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }

        protected void CompleteExecution(int affectedRows)
        {
            try
            {
                if (_isInsert)
                {
                    _affectedRows = affectedRows;
                }
            }
            finally
            {
                CleanExec();
            }
        }
    }
}
