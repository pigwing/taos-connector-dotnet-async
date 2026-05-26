using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;

namespace TDengine.Driver.Impl.WebSocketMethods
{
    public partial class ConnectionAsync : BaseConnectionAsync
    {
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
                BearerToken = _bearerToken
            }, reqId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<WSQueryResp> BinaryQueryAsync(string sql, ulong reqId = 0,
            CancellationToken cancellationToken = default)
        {
            if (reqId == 0)
            {
                reqId = _GetReqId();
            }

            var sqlByteCount = Encoding.UTF8.GetByteCount(sql);
            var req = new byte[30 + sqlByteCount];
            WriteUInt64ToBytes(req, reqId, 0);
            WriteUInt64ToBytes(req, 0, 8);
            WriteUInt64ToBytes(req, WSActionBinary.BinaryQueryMessage, 16);
            WriteUInt16ToBytes(req, 1, 24);
            WriteUInt32ToBytes(req, (uint)sqlByteCount, 26);
            Encoding.UTF8.GetBytes(sql, 0, sql.Length, req, 30);

            return await SendBinaryBackJsonAsync<WSQueryResp>(req, reqId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<byte[]> FetchRawBlockBinaryAsync(ulong resultId,
            CancellationToken cancellationToken = default)
        {
            var req = new byte[32];
            var reqId = _GetReqId();
            WriteUInt64ToBytes(req, reqId, 0);
            WriteUInt64ToBytes(req, resultId, 8);
            WriteUInt64ToBytes(req, WSActionBinary.FetchRawBlockMessage, 16);
            WriteUInt64ToBytes(req, 1, 24);
            return await SendBinaryBackBytesAsync(req, reqId, cancellationToken).ConfigureAwait(false);
        }

        public async Task FreeResultAsync(ulong resultId, CancellationToken cancellationToken = default)
        {
            var reqId = _GetReqId();
            await SendJsonAsync(WSAction.FreeResult, new WSFreeResultReq
            {
                ReqId = reqId,
                ResultId = resultId
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
