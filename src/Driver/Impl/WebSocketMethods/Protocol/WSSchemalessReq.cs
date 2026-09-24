using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSSchemalessReq
    {
        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("protocol")] public int Protocol { get; set; }

        [JsonPropertyName("precision")] public string Precision { get; set; }

        [JsonPropertyName("ttl")] public int TTL { get; set; }

        [JsonPropertyName("data")] public string Data { get; set; }
    }
}