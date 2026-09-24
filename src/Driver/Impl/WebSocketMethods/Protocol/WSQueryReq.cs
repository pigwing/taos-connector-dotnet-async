using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSQueryReq
    {
        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("sql")] public string Sql { get; set; }
    }
}