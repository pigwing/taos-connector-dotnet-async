using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSStmtGetTagFieldsReq
    {
        [JsonPropertyName("req_id")]
        public ulong ReqId { get; set; }

        [JsonPropertyName("stmt_id")]
        public ulong StmtId { get; set; }
    }
}