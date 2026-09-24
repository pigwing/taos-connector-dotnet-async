using System.Text.Json.Serialization;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;

namespace TDengine.Driver.Impl.WebSocketMethods
{
    public class WSStmt2ExecResp:IWSBaseResp
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("action")]
        public string Action { get; set; }

        [JsonPropertyName("req_id")]
        public ulong ReqId { get; set; }

        [JsonPropertyName("timing")]
        public long Timing { get; set; }

        [JsonPropertyName("stmt_id")]
        public ulong StmtId { get; set; }

        [JsonPropertyName("affected")]
        public int Affected { get; set; }
    }
}