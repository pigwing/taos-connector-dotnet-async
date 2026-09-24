using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSFetchResp : IWSBaseResp
    {
        [JsonPropertyName("code")] public int Code { get; set; }

        [JsonPropertyName("message")] public string Message { get; set; }

        [JsonPropertyName("action")] public string Action { get; set; }

        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("timing")] public long Timing { get; set; }

        [JsonPropertyName("id")] public ulong ResultId { get; set; }

        [JsonPropertyName("completed")] public bool Completed { get; set; }

        [JsonPropertyName("lengths")] public int[] Lengths { get; set; }

        [JsonPropertyName("rows")] public int Rows { get; set; }
    }
}