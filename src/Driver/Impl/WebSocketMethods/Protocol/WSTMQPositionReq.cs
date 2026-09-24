using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSTMQPositionReq
    {
        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("topic_vgroup_ids")] public List<WSTopicVgroupId> TopicVgroupIds { get; set; }
    }


}