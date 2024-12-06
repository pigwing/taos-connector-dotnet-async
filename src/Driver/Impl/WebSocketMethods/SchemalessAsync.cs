using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;

namespace TDengine.Driver.Impl.WebSocketMethods
{
    public partial class ConnectionAsync
    {
        public async Task<WSSchemalessResp> SchemalessInsertAsync(string lines, TDengineSchemalessProtocol protocol,
            TDengineSchemalessPrecision precision,
            int ttl, long reqId)
        {
            return await SendJsonBackJsonAsync<WSSchemalessReq, WSSchemalessResp>(WSAction.SchemalessWrite, new WSSchemalessReq
            {
                ReqId = (ulong)reqId,
                Protocol = (int)protocol,
                Precision = TDengineConstant.SchemalessPrecisionString(precision),
                TTL = ttl,
                Data = lines,
            });
        }
    }
}