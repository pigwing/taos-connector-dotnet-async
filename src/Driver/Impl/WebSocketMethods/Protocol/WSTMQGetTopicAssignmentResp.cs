using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSTMQGetTopicAssignmentResp:IWSBaseResp
    {
        [JsonPropertyName("code")] public int Code { get; set; }

        [JsonPropertyName("message")] public string Message { get; set; }

        [JsonPropertyName("action")] public string Action { get; set; }

        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("timing")] public long Timing { get; set; }

        [JsonPropertyName("assignment")] public List<WSTMQAssignment> Assignment { get; set; }
    }

    public class WSTMQAssignment
    {
        [JsonPropertyName("vgroup_id")] public int VGroupId { get; set; }

        [JsonPropertyName("offset")] public long Offset { get; set; }

        [JsonPropertyName("begin")] public long Begin { get; set; }

        [JsonPropertyName("end")] public long End { get; set; }
    }
}