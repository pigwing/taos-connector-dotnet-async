using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSStmtSetTableNameReq
    {
        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("stmt_id")] public ulong StmtId { get; set; }

        [JsonPropertyName("name")] public string Name { get; set; }
    }
}