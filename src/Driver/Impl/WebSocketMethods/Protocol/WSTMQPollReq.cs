using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSTMQPollReq
    {
        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("blocking_time")] public long BlockingTime { get; set; }

        [JsonPropertyName("message_id")] public ulong MessageId { get; set; }
    }
}