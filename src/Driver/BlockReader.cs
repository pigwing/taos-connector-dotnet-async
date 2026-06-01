using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
using System.Buffers.Binary;
#endif

namespace TDengine.Driver
{
    public class BlockReader
    {
        private static readonly int ColInfoSize = TDengineConstant.Int8Size + TDengineConstant.Int32Size;
        private static readonly int RawBlockVersionOffset = 0;
        private static readonly int RawBlockLengthOffset = RawBlockVersionOffset + TDengineConstant.Int32Size;
        private static readonly int NumOfRowsOffset = RawBlockLengthOffset + TDengineConstant.Int32Size;
        private static readonly int NumOfColsOffset = NumOfRowsOffset + TDengineConstant.Int32Size;
        private static readonly int HasColumnSegmentOffset = NumOfColsOffset + TDengineConstant.Int32Size;
        private static readonly int GroupIdOffset = HasColumnSegmentOffset + TDengineConstant.Int32Size;
        private static readonly int ColInfoOffset = GroupIdOffset + TDengineConstant.UInt64Size;

        private byte[] _block;
        private int _rows;
        private int _lengthOffset;
        private int _headerOffset;
        private int _nullBitMapOffset;

        private int _precision;
        private int[] _colHeadOffset;
        private int _cols;
        private byte[] _colType;
        private TimeZoneInfo _tz;
        private byte[] _scales;

