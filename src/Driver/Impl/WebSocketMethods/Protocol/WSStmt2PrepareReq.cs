using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSStmt2PrepareReq
    {
        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("stmt_id")] public ulong StmtId { get; set; }

        [JsonPropertyName("sql")] public string SQL { get; set; }

        [JsonPropertyName("get_fields")] public bool GetFields { get; set; }
    }
}