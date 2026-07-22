using System.Threading;
using System.Threading.Tasks;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;

namespace TDengine.Driver.Impl.WebSocketMethods
{
    public partial class ConnectionAsync
    {
        public async Task<WSSchemalessResp> SchemalessInsertAsync(string lines, TDengineSchemalessProtocol protocol,
            TDengineSchemalessPrecision precision, int ttl, long reqId,
            CancellationToken cancellationToken = default)
        {
            reqId = ReqId.Normalize(reqId, nameof(reqId));
            var requestId = (ulong)reqId;
            return await SendJsonBackJsonAsync<WSSchemalessReq, WSSchemalessResp>(WSAction.SchemalessWrite,
                new WSSchemalessReq
                {
                    ReqId = requestId,
                    Protocol = (int)protocol,
                    Precision = TDengineConstant.SchemalessPrecisionString(precision),
                    TTL = ttl,
                    Data = lines,
                }, requestId, cancellationToken).ConfigureAwait(false);
        }
    }
}
