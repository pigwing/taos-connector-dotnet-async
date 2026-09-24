using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSConnReq
    {
        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }
        [JsonPropertyName("user")] public string User { get; set; }
        [JsonPropertyName("password")] public string Password { get; set; }
        [JsonPropertyName("db")] public string Db { get; set; }
        [JsonPropertyName("tz")] public string Timezone { get; set; }
        [JsonPropertyName("app")] public string App { get; set; }
        
        // connector
        [JsonPropertyName("connector")] public string Connector { get; set; }
        // bearer_token
        [JsonPropertyName("bearer_token")] public string BearerToken { get; set; }

        [JsonPropertyName("list_instances"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ListInstances { get; set; }
    }
}
