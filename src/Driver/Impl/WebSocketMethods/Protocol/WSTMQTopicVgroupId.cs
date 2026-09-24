using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSTopicVgroupId
    {
        [JsonPropertyName("topic")] public string Topic { get; set; }

        [JsonPropertyName("vgroup_id")] public int VGroupId { get; set; }
    }
}