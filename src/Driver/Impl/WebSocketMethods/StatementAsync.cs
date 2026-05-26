using System.Threading;
using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;

namespace TDengine.Driver.Impl.WebSocketMethods
{
    public partial class ConnectionAsync
    {
        public async Task<WSStmt2InitResp> Stmt2InitAsync(ulong reqId,
            CancellationToken cancellationToken = default)
        {
            return await SendJsonBackJsonAsync<WSStmt2InitReq, WSStmt2InitResp>(WSAction.STMT2Init,
                new WSStmt2InitReq
                {
                    ReqId = reqId,
                    SingleStbInsert = true,
                    SingleTableBindOnce = true,
                }, reqId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<WSStmt2PrepareResp> Stmt2PrepareAsync(ulong stmtId, string sql,
            CancellationToken cancellationToken = default)
        {
            var reqId = _GetReqId();
            return await SendJsonBackJsonAsync<WSStmt2PrepareReq, WSStmt2PrepareResp>(WSAction.STMT2Prepare,
                new WSStmt2PrepareReq
                {
                    ReqId = reqId,
                    StmtId = stmtId,
                    SQL = sql,
                    GetFields = true,
                }, reqId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<WSStmt2BindResp> Stmt2BindAsync(ulong stmtId, byte[] req,
            CancellationToken cancellationToken = default)
        {
            var reqId = _GetReqId();
            WriteUInt64ToBytes(req, reqId, 0);
            WriteUInt64ToBytes(req, stmtId, 8);
            WriteUInt64ToBytes(req, WSActionBinary.Stmt2BindMessage, 16);
            WriteUInt16ToBytes(req, 1, 24);
            WriteUInt32ToBytes(req, 0xffffffff, 26);
            return await SendBinaryBackJsonAsync<WSStmt2BindResp>(req, reqId, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<WSStmt2ExecResp> Stmt2ExecAsync(ulong stmtId,
            CancellationToken cancellationToken = default)
        {
            var reqId = _GetReqId();
            return await SendJsonBackJsonAsync<WSStmt2ExecReq, WSStmt2ExecResp>(WSAction.STMT2Exec,
                new WSStmt2ExecReq
                {
                    ReqId = reqId,
                    StmtId = stmtId
                }, reqId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<WSStmt2UseResultResp> Stmt2UseResultAsync(ulong stmtId,
            CancellationToken cancellationToken = default)
        {
            var reqId = _GetReqId();
            return await SendJsonBackJsonAsync<WSStmt2UseResultReq, WSStmt2UseResultResp>(WSAction.STMT2Result,
                new WSStmt2UseResultReq
                {
                    ReqId = reqId,
                    StmtId = stmtId
                }, reqId, cancellationToken).ConfigureAwait(false);
        }

        public async Task Stmt2CloseAsync(ulong stmtId, CancellationToken cancellationToken = default)
        {
            var reqId = _GetReqId();
            await SendJsonAsync(WSAction.STMT2Close, new WSStmt2CloseReq
            {
                ReqId = reqId,
                StmtId = stmtId
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
