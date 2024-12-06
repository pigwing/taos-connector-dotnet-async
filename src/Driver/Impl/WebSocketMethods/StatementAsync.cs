using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;

namespace TDengine.Driver.Impl.WebSocketMethods
{
    public partial class ConnectionAsync
    {

        public async Task<WSStmtInitResp> StmtInitAsync(ulong reqId)
        {
            return await SendJsonBackJsonAsync<WSStmtInitReq, WSStmtInitResp>(WSAction.STMTInit, new WSStmtInitReq
            {
                ReqId = reqId,
            });
        }

        public async Task<WSStmtPrepareResp> StmtPrepareAsync(ulong stmtId, string sql)
        {
            return await SendJsonBackJsonAsync<WSStmtPrepareReq, WSStmtPrepareResp>(WSAction.STMTPrepare,
                new WSStmtPrepareReq
                {
                    ReqId = _GetReqId(),
                    StmtId = stmtId,
                    SQL = sql
                });
        }

        public async Task<WSStmtSetTableNameResp> StmtSetTableNameAsync(ulong stmtId, string tablename)
        {
            return await SendJsonBackJsonAsync<WSStmtSetTableNameReq, WSStmtSetTableNameResp>(WSAction.STMTSetTableName,
                new WSStmtSetTableNameReq
                {
                    ReqId = _GetReqId(),
                    StmtId = stmtId,
                    Name = tablename,
                });
        }

        public async Task<WSStmtSetTagsResp> StmtSetTagsAsync(ulong stmtId, TaosFieldE[] fields, object[] tags)
        {
            //p0 uin64  req_id
            //p0+8 uint64  stmt_id
            //p0+16 uint64 (1 (set tag) 2 (bind))
            //p0+24 raw block
            Array[] param = new Array[tags.Length];
            for (int i = 0; i < tags.Length; i++)
            {
                if (tags[i] == null)
                {
                    var a = new object[1] { 123 };
                    Array newArray = Array.CreateInstance(TDengineConstant.ScanNullableType(fields[i].type), 1);
                    newArray.SetValue(null, 0);
                    param[i] = newArray;
                }
                else
                {
                    Array newArray = Array.CreateInstance(tags[i].GetType(), 1);
                    newArray.SetValue(tags[i], 0);
                    param[i] = newArray;
                }
            }

            var bytes = BlockWriter.Serialize(1, fields, param);
            var req = new byte[24 + bytes.Length];
            WriteUInt64ToBytes(req, _GetReqId(), 0);
            WriteUInt64ToBytes(req, stmtId, 8);
            WriteUInt64ToBytes(req, WSActionBinary.SetTagsMessage, 16);
            Buffer.BlockCopy(bytes, 0, req, 24, bytes.Length);
            return await SendBinaryBackJsonAsync<WSStmtSetTagsResp>(req);
        }

        public async Task<WSStmtBindResp> StmtBindAsync(ulong stmtId, TaosFieldE[] fields, object[] row)
        {
            //p0 uin64  req_id
            //p0+8 uint64  stmt_id
            //p0+16 uint64 (1 (set tag) 2 (bind))
            //p0+24 raw block
            Array[] param = new Array[row.Length];
            for (int i = 0; i < row.Length; i++)
            {
                if (row[i] == null)
                {
                    Array newArray = Array.CreateInstance(TDengineConstant.ScanNullableType(fields[i].type), 1);
                    newArray.SetValue(null, 0);
                    param[i] = newArray;
                }
                else
                {
                    Array newArray = Array.CreateInstance(row[i].GetType(), 1);
                    newArray.SetValue(row[i], 0);
                    param[i] = newArray;
                }
            }

            var bytes = BlockWriter.Serialize(1, fields, param);
            var req = new byte[24 + bytes.Length];
            WriteUInt64ToBytes(req, _GetReqId(), 0);
            WriteUInt64ToBytes(req, stmtId, 8);
            WriteUInt64ToBytes(req, WSActionBinary.BindMessage, 16);
            Buffer.BlockCopy(bytes, 0, req, 24, bytes.Length);
            return await SendBinaryBackJsonAsync<WSStmtBindResp>(req);
        }

        public async Task<WSStmtBindResp> StmtBindAsync(ulong stmtId, TaosFieldE[] fields, params Array[] param)
        {
            //p0 uin64  req_id
            //p0+8 uint64  stmt_id
            //p0+16 uint64 (1 (set tag) 2 (bind))
            //p0+24 raw block

            var bytes = BlockWriter.Serialize(param[0].Length, fields, param);
            var req = new byte[24 + bytes.Length];
            WriteUInt64ToBytes(req, _GetReqId(), 0);
            WriteUInt64ToBytes(req, stmtId, 8);
            WriteUInt64ToBytes(req, WSActionBinary.BindMessage, 16);
            Buffer.BlockCopy(bytes, 0, req, 24, bytes.Length);
            return await SendBinaryBackJsonAsync<WSStmtBindResp>(req);
        }

        public async Task<WSStmtAddBatchResp> StmtAddBatchAsync(ulong stmtId)
        {
            return await SendJsonBackJsonAsync<WSStmtAddBatchReq, WSStmtAddBatchResp>(WSAction.STMTAddBatch, new WSStmtAddBatchReq
            {
                ReqId = _GetReqId(),
                StmtId = stmtId
            });
        }

        public async Task<WSStmtExecResp> StmtExecAsync(ulong stmtId)
        {
            return await SendJsonBackJsonAsync<WSStmtExecReq, WSStmtExecResp>(WSAction.STMTExec, new WSStmtExecReq
            {
                ReqId = _GetReqId(),
                StmtId = stmtId
            });
        }

        public async  Task<WSStmtGetColFieldsResp> StmtGetColFieldsAsync(ulong stmtId)
        {
            return await SendJsonBackJsonAsync<WSStmtGetColFieldsReq, WSStmtGetColFieldsResp>(WSAction.STMTGetColFields,
                new WSStmtGetColFieldsReq
                {
                    ReqId = _GetReqId(),
                    StmtId = stmtId
                });
        }

        public async Task<WSStmtGetTagFieldsResp> StmtGetTagFieldsAsync(ulong stmtId)
        {
            return await SendJsonBackJsonAsync<WSStmtGetTagFieldsReq, WSStmtGetTagFieldsResp>(WSAction.STMTGetTagFields,
                new WSStmtGetTagFieldsReq
                {
                    ReqId = _GetReqId(),
                    StmtId = stmtId
                });
        }

        public async Task<WSStmtUseResultResp> StmtUseResultAsync(ulong stmtId)
        {
            return await SendJsonBackJsonAsync<WSStmtUseResultReq, WSStmtUseResultResp>(WSAction.STMTUseResult,
                new WSStmtUseResultReq
                {
                    ReqId = _GetReqId(),
                    StmtId = stmtId
                });
        }

        public async Task StmtCloseAsync(ulong stmtId)
        {
            await SendJsonAsync(WSAction.STMTClose, new WSStmtCloseReq
            {
                ReqId = _GetReqId(),
                StmtId = stmtId
            });
        }
    }
}
