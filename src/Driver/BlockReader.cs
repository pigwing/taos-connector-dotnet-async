using System;
using System.Globalization;
using System.IO;
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
        internal const int MaximumColumnCount = 4096;
        private static readonly int ColInfoSize = TDengineConstant.Int8Size + TDengineConstant.Int32Size;
        private static readonly int RawBlockVersionOffset = 0;
        private static readonly int RawBlockLengthOffset = RawBlockVersionOffset + TDengineConstant.Int32Size;
        private static readonly int NumOfRowsOffset = RawBlockLengthOffset + TDengineConstant.Int32Size;
        private static readonly int NumOfColsOffset = NumOfRowsOffset + TDengineConstant.Int32Size;
        private static readonly int HasColumnSegmentOffset = NumOfColsOffset + TDengineConstant.Int32Size;
        private static readonly int GroupIdOffset = HasColumnSegmentOffset + TDengineConstant.Int32Size;
        private static readonly int ColInfoOffset = GroupIdOffset + TDengineConstant.UInt64Size;
        private static readonly Encoding Utf8Encoding = new UTF8Encoding(false, true);
        private static readonly Encoding Ucs4Encoding = new UTF32Encoding(false, false, true);

        private byte[] _block;
        private int _rows;
        private int _lengthOffset;
        private int _headerOffset;
        private int _nullBitMapOffset;

        private int _precision;
        private int[] _colHeadOffset;
        private int[] _colEndOffset;
        private int _cols;
        private byte[] _colType;
        private TimeZoneInfo _tz;
        private byte[] _scales;

        private int _offset;

        [StructLayout(LayoutKind.Explicit)]
        private struct Int32SingleUnion
        {
            [FieldOffset(0)] internal int Int32;
            [FieldOffset(0)] internal float Single;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static short ReadInt16(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BinaryPrimitives.ReadInt16LittleEndian(source.AsSpan(offset, sizeof(short)));
#else
            return (short)(source[offset] | (source[offset + 1] << 8));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort ReadUInt16(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset, sizeof(ushort)));
#else
            return (ushort)(source[offset] | (source[offset + 1] << 8));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadInt32(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(offset, sizeof(int)));
#else
            return source[offset]
                   | (source[offset + 1] << 8)
                   | (source[offset + 2] << 16)
                   | (source[offset + 3] << 24);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ReadUInt32(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, sizeof(uint)));