        private int _offset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static short ReadInt16(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BinaryPrimitives.ReadInt16LittleEndian(source.AsSpan(offset, sizeof(short)));
#else
            return BitConverter.ToInt16(source, offset);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort ReadUInt16(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset, sizeof(ushort)));
#else
            return BitConverter.ToUInt16(source, offset);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadInt32(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(offset, sizeof(int)));
#else
            return BitConverter.ToInt32(source, offset);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ReadUInt32(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, sizeof(uint)));
#else
            return BitConverter.ToUInt32(source, offset);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long ReadInt64(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BinaryPrimitives.ReadInt64LittleEndian(source.AsSpan(offset, sizeof(long)));
#else
            return BitConverter.ToInt64(source, offset);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ReadUInt64(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(offset, sizeof(ulong)));
#else
            return BitConverter.ToUInt64(source, offset);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ReadSingle(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BitConverter.Int32BitsToSingle(ReadInt32(source, offset));
#else
            return BitConverter.ToSingle(source, offset);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double ReadDouble(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BitConverter.Int64BitsToDouble(ReadInt64(source, offset));
#else
            return BitConverter.ToDouble(source, offset);
#endif
        }

        public BlockReader(int offset, int cols, int precision, byte[] colType, byte[] scales,
            TimeZoneInfo tz = null) :
            this(offset, tz)
        {
            _cols = cols;
            _precision = precision;
            _colHeadOffset = new int[cols];
            _colType = colType;
            _scales = scales;
        }

        // Constructor for TMQ blocks
        public BlockReader(int offset, TimeZoneInfo tz = null)
        {
            _offset = offset;
            if (tz == null)
            {
                tz = TimeZoneInfo.Local;
            }

            _tz = tz;
        }

        // Set block for raw blocks (used in NativeRows)
        // copies the data from the unmanaged memory pointed to by pBlock
        // into a managed byte array and initializes the block reader with it
        public void SetBlockPtr(IntPtr pBlock, int rows)
        {
            var blockSize = GetBlockSize(pBlock);
            byte[] dataArray = new byte[blockSize];
            Marshal.Copy(pBlock, dataArray, 0, blockSize);
            SetBlock(dataArray);
        }

        private Int32 GetBlockSize(IntPtr pBlock)
        {
            return Marshal.ReadInt32(pBlock + _offset + RawBlockLengthOffset);
        }

        // Set block for raw blocks (used in WSRows)
        public void SetBlock(byte[] block)
        {
            _block = block;
            _rows = GetRowCount();
            _nullBitMapOffset = TDengineConstant.BitmapLen(_rows);
            _lengthOffset = _offset + ColInfoOffset + _cols * ColInfoSize;
            _headerOffset = _offset + ColInfoOffset + _cols * ColInfoSize + _cols * TDengineConstant.Int32Size;
            _colHeadOffset[0] = _headerOffset;
            if (_cols == 1) return;
            for (int i = 0; i < _cols - 1; i++)
            {
                var colLength = ReadInt32(block, _lengthOffset + TDengineConstant.Int32Size * i);
                if (TDengineConstant.IsVarDataType(_colType[i]))
                {
                    _colHeadOffset[i + 1] = _colHeadOffset[i] + TDengineConstant.Int32Size * _rows + colLength;
                }
                else
                {
                    _colHeadOffset[i + 1] = _colHeadOffset[i] + _nullBitMapOffset + colLength;
                }
            }
        }

        public void ClearBlock()
        {
            _block = null;
            _rows = 0;
            _lengthOffset = 0;
            _headerOffset = 0;
            _nullBitMapOffset = 0;
            if (_colHeadOffset != null)
            {
                Array.Clear(_colHeadOffset, 0, _colHeadOffset.Length);
            }
        }

        // Set block for for TMQ blocks
        public void SetTMQBlock(byte[] block, int precision, int offset)
        {
            _block = block;
            _offset = offset;
            _precision = precision;
            _cols = GetColumnCount();
            _colHeadOffset = new int[_cols];
            InitMeta();
            SetBlock(_block);
        }

        public int GetRows()
        {
            return _rows;
        }

        private int GetColumnCount()
        {
            return ReadInt32(_block, _offset + NumOfColsOffset);
        }

        private int GetRowCount()
        {
            return ReadInt32(_block, _offset + NumOfRowsOffset);
        }

        private void InitMeta()
        {
            _colType = new byte[_cols];
            _scales = new byte[_cols];
            for (int i = 0; i < _cols; i++)
            {
                _colType[i] = _block[_offset + ColInfoOffset + i * ColInfoSize];
                if (_colType[i] == (byte)TDengineDataType.TSDB_DATA_TYPE_DECIMAL64 ||
                    _colType[i] == (byte)TDengineDataType.TSDB_DATA_TYPE_DECIMAL)
                {
                    // type, scale, precision, empty, bytes
                    _scales[i] = _block[_offset + ColInfoOffset + i * ColInfoSize + TDengineConstant.Int8Size];
                }
            }
        }

        private bool ItemIsNull(int headOffset, int row) =>
            TDengineConstant.BitmapIsNull(_block[headOffset + TDengineConstant.CharOffset(row)], row);

        public object Read(int row, int col)
        {
            var colType = (TDengineDataType)_colType[col];
            switch (colType)
            {
                case TDengineDataType.TSDB_DATA_TYPE_BOOL:
                    return ItemIsNull(_colHeadOffset[col], row) ? (object)null : ConvertBool(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_TINYINT:
                    return ItemIsNull(_colHeadOffset[col], row) ? (object)null : ConvertTinyint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_SMALLINT:
                    return ItemIsNull(_colHeadOffset[col], row) ? (object)null : ConvertSmallint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_INT:
                    return ItemIsNull(_colHeadOffset[col], row) ? (object)null : ConvertInt(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_BIGINT:
                    return ItemIsNull(_colHeadOffset[col], row) ? (object)null : ConvertBigInt(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_FLOAT:
                    return ItemIsNull(_colHeadOffset[col], row) ? (object)null : ConvertFloat(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_DOUBLE:
                    return ItemIsNull(_colHeadOffset[col], row) ? (object)null : ConvertDouble(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_BINARY:
                    return ConvertBinary(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_TIMESTAMP:
                    return ItemIsNull(_colHeadOffset[col], row) ? (object)null : ConvertTime(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_NCHAR:
                    return ConvertNchar(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_UTINYINT:
                    return ItemIsNull(_colHeadOffset[col], row) ? (object)null : ConvertUTinyint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_USMALLINT:
                    return ItemIsNull(_colHeadOffset[col], row) ? (object)null : ConvertUSmallint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_UINT:
                    return ItemIsNull(_colHeadOffset[col], row) ? (object)null : ConvertUInt(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_UBIGINT:
                    return ItemIsNull(_colHeadOffset[col], row) ? (object)null : ConvertUBigInt(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_JSONTAG:
                    return ConvertJson(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_VARBINARY:
                    return ConvertBinary(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_GEOMETRY:
                    return ConvertBinary(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_BLOB:
                    return ConvertBlob(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL64:
                    return ItemIsNull(_colHeadOffset[col], row) ? (object)null : ConvertDecimal64(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL:
                    return ItemIsNull(_colHeadOffset[col], row) ? (object)null : ConvertDecimal128(row, col);
                default:
                    throw new NotSupportedException($"Unsupported data type: {colType}");
            }
        }

        private bool ConvertBool(int row, int col) => _block[_colHeadOffset[col] + _nullBitMapOffset + row] != 0;

        private sbyte ConvertTinyint(int row, int col)
        {
            return (sbyte)_block[_colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.Int8Size];
        }

        private short ConvertSmallint(int row, int col) =>
            ReadInt16(_block, _colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.Int16Size);

        private int ConvertInt(int row, int col) =>
            ReadInt32(_block, _colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.Int32Size);

        private long ConvertBigInt(int row, int col) =>
            ReadInt64(_block, _colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.Int64Size);

        private byte ConvertUTinyint(int row, int col) =>
            _block[_colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.UInt8Size];

        private ushort ConvertUSmallint(int row, int col) =>
            ReadUInt16(_block, _colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.UInt16Size);

        private uint ConvertUInt(int row, int col) =>
            ReadUInt32(_block, _colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.UInt32Size);

        private ulong ConvertUBigInt(int row, int col) =>
            ReadUInt64(_block, _colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.UInt64Size);

        private float ConvertFloat(int row, int col) =>
            ReadSingle(_block, _colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.Float32Size);

        private double ConvertDouble(int row, int col) =>
            ReadDouble(_block, _colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.Float64Size);

        private string ConvertDecimal64Str(int row, int col)
        {
            return ConvertDecimal64(row, col).ToString(CultureInfo.InvariantCulture);
        }

        private decimal ConvertDecimal64(int row, int col)
        {
            var int64Value = ReadInt64(_block,
                _colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.Int64Size);
            var scale = _scales[col];
            bool isNegative = int64Value < 0;
            var val = int64Value;
            if (isNegative)
            {
                val = -int64Value;
            }

            int lo = (int)(val & 0xFFFFFFFF);
            int mid = (int)((val >> 32) & 0xFFFFFFFF);
            return new decimal(lo, mid, 0, isNegative, scale);
        }

        private string ConvertDecimal128Str(int row, int col)
        {
            try
            {
                return ConvertDecimal128(row, col).ToString(CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                // Use BigInteger for large values
                int startIndex = _colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.Int64Size * 2;
                var lo = ReadUInt64(_block, startIndex);
                var hi = ReadInt64(_block, startIndex + TDengineConstant.UInt64Size);
                var scale = _scales[col];
                var str = FormatI128(hi, lo);
                return FormatDecimal(str, scale);
            }
        }

        private decimal ConvertDecimal128(int row, int col)
        {
            int startIndex = _colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.Int64Size * 2;
            ulong lower = ReadUInt64(_block, startIndex);
            ulong upper = ReadUInt64(_block, startIndex + TDengineConstant.UInt64Size);
            bool isNegative = (long)(upper) < 0;
            if (isNegative)
            {
                lower = 0UL - lower;
                ulong borrow = (lower > 0UL) ? 1UL : 0UL;
                upper = 0UL - upper - borrow;
            }

            ulong lo64 = lower;
            if (upper > uint.MaxValue)
            {
                throw new OverflowException("Value was either too large or too small for a Decimal.");
            }

            uint hi32 = (uint)(upper);
            var scale = _scales[col];
            return new decimal((int)(lo64), (int)(lo64 >> 32), (int)(hi32), isNegative: isNegative, scale: scale);
        }

        private static string FormatI128(long hi, ulong lo)
        {
            BigInteger highPart = new BigInteger(hi) << 64;
            BigInteger lowPart = new BigInteger(lo);
            BigInteger result = highPart | lowPart;
            return result.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatDecimal(string str, int scale)
        {
            if (scale == 0)
                return str;

            var builder = new StringBuilder();
            int startIndex = 0;

            // Handle negative sign
            if (str.StartsWith("-"))
            {
                builder.Append('-');
                startIndex = 1; // Skip the negative sign
            }

            int length = str.Length - startIndex;
            int delta = length - scale;

            // Handle the position of the decimal point
            if (delta > 0)
            {
                // Example: str="12345", scale=3 → "12.345"
                builder.Append(str, startIndex, delta); // Integer part
                builder.Append('.');
                builder.Append(str, startIndex + delta, scale); // Fractional part
            }
            else
            {
                // Example: str="123", scale=5 → "0.00123"
                builder.Append("0.");
                builder.Append('0', -delta); // Pad with zeros
                builder.Append(str, startIndex, length); // Original number
            }

            return builder.ToString();
        }

        private DateTime ConvertTime(int row, int col)
        {
            var ts = ConvertBigInt(row, col);
            return TDengineConstant.ConvertTimestampToDateTime(ts, (TDenginePrecision)_precision, _tz);
        }
        
        private DateTimeOffset ConvertTimeOffset(int row, int col)
        {
            var ts = ConvertBigInt(row, col);
            return TDengineConstant.ConvertTimestampToDateTimeOffset(ts, (TDenginePrecision)_precision, _tz);
        }

        private byte[] ConvertBinary(int row, int col)
        {
            int currentRow;
            int length;
            if (!TryGetVarData(row, col, TDengineConstant.Int16Size, out currentRow, out length))
            {
                return null;
            }

            byte[] subarray = new byte[length];
            Buffer.BlockCopy(_block, currentRow, subarray, 0, length);
            return subarray;
        }

        private byte[] ConvertBlob(int row, int col)
        {
            int currentRow;
            int length;
            if (!TryGetVarData(row, col, TDengineConstant.Int32Size, out currentRow, out length))
            {
                return null;
            }

            byte[] subarray = new byte[length];
            Buffer.BlockCopy(_block, currentRow, subarray, 0, length);
            return subarray;
        }

        private string ConvertNchar(int row, int col)
        {
            int currentRow;
            int length;
            if (!TryGetVarData(row, col, TDengineConstant.Int16Size, out currentRow, out length))
            {
                return null;
            }

            return ConvertUcs4BytesToUtf8String(_block, currentRow, length);
        }

        private static string ConvertUcs4BytesToUtf8String(byte[] ucs4Bytes, int offset, int count)
        {
            return Encoding.UTF8.GetString(Encoding.Convert(Encoding.UTF32, Encoding.UTF8, ucs4Bytes, offset, count));
        }

        private byte[] ConvertJson(int row, int col)
        {
            int currentRow;
            int length;
            if (!TryGetVarData(row, col, TDengineConstant.Int16Size, out currentRow, out length))
            {
                return null;
            }

            byte[] subarray = new byte[length];
            Buffer.BlockCopy(_block, currentRow, subarray, 0, length);
            return subarray;
        }

        private bool TryGetVarData(int row, int col, int lengthHeaderSize, out int dataOffset, out int length)
        {
            var offset = ReadInt32(_block, _colHeadOffset[col] + row * TDengineConstant.Int32Size);
            if (offset == -1)
            {
                dataOffset = 0;
                length = 0;
                return false;
            }

            var currentRow = _colHeadOffset[col] + TDengineConstant.Int32Size * _rows + offset;
            if (lengthHeaderSize == TDengineConstant.Int16Size)
            {
                length = ReadUInt16(_block, currentRow);
            }
            else
            {
                length = (int)ReadUInt32(_block, currentRow);
            }

            dataOffset = currentRow + lengthHeaderSize;
            return true;
        }

        public long GetChars(int row, int col, long dataOffset, char[] buffer, int bufferOffset, int length)
        {
            if (!TDengineConstant.IsVarDataType(_colType[col]))
            {
                throw new Exception("GetBytes cannot be used on non-character columns");
            }

            var data = Read(row, col);

            char[] value = null;
            switch (data)
            {
                case string val:
                    value = val.ToCharArray();
                    break;
                case byte[] val:
                    value = Encoding.UTF8.GetChars(val);
                    break;
            }

            if (value == null)
            {
                return 0;
            }

            var dataLength = value.Length - (int)dataOffset;
            var bufferLength = buffer.Length - bufferOffset;
            var minLength = dataLength > bufferLength ? bufferLength : dataLength;
            minLength = minLength > length ? length : minLength;
            Array.Copy(value, (int)dataOffset, buffer, bufferOffset, minLength);
            return minLength;
        }

        public char GetChar(int row, int col)
        {
            if (!TDengineConstant.IsVarDataType(_colType[col]))
            {
                throw new Exception("GetChar cannot be used on non-character columns");
            }

            var data = Read(row, col);

            switch (data)
            {
                case string val:
                    return val[0];
                case byte[] val:
                    return Encoding.UTF8.GetChars(val)[0];
            }

            return (char)0;
        }

        public long GetBytes(int row, int col, long dataOffset, byte[] buffer, int bufferOffset, int length)
        {
            if (!TDengineConstant.IsVarDataType(_colType[col]))
            {
                throw new Exception("GetBytes cannot be used on non-character columns");
            }

            int sourceOffset;
            int sourceLength;
            switch ((TDengineDataType)_colType[col])
            {
                case TDengineDataType.TSDB_DATA_TYPE_NCHAR:
                {
                    var value = ConvertNchar(row, col);
                    if (value == null)
                    {
                        return 0;
                    }

                    var valueBytes = Encoding.UTF8.GetBytes(value);
                    return CopyVarData(valueBytes, 0, valueBytes.Length, dataOffset, buffer, bufferOffset, length);
                }
                case TDengineDataType.TSDB_DATA_TYPE_BLOB:
                    if (!TryGetVarData(row, col, TDengineConstant.Int32Size, out sourceOffset, out sourceLength))
                    {
                        return 0;
                    }

                    break;
                default:
                    if (!TryGetVarData(row, col, TDengineConstant.Int16Size, out sourceOffset, out sourceLength))
                    {
                        return 0;
                    }

                    break;
            }

            return CopyVarData(_block, sourceOffset, sourceLength, dataOffset, buffer, bufferOffset, length);
        }

        private bool VarDataTypeIsNull(int row, int col)
        {
            var offset = ReadInt32(_block, _colHeadOffset[col] + row * TDengineConstant.Int32Size);
            return offset == -1;
        }

        public bool IsDBNull(int row, int col)
        {
            return TDengineConstant.IsVarDataType(_colType[col]) ? VarDataTypeIsNull(row, col) : ItemIsNull(_colHeadOffset[col], row);
        }

        private void CheckNull(int row, int col)
        {
            if (col < 0 || col >= _cols)
            {
                throw new ArgumentOutOfRangeException(nameof(col), $"value must be between 0 and {_cols - 1}");
            }

            if (IsDBNull(row, col))
            {
                throw new InvalidCastException("Cannot cast null value to non-nullable type.");
            }
        }

        public byte GetByte(int row, int col)
        {
            CheckNull(row, col);
            switch ((TDengineDataType)_colType[col])
            {
                case TDengineDataType.TSDB_DATA_TYPE_TINYINT:
                    return checked((byte)ConvertTinyint(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_UTINYINT:
                    return ConvertUTinyint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_SMALLINT:
                    return checked((byte)ConvertSmallint(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_USMALLINT:
                    return checked((byte)ConvertUSmallint(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_INT:
                    return checked((byte)ConvertInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_UINT:
                    return checked((byte)ConvertUInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_BIGINT:
                    return checked((byte)ConvertBigInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_UBIGINT:
                    return checked((byte)ConvertUBigInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_FLOAT:
                    return checked((byte)ConvertFloat(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_DOUBLE:
                    return checked((byte)ConvertDouble(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL64:
                    return (byte)ConvertDecimal64(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL:
                    return (byte)ConvertDecimal128(row, col);
                default:
                    throw new InvalidCastException("Cannot cast to byte from " +
                                                   TDengineConstant.GetFieldTypeName((sbyte)_colType[col]));
            }
        }

        public short GetInt16(int row, int col)
        {
            CheckNull(row, col);
            switch ((TDengineDataType)_colType[col])
            {
                case TDengineDataType.TSDB_DATA_TYPE_TINYINT:
                    return ConvertTinyint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_UTINYINT:
                    return ConvertUTinyint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_SMALLINT:
                    return ConvertSmallint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_USMALLINT:
                    return checked((short)ConvertUSmallint(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_INT:
                    return checked((short)ConvertInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_UINT:
                    return checked((short)ConvertUInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_BIGINT:
                    return checked((short)ConvertBigInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_UBIGINT:
                    return checked((short)ConvertUBigInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_FLOAT:
                    return checked((short)ConvertFloat(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_DOUBLE:
                    return checked((short)ConvertDouble(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL64:
                    return (short)ConvertDecimal64(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL:
                    return (short)ConvertDecimal128(row, col);
                default:
                    throw new InvalidCastException("Cannot cast to short from " +
                                                   TDengineConstant.GetFieldTypeName((sbyte)_colType[col]));
            }
        }

        public int GetInt32(int row, int col)
        {
            CheckNull(row, col);
            switch ((TDengineDataType)_colType[col])
            {
                case TDengineDataType.TSDB_DATA_TYPE_TINYINT:
                    return ConvertTinyint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_UTINYINT:
                    return ConvertUTinyint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_SMALLINT:
                    return ConvertSmallint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_USMALLINT:
                    return ConvertUSmallint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_INT:
                    return ConvertInt(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_UINT:
                    return checked((int)ConvertUInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_BIGINT:
                    return checked((int)ConvertBigInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_UBIGINT:
                    return checked((int)ConvertUBigInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_FLOAT:
                    return checked((int)ConvertFloat(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_DOUBLE:
                    return checked((int)ConvertDouble(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL64:
                    return (int)ConvertDecimal64(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL:
                    return (int)ConvertDecimal128(row, col);
                default:
                    throw new InvalidCastException("Cannot cast to int from " +
                                                   TDengineConstant.GetFieldTypeName((sbyte)_colType[col]));
            }
        }

        public long GetInt64(int row, int col)
        {
            CheckNull(row, col);
            switch ((TDengineDataType)_colType[col])
            {
                case TDengineDataType.TSDB_DATA_TYPE_TIMESTAMP:
                    return ConvertBigInt(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_TINYINT:
                    return ConvertTinyint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_UTINYINT:
                    return ConvertUTinyint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_SMALLINT:
                    return ConvertSmallint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_USMALLINT:
                    return ConvertUSmallint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_INT:
                    return ConvertInt(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_UINT:
                    return ConvertUInt(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_BIGINT:
                    return ConvertBigInt(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_UBIGINT:
                    return checked((long)ConvertUBigInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_FLOAT:
                    return checked((long)ConvertFloat(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_DOUBLE:
                    return checked((long)ConvertDouble(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL64:
                    return (long)ConvertDecimal64(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL:
                    return (long)ConvertDecimal128(row, col);
                default:
                    throw new InvalidCastException("Cannot cast to long from " +
                                                   TDengineConstant.GetFieldTypeName((sbyte)_colType[col]));
            }
        }

        public bool GetBoolean(int row, int col)
        {
            CheckNull(row, col);
            switch ((TDengineDataType)_colType[col])
            {
                case TDengineDataType.TSDB_DATA_TYPE_BOOL:
                    return ConvertBool(row, col);
                default:
                    throw new InvalidCastException("Cannot cast to bool from " +
                                                   TDengineConstant.GetFieldTypeName((sbyte)_colType[col]));
            }
        }

        public DateTime GetDateTime(int row, int col)
        {
            CheckNull(row, col);
            switch ((TDengineDataType)_colType[col])
            {
                case TDengineDataType.TSDB_DATA_TYPE_TIMESTAMP:
                    return ConvertTime(row, col);
                default:
                    throw new InvalidCastException("Cannot cast to datetime from " +
                                                   TDengineConstant.GetFieldTypeName((sbyte)_colType[col]));
            }
        }

        public decimal GetDecimal(int row, int col)
        {
            CheckNull(row, col);
            switch ((TDengineDataType)_colType[col])
            {
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL64:
                    return ConvertDecimal64(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL:
                    return ConvertDecimal128(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_TINYINT:
                    return Convert.ToDecimal(ConvertTinyint(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_UTINYINT:
                    return Convert.ToDecimal(ConvertUTinyint(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_SMALLINT:
                    return Convert.ToDecimal(ConvertSmallint(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_USMALLINT:
                    return Convert.ToDecimal(ConvertUSmallint(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_INT:
                    return Convert.ToDecimal(ConvertInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_UINT:
                    return Convert.ToDecimal(ConvertUInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_BIGINT:
                    return Convert.ToDecimal(ConvertBigInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_UBIGINT:
                    return Convert.ToDecimal(ConvertUBigInt(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_FLOAT:
                    return Convert.ToDecimal(ConvertFloat(row, col));
                case TDengineDataType.TSDB_DATA_TYPE_DOUBLE:
                    return Convert.ToDecimal(ConvertDouble(row, col));
                default:
                    throw new InvalidCastException("Cannot cast to decimal from " +
                                                   TDengineConstant.GetFieldTypeName((sbyte)_colType[col]));
            }
        }

        public double GetDouble(int row, int col)
        {
            CheckNull(row, col);
            switch ((TDengineDataType)_colType[col])
            {
                case TDengineDataType.TSDB_DATA_TYPE_FLOAT:
                    return ConvertFloat(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_DOUBLE:
                    return ConvertDouble(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL64:
                    return (double)ConvertDecimal64(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL:
                    return (double)ConvertDecimal128(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_TINYINT:
                    return ConvertTinyint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_UTINYINT:
                    return ConvertUTinyint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_SMALLINT:
                    return ConvertSmallint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_USMALLINT:
                    return ConvertUSmallint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_INT:
                    return ConvertInt(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_UINT:
                    return ConvertUInt(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_BIGINT:
                    return ConvertBigInt(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_UBIGINT:
                    return ConvertUBigInt(row, col);
                default:
                    throw new InvalidCastException("Cannot cast to double from " +
                                                   TDengineConstant.GetFieldTypeName((sbyte)_colType[col]));
            }
        }

        public float GetFloat(int row, int col)
        {
            CheckNull(row, col);
            switch ((TDengineDataType)_colType[col])
            {
                case TDengineDataType.TSDB_DATA_TYPE_FLOAT:
                    return ConvertFloat(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_DOUBLE:
                    var val = ConvertDouble(row, col);
                    if (val >= float.MinValue && val <= float.MaxValue)
                    {
                        return (float)val;
                    }

                    throw new InvalidCastException("The double value cannot be safely cast to float.");
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL64:
                    return (float)ConvertDecimal64(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL:
                    return (float)ConvertDecimal128(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_TINYINT:
                    return ConvertTinyint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_UTINYINT:
                    return ConvertUTinyint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_SMALLINT:
                    return ConvertSmallint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_USMALLINT:
                    return ConvertUSmallint(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_INT:
                    return ConvertInt(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_UINT:
                    return ConvertUInt(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_BIGINT:
                    return ConvertBigInt(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_UBIGINT:
                    return ConvertUBigInt(row, col);
                default:
                    throw new InvalidCastException("Cannot cast to float from " +
                                                   TDengineConstant.GetFieldTypeName((sbyte)_colType[col]));
            }
        }

        public string GetString(int row, int col)
        {
            CheckNull(row, col);
            int offset;
            int length;
            switch ((TDengineDataType)_colType[col])
            {
                case TDengineDataType.TSDB_DATA_TYPE_BINARY:
                    TryGetVarData(row, col, TDengineConstant.Int16Size, out offset, out length);
                    return Encoding.UTF8.GetString(_block, offset, length);
                case TDengineDataType.TSDB_DATA_TYPE_NCHAR:
                    return ConvertNchar(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_JSONTAG:
                    TryGetVarData(row, col, TDengineConstant.Int16Size, out offset, out length);
                    return Encoding.UTF8.GetString(_block, offset, length);
                case TDengineDataType.TSDB_DATA_TYPE_VARBINARY:
                    TryGetVarData(row, col, TDengineConstant.Int16Size, out offset, out length);
                    return Encoding.UTF8.GetString(_block, offset, length);
                case TDengineDataType.TSDB_DATA_TYPE_BLOB:
                    TryGetVarData(row, col, TDengineConstant.Int32Size, out offset, out length);
                    return Encoding.UTF8.GetString(_block, offset, length);
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL64:
                    return ConvertDecimal64Str(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL:
                    return ConvertDecimal128Str(row, col);
                default:
                    throw new InvalidCastException("Cannot cast to string from " +
                                                   TDengineConstant.GetFieldTypeName((sbyte)_colType[col]));
            }
        }

        public DateTimeOffset GetDateTimeOffset(int row, int col)
        {
            CheckNull(row, col);
            switch ((TDengineDataType)_colType[col])
            {
                case TDengineDataType.TSDB_DATA_TYPE_TIMESTAMP:
                    return ConvertTimeOffset(row, col);
                default:
                    throw new InvalidCastException("Cannot cast to DateTimeOffset from " +
                                                   TDengineConstant.GetFieldTypeName((sbyte)_colType[col]));
            }
        }

        public int GetValues(int row, object[] values)
        {
            var minCount = Math.Min(values.Length, _cols);
            for (var i = 0; i < minCount; i++)
            {
                values[i] = Read(row, i);
            }

            return minCount;
        }

        private static long CopyVarData(byte[] source, int sourceOffset, int sourceLength, long dataOffset,
            byte[] buffer, int bufferOffset, int length)
        {
            if (dataOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dataOffset));
            }

            if (buffer == null)
            {
                return sourceLength;
            }

            if (bufferOffset < 0 || bufferOffset > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferOffset));
            }

            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            if (dataOffset >= sourceLength)
            {
                return 0;
            }

            var available = sourceLength - (int)dataOffset;
            var bufferLength = buffer.Length - bufferOffset;
            var count = Math.Min(Math.Min(available, bufferLength), length);
            Buffer.BlockCopy(source, sourceOffset + (int)dataOffset, buffer, bufferOffset, count);
            return count;
        }
    }
}
