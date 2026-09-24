using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSTMQPollResp : IWSBaseResp
    {
        [JsonPropertyName("code")] public int Code { get; set; }

        [JsonPropertyName("message")] public string Message { get; set; }

        [JsonPropertyName("action")] public string Action { get; set; }

        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("timing")] public long Timing { get; set; }

        [JsonPropertyName("have_message")] public bool HaveMessage { get; set; }

        [JsonPropertyName("topic")] public string Topic { get; set; }

        [JsonPropertyName("database")] public string Database { get; set; }

        [JsonPropertyName("vgroup_id")] public int VgroupId { get; set; }

        [JsonPropertyName("message_type")] public int MessageType { get; set; }

        [JsonPropertyName("message_id")] public ulong MessageId { get; set; }

        [JsonPropertyName("offset")] public long Offset { get; set; }
    }
}