#else
            return (uint)(source[offset]
                          | (source[offset + 1] << 8)
                          | (source[offset + 2] << 16)
                          | (source[offset + 3] << 24));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long ReadInt64(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BinaryPrimitives.ReadInt64LittleEndian(source.AsSpan(offset, sizeof(long)));
#else
            return (long)ReadUInt64(source, offset);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ReadUInt64(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(offset, sizeof(ulong)));
#else
            return source[offset]
                   | ((ulong)source[offset + 1] << 8)
                   | ((ulong)source[offset + 2] << 16)
                   | ((ulong)source[offset + 3] << 24)
                   | ((ulong)source[offset + 4] << 32)
                   | ((ulong)source[offset + 5] << 40)
                   | ((ulong)source[offset + 6] << 48)
                   | ((ulong)source[offset + 7] << 56);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float ReadSingle(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BitConverter.Int32BitsToSingle(ReadInt32(source, offset));
#else
            return new Int32SingleUnion { Int32 = ReadInt32(source, offset) }.Single;
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double ReadDouble(byte[] source, int offset)
        {
#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
            return BitConverter.Int64BitsToDouble(ReadInt64(source, offset));
#else
            return BitConverter.Int64BitsToDouble(ReadInt64(source, offset));
#endif
        }

        public BlockReader(int offset, int cols, int precision, byte[] colType, byte[] scales,
            TimeZoneInfo tz = null) :
            this(offset, tz)
        {
            if (cols < 0 || cols > MaximumColumnCount)
            {
                throw new ArgumentOutOfRangeException(nameof(cols),
                    $"Column count must be between 0 and {MaximumColumnCount}.");
            }
            if (colType == null) throw new ArgumentNullException(nameof(colType));
            if (colType.Length != cols)
            {
                throw new ArgumentException("Column type count must match cols.", nameof(colType));
            }

            if (scales != null && scales.Length > cols)
            {
                throw new ArgumentException("Column scale count cannot exceed cols.", nameof(scales));
            }

            _cols = cols;
            _precision = precision;
            _colHeadOffset = new int[cols];
            _colEndOffset = new int[cols];
            _colType = new byte[cols];
            Buffer.BlockCopy(colType, 0, _colType, 0, cols);
            _scales = new byte[cols];
            if (scales != null)
            {
                Buffer.BlockCopy(scales, 0, _scales, 0, scales.Length);
            }
        }

        // Constructor for TMQ blocks
        public BlockReader(int offset, TimeZoneInfo tz = null)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
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
            if (pBlock == IntPtr.Zero) throw new ArgumentException("Block pointer cannot be zero.", nameof(pBlock));
            var blockSize = GetBlockSize(pBlock);
            if (blockSize < ColInfoOffset)
            {
                throw new InvalidDataException("Invalid raw block length.");
            }

            byte[] dataArray = new byte[blockSize];
            Marshal.Copy(pBlock, dataArray, 0, blockSize);
            SetBlock(dataArray);
            if (rows >= 0 && rows != _rows)
            {
                throw new InvalidDataException("Raw block row count does not match the supplied row count.");
            }
        }

        private Int32 GetBlockSize(IntPtr pBlock)
        {
            return Marshal.ReadInt32(pBlock + _offset + RawBlockLengthOffset);
        }

        // Set block for raw blocks (used in WSRows)
        public void SetBlock(byte[] block)
        {
            ValidateRange(block, _offset, ColInfoOffset, "header");
            var version = ReadInt32(block, _offset + RawBlockVersionOffset);
            if (version != 1 && version != 2)
            {
                throw new InvalidDataException("Unsupported raw block version " + version + ".");
            }

            var rawBlockLength = ReadInt32(block, _offset + RawBlockLengthOffset);
            if (rawBlockLength < ColInfoOffset)
            {
                throw new InvalidDataException("Invalid raw block length.");
            }

            var rawBlockEnd = checked((long)_offset + rawBlockLength);
            ValidateRange(block, _offset, rawBlockLength, "body");

            var rows = ReadInt32(block, _offset + NumOfRowsOffset);
            var cols = ReadInt32(block, _offset + NumOfColsOffset);
            if (rows < 0)
            {
                throw new InvalidDataException("Raw block contains a negative row count.");
            }

            if (cols < 0 || cols != _cols)
            {
                throw new InvalidDataException(
                    $"Raw block column count ({cols}) does not match metadata ({_cols}).");
            }

            var lengthOffset = checked((long)_offset + ColInfoOffset + (long)_cols * ColInfoSize);
            var headerOffset = checked(lengthOffset + (long)_cols * TDengineConstant.Int32Size);
            if (headerOffset > rawBlockEnd || headerOffset > int.MaxValue)
            {
                throw new InvalidDataException("Raw block column metadata exceeds the block length.");
            }

            var nullBitmapLength = checked((rows + 7L) / 8L);
            if (nullBitmapLength > int.MaxValue)
            {
                throw new InvalidDataException("Raw block null bitmap is too large.");
            }

            var columnOffset = headerOffset;
            for (var i = 0; i < _cols; i++)
            {
                var rawType = block[checked(_offset + ColInfoOffset + i * ColInfoSize)];
                if (rawType != _colType[i])
                {
                    throw new InvalidDataException(
                        $"Raw block column type at index {i} does not match result metadata.");
                }

                var colLength = ReadInt32(block,
                    checked((int)lengthOffset + TDengineConstant.Int32Size * i));
                if (colLength < 0)
                {
                    throw new InvalidDataException($"Raw block column {i} contains a negative length.");
                }

                long segmentLength;
                if (TDengineConstant.IsVarDataType(_colType[i]))
                {
                    segmentLength = checked((long)TDengineConstant.Int32Size * rows + colLength);
                }
                else
                {
                    var minimumDataLength = checked((long)GetFixedTypeLength(_colType[i]) * rows);
                    if (colLength < minimumDataLength)
                    {
                        throw new InvalidDataException($"Raw block fixed column {i} is shorter than its row data.");
                    }

                    segmentLength = checked(nullBitmapLength + colLength);
                }

                var columnEnd = checked(columnOffset + segmentLength);
                if (columnOffset > int.MaxValue || columnEnd > rawBlockEnd || columnEnd > int.MaxValue)
                {
                    throw new InvalidDataException($"Raw block column {i} exceeds the block length.");
                }

                columnOffset = columnEnd;
            }

            var trailingPadding = rawBlockEnd - columnOffset;
            if (trailingPadding < 0 || trailingPadding > 7)
            {
                throw new InvalidDataException("Raw block length does not match its column segments.");
            }

            for (var i = 0; i < trailingPadding; i++)
            {
                if (block[(int)columnOffset + i] != 0)
                {
                    throw new InvalidDataException("Raw block contains non-zero trailing padding.");
                }
            }

            columnOffset = headerOffset;
            for (var i = 0; i < _cols; i++)
            {
                var colLength = ReadInt32(block,
                    checked((int)lengthOffset + TDengineConstant.Int32Size * i));
                long segmentLength;
                if (TDengineConstant.IsVarDataType(_colType[i]))
                {
                    segmentLength = checked((long)TDengineConstant.Int32Size * rows + colLength);
                }
                else
                {
                    segmentLength = checked(nullBitmapLength + colLength);
                }

                var columnEnd = checked(columnOffset + segmentLength);
                _colHeadOffset[i] = (int)columnOffset;
                _colEndOffset[i] = (int)columnEnd;
                columnOffset = columnEnd;
            }

            _block = block;
            _rows = rows;
            _nullBitMapOffset = (int)nullBitmapLength;
            _lengthOffset = (int)lengthOffset;
            _headerOffset = (int)headerOffset;
        }

        internal void SetBlock(byte[] block, int offset)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            var previousOffset = _offset;
            _offset = offset;
            try
            {
                SetBlock(block);
            }
            catch
            {
                _offset = previousOffset;
                throw;
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

            if (_colEndOffset != null)
            {
                Array.Clear(_colEndOffset, 0, _colEndOffset.Length);
            }
        }

        // Set block for for TMQ blocks
        public void SetTMQBlock(byte[] block, int precision, int offset)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (!TDengineConstant.IsValidTimestampPrecision(precision))
            {
                throw new InvalidDataException("TMQ raw block timestamp precision is invalid.");
            }

            ValidateRange(block, offset, ColInfoOffset, "TMQ header");
            var cols = ReadInt32(block, offset + NumOfColsOffset);
            if (cols < 0 || cols > MaximumColumnCount)
            {
                throw new InvalidDataException(
                    $"TMQ raw block column count must be between 0 and {MaximumColumnCount}.");
            }

            var metadataLength = checked((long)ColInfoOffset + (long)cols * ColInfoSize);
            ValidateRange(block, offset, metadataLength, "TMQ column metadata");
            var colTypes = new byte[cols];
            var scales = new byte[cols];
            for (var i = 0; i < cols; i++)
            {
                colTypes[i] = block[offset + ColInfoOffset + i * ColInfoSize];
                if (!TDengineConstant.IsSupportedDataType(colTypes[i]))
                {
                    throw new InvalidDataException($"TMQ raw block column {i} has an unsupported data type.");
                }

                if (colTypes[i] == (byte)TDengineDataType.TSDB_DATA_TYPE_DECIMAL64 ||
                    colTypes[i] == (byte)TDengineDataType.TSDB_DATA_TYPE_DECIMAL)
                {
                    scales[i] = block[offset + ColInfoOffset + i * ColInfoSize + TDengineConstant.Int8Size];
                    var maximumScale = colTypes[i] == (byte)TDengineDataType.TSDB_DATA_TYPE_DECIMAL64
                        ? 18
                        : 38;
                    if (scales[i] > maximumScale)
                    {
                        throw new InvalidDataException($"TMQ raw block decimal scale at column {i} is invalid.");
                    }
                }
            }

            var previousOffset = _offset;
            var previousPrecision = _precision;
            var previousCols = _cols;
            var previousColHeadOffset = _colHeadOffset;
            var previousColEndOffset = _colEndOffset;
            var previousColType = _colType;
            var previousScales = _scales;
            _offset = offset;
            _precision = precision;
            _cols = cols;
            _colHeadOffset = new int[cols];
            _colEndOffset = new int[cols];
            _colType = colTypes;
            _scales = scales;
            try
            {
                SetBlock(block);
            }
            catch
            {
                _offset = previousOffset;
                _precision = previousPrecision;
                _cols = previousCols;
                _colHeadOffset = previousColHeadOffset;
                _colEndOffset = previousColEndOffset;
                _colType = previousColType;
                _scales = previousScales;
                throw;
            }
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

        private static void ValidateRange(byte[] block, long offset, long length, string segment)
        {
            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }

            if (offset < 0 || length < 0 || offset > block.Length || length > block.Length - offset)
            {
                throw new InvalidDataException($"Raw block {segment} exceeds the supplied buffer.");
            }
        }

        private static int GetFixedTypeLength(byte type)
        {
            switch ((TDengineDataType)type)
            {
                case TDengineDataType.TSDB_DATA_TYPE_NULL:
                case TDengineDataType.TSDB_DATA_TYPE_BOOL:
                case TDengineDataType.TSDB_DATA_TYPE_TINYINT:
                case TDengineDataType.TSDB_DATA_TYPE_UTINYINT:
                    return 1;
                case TDengineDataType.TSDB_DATA_TYPE_SMALLINT:
                case TDengineDataType.TSDB_DATA_TYPE_USMALLINT:
                    return 2;
                case TDengineDataType.TSDB_DATA_TYPE_INT:
                case TDengineDataType.TSDB_DATA_TYPE_UINT:
                case TDengineDataType.TSDB_DATA_TYPE_FLOAT:
                    return 4;
                case TDengineDataType.TSDB_DATA_TYPE_BIGINT:
                case TDengineDataType.TSDB_DATA_TYPE_UBIGINT:
                case TDengineDataType.TSDB_DATA_TYPE_DOUBLE:
                case TDengineDataType.TSDB_DATA_TYPE_TIMESTAMP:
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL64:
                    return 8;
                case TDengineDataType.TSDB_DATA_TYPE_DECIMAL:
                    return 16;
                default:
                    throw new InvalidDataException("Raw block contains an unsupported fixed column type " + type + ".");
            }
        }

        private bool ItemIsNull(int headOffset, int row) =>
            TDengineConstant.BitmapIsNull(_block[headOffset + TDengineConstant.CharOffset(row)], row);

        public object Read(int row, int col)
        {
            ValidateCell(row, col);
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
            var value = ReadInt64(_block,
                _colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.Int64Size);
            return FormatDecimal(value.ToString(CultureInfo.InvariantCulture), _scales[col]);
        }

        private decimal ConvertDecimal64(int row, int col)
        {
            var int64Value = ReadInt64(_block,
                _colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.Int64Size);
            var scale = _scales[col];
            if (scale > 28)
            {
                throw new OverflowException("A Decimal64 scale greater than 28 cannot be represented by System.Decimal.");
            }

            var isNegative = int64Value < 0;
            var magnitude = isNegative
                ? (ulong)(-(int64Value + 1)) + 1
                : (ulong)int64Value;
            var lo = (int)(magnitude & uint.MaxValue);
            var mid = (int)(magnitude >> 32);
            return new decimal(lo, mid, 0, isNegative, scale);
        }

        private string ConvertDecimal128Str(int row, int col)
        {
            var startIndex = _colHeadOffset[col] + _nullBitMapOffset + row * TDengineConstant.Int64Size * 2;
            var lo = ReadUInt64(_block, startIndex);
            var hi = ReadInt64(_block, startIndex + TDengineConstant.UInt64Size);
            var str = FormatI128(hi, lo);
            return FormatDecimal(str, _scales[col]);
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
            if (scale > 28)
            {
                throw new OverflowException("A Decimal128 scale greater than 28 cannot be represented by System.Decimal.");
            }

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
            if (str.StartsWith("-", StringComparison.Ordinal))
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
            if ((count & 3) != 0)
            {
                throw new InvalidDataException("NCHAR data length must be a multiple of four bytes.");
            }

            return Ucs4Encoding.GetString(ucs4Bytes, offset, count);
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
            ValidateCell(row, col);
            if (!TDengineConstant.IsVarDataType(_colType[col]))
            {
                throw new InvalidOperationException("The requested column is not variable-length data.");
            }

            if (lengthHeaderSize != TDengineConstant.Int16Size &&
                lengthHeaderSize != TDengineConstant.Int32Size)
            {
                throw new ArgumentOutOfRangeException(nameof(lengthHeaderSize));
            }

            var offsetTableEntry = checked(_colHeadOffset[col] + row * TDengineConstant.Int32Size);
            var offset = ReadInt32(_block, offsetTableEntry);
            if (offset == -1)
            {
                dataOffset = 0;
                length = 0;
                return false;
            }

            if (offset < 0)
            {
                throw new InvalidDataException("Variable-length column contains a negative data offset.");
            }

            var dataRegionOffset = checked((long)_colHeadOffset[col] + (long)TDengineConstant.Int32Size * _rows);
            var currentRowLong = checked(dataRegionOffset + offset);
            if (currentRowLong > _colEndOffset[col] - lengthHeaderSize || currentRowLong > int.MaxValue)
            {
                throw new InvalidDataException("Variable-length column data header exceeds its column segment.");
            }

            var currentRow = (int)currentRowLong;
            if (lengthHeaderSize == TDengineConstant.Int16Size)
            {
                length = ReadUInt16(_block, currentRow);
            }
            else
            {
                var unsignedLength = ReadUInt32(_block, currentRow);
                if (unsignedLength > int.MaxValue)
                {
                    throw new InvalidDataException("Variable-length column value exceeds the supported length.");
                }

                length = (int)unsignedLength;
            }

            var valueOffset = checked(currentRow + lengthHeaderSize);
            if (length > _colEndOffset[col] - valueOffset)
            {
                throw new InvalidDataException("Variable-length column value exceeds its column segment.");
            }

            dataOffset = valueOffset;
            return true;
        }

        public long GetChars(int row, int col, long dataOffset, char[] buffer, int bufferOffset, int length)
        {
            ValidateCell(row, col);
            if (!TDengineConstant.IsVarDataType(_colType[col]))
            {
                throw new InvalidCastException("GetChars cannot be used on non-character columns.");
            }

            if (dataOffset < 0 || dataOffset > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(dataOffset));
            }

            if ((TDengineDataType)_colType[col] == TDengineDataType.TSDB_DATA_TYPE_NCHAR)
            {
                int sourceOffset;
                int sourceLength;
                if (!TryGetVarData(row, col, TDengineConstant.Int16Size, out sourceOffset, out sourceLength))
                {
                    return 0;
                }

                return CopyNcharChars(_block, sourceOffset, sourceLength, dataOffset, buffer, bufferOffset, length);
            }

            var value = GetCharacterValue(row, col);
            if (value == null)
            {
                return 0;
            }

            if (buffer == null)
            {
                return value.Length;
            }

            ValidateCopyArguments(buffer.Length, bufferOffset, length);
            if (dataOffset >= value.Length)
            {
                return 0;
            }

            var count = Math.Min(Math.Min(value.Length - (int)dataOffset, buffer.Length - bufferOffset), length);
            value.CopyTo((int)dataOffset, buffer, bufferOffset, count);
            return count;
        }

        public char GetChar(int row, int col)
        {
            ValidateCell(row, col);
            if (!TDengineConstant.IsVarDataType(_colType[col]))
            {
                throw new InvalidCastException("GetChar cannot be used on non-character columns.");
            }

            if ((TDengineDataType)_colType[col] == TDengineDataType.TSDB_DATA_TYPE_NCHAR)
            {
                int sourceOffset;
                int sourceLength;
                if (!TryGetVarData(row, col, TDengineConstant.Int16Size, out sourceOffset, out sourceLength))
                {
                    throw new InvalidCastException("Cannot cast null value to char.");
                }

                var charCount = GetNcharCharCount(_block, sourceOffset, sourceLength);
                if (charCount == 0)
                {
                    throw new InvalidOperationException("Cannot read a character from an empty value.");
                }

                var scalar = ReadUcs4Scalar(_block, sourceOffset);
                return scalar <= char.MaxValue
                    ? (char)scalar
                    : (char)(((scalar - 0x10000U) >> 10) + 0xd800U);
            }

            var value = GetCharacterValue(row, col);
            if (value == null)
            {
                throw new InvalidCastException("Cannot cast null value to char.");
            }

            if (value.Length == 0)
            {
                throw new InvalidOperationException("Cannot read a character from an empty value.");
            }

            return value[0];
        }

        public long GetBytes(int row, int col, long dataOffset, byte[] buffer, int bufferOffset, int length)
        {
            ValidateCell(row, col);
            if (!TDengineConstant.IsVarDataType(_colType[col]))
            {
                throw new InvalidCastException("GetBytes cannot be used on fixed-length columns.");
            }

            int sourceOffset;
            int sourceLength;
            switch ((TDengineDataType)_colType[col])
            {
                case TDengineDataType.TSDB_DATA_TYPE_NCHAR:
                    if (!TryGetVarData(row, col, TDengineConstant.Int16Size, out sourceOffset, out sourceLength))
                    {
                        return 0;
                    }

                    return CopyNcharUtf8Bytes(_block, sourceOffset, sourceLength, dataOffset, buffer,
                        bufferOffset, length);
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

        private static long CopyNcharChars(byte[] source, int sourceOffset, int sourceLength, long dataOffset,
            char[] buffer, int bufferOffset, int length)
        {
            var charCount = GetNcharCharCount(source, sourceOffset, sourceLength);
            if (buffer == null)
            {
                return charCount;
            }

            ValidateCopyArguments(buffer.Length, bufferOffset, length);
            if (dataOffset >= charCount)
            {
                return 0;
            }

            var copyCount = Math.Min(Math.Min(charCount - (int)dataOffset, buffer.Length - bufferOffset), length);
            if (copyCount == 0)
            {
                return 0;
            }

            var characterIndex = 0;
            var written = 0;
            var end = sourceOffset + sourceLength;
            for (var offset = sourceOffset; offset < end && written < copyCount; offset += sizeof(uint))
            {
                var scalar = ReadUcs4Scalar(source, offset);
                if (scalar <= char.MaxValue)
                {
                    CopyNcharChar((char)scalar, dataOffset, ref characterIndex, buffer, bufferOffset,
                        ref written, copyCount);
                    continue;
                }

                scalar -= 0x10000U;
                CopyNcharChar((char)((scalar >> 10) + 0xd800U), dataOffset, ref characterIndex, buffer,
                    bufferOffset, ref written, copyCount);
                if (written < copyCount)
                {
                    CopyNcharChar((char)((scalar & 0x3ffU) + 0xdc00U), dataOffset, ref characterIndex,
                        buffer, bufferOffset, ref written, copyCount);
                }
            }

            return written;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyNcharChar(char value, long dataOffset, ref int characterIndex, char[] buffer,
            int bufferOffset, ref int written, int copyCount)
        {
            if (characterIndex >= dataOffset && written < copyCount)
            {
                buffer[bufferOffset + written] = value;
                written++;
            }

            characterIndex++;
        }

        private static long CopyNcharUtf8Bytes(byte[] source, int sourceOffset, int sourceLength,
            long dataOffset, byte[] buffer, int bufferOffset, int length)
        {
            if (dataOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dataOffset));
            }

            var byteCount = GetNcharUtf8ByteCount(source, sourceOffset, sourceLength);
            if (buffer == null)
            {
                return byteCount;
            }

            ValidateCopyArguments(buffer.Length, bufferOffset, length);
            if (dataOffset >= byteCount || dataOffset > int.MaxValue)
            {
                return 0;
            }

            var copyCount = Math.Min(Math.Min(byteCount - (int)dataOffset, buffer.Length - bufferOffset), length);
            if (copyCount == 0)
            {
                return 0;
            }

            var byteIndex = 0;
            var written = 0;
            var end = sourceOffset + sourceLength;
            for (var offset = sourceOffset; offset < end && written < copyCount; offset += sizeof(uint))
            {
                var scalar = ReadUcs4Scalar(source, offset);
                if (scalar <= 0x7fU)
                {
                    CopyNcharUtf8Byte((byte)scalar, dataOffset, ref byteIndex, buffer, bufferOffset,
                        ref written, copyCount);
                }
                else if (scalar <= 0x7ffU)
                {
                    CopyNcharUtf8Byte((byte)(0xc0U | (scalar >> 6)), dataOffset, ref byteIndex, buffer,
                        bufferOffset, ref written, copyCount);
                    CopyNcharUtf8Byte((byte)(0x80U | (scalar & 0x3fU)), dataOffset, ref byteIndex, buffer,
                        bufferOffset, ref written, copyCount);
                }
                else if (scalar <= 0xffffU)
                {
                    CopyNcharUtf8Byte((byte)(0xe0U | (scalar >> 12)), dataOffset, ref byteIndex, buffer,
                        bufferOffset, ref written, copyCount);
                    CopyNcharUtf8Byte((byte)(0x80U | ((scalar >> 6) & 0x3fU)), dataOffset, ref byteIndex,
                        buffer, bufferOffset, ref written, copyCount);
                    CopyNcharUtf8Byte((byte)(0x80U | (scalar & 0x3fU)), dataOffset, ref byteIndex, buffer,
                        bufferOffset, ref written, copyCount);
                }
                else
                {
                    CopyNcharUtf8Byte((byte)(0xf0U | (scalar >> 18)), dataOffset, ref byteIndex, buffer,
                        bufferOffset, ref written, copyCount);
                    CopyNcharUtf8Byte((byte)(0x80U | ((scalar >> 12) & 0x3fU)), dataOffset, ref byteIndex,
                        buffer, bufferOffset, ref written, copyCount);
                    CopyNcharUtf8Byte((byte)(0x80U | ((scalar >> 6) & 0x3fU)), dataOffset, ref byteIndex,
                        buffer, bufferOffset, ref written, copyCount);
                    CopyNcharUtf8Byte((byte)(0x80U | (scalar & 0x3fU)), dataOffset, ref byteIndex, buffer,
                        bufferOffset, ref written, copyCount);
                }
            }

            return written;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyNcharUtf8Byte(byte value, long dataOffset, ref int byteIndex, byte[] buffer,
            int bufferOffset, ref int written, int copyCount)
        {
            if (byteIndex >= dataOffset && written < copyCount)
            {
                buffer[bufferOffset + written] = value;
                written++;
            }

            byteIndex++;
        }

        private static int GetNcharCharCount(byte[] source, int sourceOffset, int sourceLength)
        {
            ValidateNcharRange(source, sourceOffset, sourceLength);
            var charCount = 0;
            var end = sourceOffset + sourceLength;
            for (var offset = sourceOffset; offset < end; offset += sizeof(uint))
            {
                charCount = checked(charCount + (ReadUcs4Scalar(source, offset) <= char.MaxValue ? 1 : 2));
            }

            return charCount;
        }

        private static int GetNcharUtf8ByteCount(byte[] source, int sourceOffset, int sourceLength)
        {
            ValidateNcharRange(source, sourceOffset, sourceLength);
            var byteCount = 0;
            var end = sourceOffset + sourceLength;
            for (var offset = sourceOffset; offset < end; offset += sizeof(uint))
            {
                var scalar = ReadUcs4Scalar(source, offset);
                byteCount = checked(byteCount + (scalar <= 0x7fU
                    ? 1
                    : scalar <= 0x7ffU
                        ? 2
                        : scalar <= 0xffffU
                            ? 3
                            : 4));
            }

            return byteCount;
        }

        private static void ValidateNcharRange(byte[] source, int sourceOffset, int sourceLength)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (sourceOffset < 0 || sourceLength < 0 || sourceOffset > source.Length ||
                sourceLength > source.Length - sourceOffset)
            {
                throw new InvalidDataException("NCHAR value exceeds the raw block buffer.");
            }

            if ((sourceLength & 3) != 0)
            {
                throw new InvalidDataException("NCHAR data length must be a multiple of four bytes.");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ReadUcs4Scalar(byte[] source, int offset)
        {
            var scalar = ReadUInt32(source, offset);
            if (scalar > 0x10ffffU || scalar >= 0xd800U && scalar <= 0xdfffU)
            {
                throw new InvalidDataException("NCHAR data contains an invalid Unicode scalar value.");
            }

            return scalar;
        }

        private string GetCharacterValue(int row, int col)
        {
            int sourceOffset;
            int sourceLength;
            switch ((TDengineDataType)_colType[col])
            {
                case TDengineDataType.TSDB_DATA_TYPE_NCHAR:
                    return ConvertNchar(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_BLOB:
                    if (!TryGetVarData(row, col, TDengineConstant.Int32Size, out sourceOffset,
                            out sourceLength))
                    {
                        return null;
                    }

                    return Utf8Encoding.GetString(_block, sourceOffset, sourceLength);
                default:
                    if (!TryGetVarData(row, col, TDengineConstant.Int16Size, out sourceOffset,
                            out sourceLength))
                    {
                        return null;
                    }

                    return Utf8Encoding.GetString(_block, sourceOffset, sourceLength);
            }
        }

        private static void ValidateCopyArguments(int bufferLength, int bufferOffset, int length)
        {
            if (bufferOffset < 0 || bufferOffset > bufferLength)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferOffset));
            }

            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }
        }

        private bool VarDataTypeIsNull(int row, int col)
        {
            var offset = ReadInt32(_block, _colHeadOffset[col] + row * TDengineConstant.Int32Size);
            if (offset < -1)
            {
                throw new InvalidDataException("Variable-length column contains an invalid null offset.");
            }

            return offset == -1;
        }

        public bool IsDBNull(int row, int col)
        {
            ValidateCell(row, col);
            return TDengineConstant.IsVarDataType(_colType[col]) ? VarDataTypeIsNull(row, col) : ItemIsNull(_colHeadOffset[col], row);
        }

        private void CheckNull(int row, int col)
        {
            ValidateCell(row, col);
            if (IsDBNull(row, col))
            {
                throw new InvalidCastException("Cannot cast null value to non-nullable type.");
            }
        }

        private void ValidateCell(int row, int col)
        {
            if (_block == null)
            {
                throw new InvalidOperationException("No raw block has been loaded.");
            }

            if (row < 0 || row >= _rows)
            {
                throw new ArgumentOutOfRangeException(nameof(row), $"Value must be between 0 and {_rows - 1}.");
            }

            if (col < 0 || col >= _cols)
            {
                throw new ArgumentOutOfRangeException(nameof(col), $"Value must be between 0 and {_cols - 1}.");
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
                    if (double.IsNaN(val) || double.IsInfinity(val) ||
                        (val >= float.MinValue && val <= float.MaxValue))
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
                    return Utf8Encoding.GetString(_block, offset, length);
                case TDengineDataType.TSDB_DATA_TYPE_NCHAR:
                    return ConvertNchar(row, col);
                case TDengineDataType.TSDB_DATA_TYPE_JSONTAG:
                    TryGetVarData(row, col, TDengineConstant.Int16Size, out offset, out length);
                    return Utf8Encoding.GetString(_block, offset, length);
                case TDengineDataType.TSDB_DATA_TYPE_VARBINARY:
                    TryGetVarData(row, col, TDengineConstant.Int16Size, out offset, out length);
                    return Utf8Encoding.GetString(_block, offset, length);
                case TDengineDataType.TSDB_DATA_TYPE_BLOB:
                    TryGetVarData(row, col, TDengineConstant.Int32Size, out offset, out length);
                    return Utf8Encoding.GetString(_block, offset, length);
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
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (_block == null)
            {
                throw new InvalidOperationException("No raw block has been loaded.");
            }

            if (row < 0 || row >= _rows)
            {
                throw new ArgumentOutOfRangeException(nameof(row), $"Value must be between 0 and {_rows - 1}.");
            }

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
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (sourceOffset < 0 || sourceLength < 0 || sourceOffset > source.Length ||
                sourceLength > source.Length - sourceOffset)
            {
                throw new InvalidDataException("Variable-length value exceeds the raw block buffer.");
            }

            if (dataOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dataOffset));
            }

            if (buffer == null)
            {
                return sourceLength;
            }

            ValidateCopyArguments(buffer.Length, bufferOffset, length);

            if (dataOffset >= sourceLength || dataOffset > int.MaxValue)
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
