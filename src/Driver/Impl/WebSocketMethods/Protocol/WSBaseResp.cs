using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public interface IWSBaseResp
    {
        [JsonPropertyName("code")] int Code { get; set; }

        [JsonPropertyName("message")] string Message { get; set; }

        [JsonPropertyName("action")] string Action { get; set; }

        [JsonPropertyName("req_id")] ulong ReqId { get; set; }

        [JsonPropertyName("timing")] long Timing { get; set; }
    }
    
    public class WSBaseResp : IWSBaseResp
    {
        [JsonPropertyName("code")] public int Code { get; set; }

        [JsonPropertyName("message")] public string Message { get; set; }

        [JsonPropertyName("action")] public string Action { get; set; }

        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("timing")] public long Timing { get; set; }
    }
}