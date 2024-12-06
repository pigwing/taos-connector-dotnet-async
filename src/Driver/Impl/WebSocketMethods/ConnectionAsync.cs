using System;
using System.Text;
using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;

namespace TDengine.Driver.Impl.WebSocketMethods
{

    public partial class ConnectionAsync : BaseConnectionAsync
    {
        private readonly string _user;

        private readonly string _password;

        private readonly string _db;

        public ConnectionAsync(string addr, string user, string password, string db,
            TimeSpan connectTimeout = default(TimeSpan), TimeSpan readTimeout = default(TimeSpan),
            TimeSpan writeTimeout = default(TimeSpan), bool enableCompression = false)
            : base(addr, connectTimeout, readTimeout, writeTimeout, enableCompression)
        {
            _user = user;
            _password = password;
            _db = db;
        }

        public async Task<WSConnResp> ConnectAsync()
        {
            await ClientConnectAsync();
            var resp = await SendJsonBackJsonAsync<WSConnReq, WSConnResp>(WSAction.Conn, new WSConnReq
            {
                ReqId = _GetReqId(),
                User = _user,
                Password = _password,
                Db = _db
            });
            return resp;
        }

        public async Task<WSQueryResp> BinaryQueryAsync(string sql, ulong reqid = 0uL)
        {
            if (reqid == default)
            {
                reqid = _GetReqId();
            }

            //p0 uin64  req_id
            //p0+8 uint64  message_id
            //p0+16 uint64 action
            //p0+24 uint16 version
            //p0+26 uint32 sql_len
            //p0+30 raw sql
            var req = new byte[30 + sql.Length];
            WriteUInt64ToBytes(req, reqid, 0);
            WriteUInt64ToBytes(req, 0, 8);
            WriteUInt64ToBytes(req, WSActionBinary.BinaryQueryMessage, 16);
            WriteUInt16ToBytes(req, 1, 24);
            WriteUInt32ToBytes(req, (uint)sql.Length, 26);
            Buffer.BlockCopy(Encoding.UTF8.GetBytes(sql), 0, req, 30, sql.Length);

            return await SendBinaryBackJsonAsync<WSQueryResp>(req);
        }

        public async Task<byte[]> FetchRawBlockBinaryAsync(ulong resultId)
        {
            //p0 uin64  req_id
            //p0+8 uint64  message_id
            //p0+16 uint64 action
            //p0+24 uint16 version
            var req = new byte[32];
            WriteUInt64ToBytes(req, _GetReqId(), 0);
            WriteUInt64ToBytes(req, resultId, 8);
            WriteUInt64ToBytes(req, WSActionBinary.FetchRawBlockMessage, 16);
            WriteUInt64ToBytes(req, 1, 24);
            return await SendBinaryBackBytesAsync(req);
        }

        public async Task FreeResultAsync(ulong resultId)
        {
            await SendJsonAsync(WSAction.FreeResult, new WSFreeResultReq
            {
                ReqId = _GetReqId(),
                ResultId = resultId
            });
        }
    }
}
