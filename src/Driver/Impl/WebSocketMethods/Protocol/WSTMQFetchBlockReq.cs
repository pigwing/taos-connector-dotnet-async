using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSTMQFetchBlockReq
    {
        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("message_id")] public ulong MessageId { get; set; }
    }
}