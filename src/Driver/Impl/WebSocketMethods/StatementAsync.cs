using System;
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
            reqId = reqId == 0 ? _GetReqId() : reqId;
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
            if (stmtId == 0) throw new ArgumentOutOfRangeException(nameof(stmtId));
            if (sql == null) throw new ArgumentNullException(nameof(sql));
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
            if (stmtId == 0) throw new ArgumentOutOfRangeException(nameof(stmtId));
            if (req == null) throw new System.ArgumentNullException(nameof(req));
            return await Stmt2BindAsync(stmtId, req, req.Length, cancellationToken).ConfigureAwait(false);
        }

        public async Task<WSStmt2BindResp> Stmt2BindAsync(ulong stmtId, byte[] req, int requestLength,
            CancellationToken cancellationToken = default)
        {
            if (stmtId == 0) throw new ArgumentOutOfRangeException(nameof(stmtId));
            if (req == null) throw new System.ArgumentNullException(nameof(req));
            if (requestLength < 30 || requestLength > req.Length)
            {
                throw new System.ArgumentOutOfRangeException(nameof(requestLength));
            }

            var reqId = _GetReqId();
            WriteUInt64ToBytes(req, reqId, 0);
            WriteUInt64ToBytes(req, stmtId, 8);
            WriteUInt64ToBytes(req, WSActionBinary.Stmt2BindMessage, 16);
            WriteUInt16ToBytes(req, 1, 24);
            WriteUInt32ToBytes(req, 0xffffffff, 26);
            return await SendBinaryBackJsonAsync<WSStmt2BindResp>(req, requestLength, reqId, WSAction.STMT2Bind,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<WSStmt2ExecResp> Stmt2ExecAsync(ulong stmtId,
            CancellationToken cancellationToken = default)
        {
            if (stmtId == 0) throw new ArgumentOutOfRangeException(nameof(stmtId));
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
            if (stmtId == 0) throw new ArgumentOutOfRangeException(nameof(stmtId));
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
            if (stmtId == 0) throw new ArgumentOutOfRangeException(nameof(stmtId));
            var reqId = _GetReqId();
            await SendJsonAsync(WSAction.STMT2Close, new WSStmt2CloseReq
            {
                ReqId = reqId,
                StmtId = stmtId
            }, reqId, cancellationToken).ConfigureAwait(false);
        }
    }
}
