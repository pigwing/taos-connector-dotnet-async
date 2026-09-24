using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSStmt2InitReq
    {
        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }
        [JsonPropertyName("single_stb_insert")] public bool SingleStbInsert { get; set; }

        [JsonPropertyName("single_table_bind_once")]
        public bool SingleTableBindOnce { get; set; }
    }
}