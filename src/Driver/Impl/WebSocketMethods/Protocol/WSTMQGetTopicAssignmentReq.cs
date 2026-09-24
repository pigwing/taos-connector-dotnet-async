using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSTMQGetTopicAssignmentReq
    {
        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("topic")] public string Topic { get; set; }
    }
}