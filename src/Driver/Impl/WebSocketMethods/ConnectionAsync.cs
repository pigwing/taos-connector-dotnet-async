using System;
using System.Buffers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;

namespace TDengine.Driver.Impl.WebSocketMethods
{
    public partial class ConnectionAsync : BaseConnectionAsync
    {
        private static readonly Encoding Utf8Encoding = new UTF8Encoding(false, true);
        private readonly string _user;
        private readonly string _password;
        private readonly string _db;
        private readonly string _bearerToken;
        private readonly string _timezone = string.Empty;

        public ConnectionAsync(string addr, string user, string password, string db, string bearerToken,
            TimeSpan connectTimeout = default, TimeSpan readTimeout = default,
            TimeSpan writeTimeout = default, bool enableCompression = false,
            TimeZoneInfo connectionTimezone = null)
            : base(addr, connectTimeout, readTimeout, writeTimeout, enableCompression)
        {
            _user = user;
            _password = password;
            _db = db;
            _bearerToken = bearerToken;
            if (connectionTimezone != null)
            {
                _timezone = connectionTimezone.Id;
            }
        }

        public async Task<WSConnResp> ConnectAsync(CancellationToken cancellationToken = default)
        {
            return await ConnectAsync(false, cancellationToken).ConfigureAwait(false);
        }

        public async Task<WSConnResp> ConnectAsync(bool listInstances, CancellationToken cancellationToken)
        {
            try
            {
                await ClientConnectAsync(cancellationToken).ConfigureAwait(false);
                var reqId = _GetReqId();
                return await SendJsonBackJsonAsync<WSConnReq, WSConnResp>(WSAction.Conn, new WSConnReq
                {
                    ReqId = reqId,
                    User = _user,
                    Password = _password,
                    Db = _db,
                    Timezone = _timezone,
                    App = TDengineConstant.ProcessName,
                    Connector = TDengineConstant.WsConnectorInfo,
                    BearerToken = _bearerToken,
                    ListInstances = listInstances ? true : (bool?)null
                }, reqId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await CloseAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the connection or authentication failure.
                }

                throw;
            }
        }

        public async Task<WSQueryResp> BinaryQueryAsync(string sql, ulong reqId = 0,
            CancellationToken cancellationToken = default)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (reqId == 0)
            {
                reqId = _GetReqId();
            }

            var sqlByteCount = Utf8Encoding.GetByteCount(sql);
            var requestLength = checked(30 + sqlByteCount);
            if (requestLength > MaximumMessageSize)
            {
                throw new ArgumentException(
                    $"The UTF-8 SQL payload exceeds the {MaximumMessageSize - 30} byte limit.", nameof(sql));
            }

            var req = ArrayPool<byte>.Shared.Rent(requestLength);
            var ownershipTransferred = false;
            try
            {
                WriteUInt64ToBytes(req, reqId, 0);
                WriteUInt64ToBytes(req, 0, 8);
                WriteUInt64ToBytes(req, WSActionBinary.BinaryQueryMessage, 16);
                WriteUInt16ToBytes(req, 1, 24);
                WriteUInt32ToBytes(req, (uint)sqlByteCount, 26);
                Utf8Encoding.GetBytes(sql, 0, sql.Length, req, 30);

                ownershipTransferred = true;
                return await SendPooledBinaryBackJsonAsync<WSQueryResp>(req, requestLength, reqId,
                        WSAction.BinaryQuery,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (!ownershipTransferred)
                {
                    ReturnPooledBuffer(req, requestLength);
                }
            }
        }

        public async Task<byte[]> FetchRawBlockBinaryAsync(ulong resultId,
            CancellationToken cancellationToken = default)
        {
            if (resultId == 0) throw new ArgumentOutOfRangeException(nameof(resultId));
            const int requestLength = 32;
            var req = ArrayPool<byte>.Shared.Rent(requestLength);
            var reqId = _GetReqId();
            var ownershipTransferred = false;
            try
            {
                WriteUInt64ToBytes(req, reqId, 0);
                WriteUInt64ToBytes(req, resultId, 8);
                WriteUInt64ToBytes(req, WSActionBinary.FetchRawBlockMessage, 16);
                WriteUInt64ToBytes(req, 1, 24);
                ownershipTransferred = true;
                return await SendPooledBinaryBackBytesAsync(req, requestLength, reqId, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (!ownershipTransferred)
                {
                    ReturnPooledBuffer(req, requestLength);
                }
            }
        }

        public async Task FreeResultAsync(ulong resultId, CancellationToken cancellationToken = default)
        {
            if (resultId == 0) throw new ArgumentOutOfRangeException(nameof(resultId));
            var reqId = _GetReqId();
            await SendJsonAsync(WSAction.FreeResult, new WSFreeResultReq
            {
                ReqId = reqId,
                ResultId = resultId
            }, reqId, cancellationToken).ConfigureAwait(false);
        }

        internal async Task ValidateConnectionAsync(CancellationToken cancellationToken)
        {
            var response = await SendJsonBackJsonAsync<WSVersionReq, WSVersionResp>(WSAction.Version,
                new WSVersionReq(), 0, cancellationToken).ConfigureAwait(false);
            if (response == null || string.IsNullOrWhiteSpace(response.Version))
            {
                var error = new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "receive empty TDengine version");
                await InvalidateAsync(error).ConfigureAwait(false);
                throw error;
            }
        }
    }
}
