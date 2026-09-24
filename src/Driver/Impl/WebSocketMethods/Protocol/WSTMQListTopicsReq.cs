using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSTMQListTopicsReq
    {
        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }
    }
}