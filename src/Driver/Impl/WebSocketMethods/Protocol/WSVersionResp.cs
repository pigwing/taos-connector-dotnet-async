using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSVersionResp : IWSBaseResp
    {
        [JsonPropertyName("code")] public int Code { get; set; }

        [JsonPropertyName("message")] public string Message { get; set; }

        [JsonPropertyName("action")] public string Action { get; set; }

        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("timing")] public long Timing { get; set; }

        [JsonPropertyName("version")] public string Version { get; set; }
    }
}