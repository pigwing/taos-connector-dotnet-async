using System;

using System.Text;

namespace TDengine.Driver
{
    public class TDengineError : Exception
    {
        private const int MaxExtendedBytes = 256;
        private const int MaxExtendedStringLength = 2048;
        private const string HexDigits = "0123456789ABCDEF";

        public int Code { get; }
        public string Error { get; }

        public byte[] ExtendedErrorBytes { get; }
        public string ExtendedErrorString { get; }

        public enum InternalErrorCode
        {
            WS_RECONNECT_FAILED = 0xf001,
            WS_UNEXPECTED_MESSAGE = 0xf002,
            WS_CONNECTION_CLOSED = 0xf003,
            WS_WRITE_TIMEOUT = 0xf004,
            [Obsolete("Typo. Use WS_CONNECT_FAILED instead.")]
            WS_CONNEC_FAILED = 0xf005, // typo, for compatibility, do not change the name
            WS_CONNECT_FAILED = 0xf005,
            WS_RECEIVE_CLOSE_FRAME = 0xf006,
            WS_CONNECT_TIMEOUT = 0xf007,
            WS_PENDING_REQUEST_LIMIT = 0xf008,
            WS_REQUEST_OUTCOME_UNKNOWN = 0xf009,
            WS_POOL_REGISTRY_LIMIT = 0xf00a,
        }

        public TDengineError(int code, string error) : base($"code:[0x{(code & 0xffff):x}],error:{error}")
        {
            Code = code & 0xffff;
            Error = error;
        }

        public TDengineError(int code, string error, byte[] extendedErrorBytes, string extendedErrorString) : base(
            $"code:[0x{(code & 0xffff):x}],error:{error},extendedBytes:{Format(extendedErrorBytes)},extendedString:{Limit(extendedErrorString)}")
        {
            Code = code & 0xffff;
            Error = error;
            ExtendedErrorBytes = CopyPrefix(extendedErrorBytes);
            ExtendedErrorString = Limit(extendedErrorString);
        }

        internal TDengineError(int code, string error, Exception innerException)
            : base($"code:[0x{(code & 0xffff):x}],error:{error}", innerException)
        {
            Code = code & 0xffff;
            Error = error;
        }

        public TDengineError(int code, string error, string extendedErrorString) : base(
            $"code:[0x{(code & 0xffff):x}],error:{error},extendedString:{Limit(extendedErrorString)}")
        {
            Code = code & 0xffff;
            Error = error;
            ExtendedErrorString = Limit(extendedErrorString);
        }

        private static string Format(byte[] extendedError)
        {
            if (extendedError == null || extendedError.Length == 0)
            {
                return string.Empty;
            }

            var count = Math.Min(extendedError.Length, MaxExtendedBytes);
            var builder = new StringBuilder(count * 5 + 40);
            for (var i = 0; i < count; i++)
            {
                var value = extendedError[i];
                builder.Append("0x");
                builder.Append(HexDigits[value >> 4]);
                builder.Append(HexDigits[value & 0x0f]);
                builder.Append(',');
            }

            if (count < extendedError.Length)
            {
                builder.Append("...(");
                builder.Append(extendedError.Length);
                builder.Append(" bytes total)");
            }

            return builder.ToString();
        }

        private static byte[] CopyPrefix(byte[] value)
        {
            if (value == null)
            {
                return null;
            }

            var count = Math.Min(value.Length, MaxExtendedBytes);
            var result = new byte[count];
            Buffer.BlockCopy(value, 0, result, 0, count);
            return result;
        }

        private static string Limit(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxExtendedStringLength)
            {
                return value;
            }

            return value.Substring(0, MaxExtendedStringLength) + "...(truncated)";
        }
    }

    /// <summary>
    /// Describes a WebSocket request failure and whether the server may have received the request.
    /// </summary>
    public sealed class TDengineWebSocketRequestException : TDengineError
    {
        internal TDengineWebSocketRequestException(int code, string error, ulong requestId,
            bool requestMayHaveBeenSent, Exception innerException)
            : base(code, error, innerException)
        {
            RequestId = requestId;
            RequestMayHaveBeenSent = requestMayHaveBeenSent;
        }

        /// <summary>
        /// Gets the request identifier used by the WebSocket protocol.
        /// </summary>
        public ulong RequestId { get; }

        /// <summary>
        /// Gets whether the request may have reached the server. When true, replaying a write can duplicate data.
        /// </summary>
        public bool RequestMayHaveBeenSent { get; }
    }
}
