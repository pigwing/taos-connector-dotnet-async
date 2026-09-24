using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSTMQOffsetSeekReq
    {
        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("topic")] public string Topic { get; set; }

        [JsonPropertyName("vgroup_id")] public int VGroupId { get; set; }

        [JsonPropertyName("offset")] public long Offset { get; set; }
    }
}