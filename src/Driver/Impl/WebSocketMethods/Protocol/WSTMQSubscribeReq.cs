using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSTMQSubscribeReq
    {
        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("user")] public string User { get; set; }

        [JsonPropertyName("password")] public string Password { get; set; }

        [JsonPropertyName("db")] public string Db { get; set; }

        [JsonPropertyName("group_id")] public string GroupId { get; set; }

        [JsonPropertyName("client_id")] public string ClientId { get; set; }

        [JsonPropertyName("offset_rest")] public string OffsetRest { get; set; }

        [JsonPropertyName("topics")] public List<string> Topics { get; set; }

        [JsonPropertyName("auto_commit")] public string AutoCommit { get; set; }

        [JsonPropertyName("auto_commit_interval_ms")]
        public string AutoCommitIntervalMs { get; set; }

        [JsonPropertyName("snapshot_enable")] public string SnapshotEnable { get; set; }

        [JsonPropertyName("with_table_name")] public string WithTableName { get; set; }

        //session_timeout_ms
        [JsonPropertyName("session_timeout_ms")] public string SessionTimeoutMs { get; set; }

        //max_poll_interval_ms
        [JsonPropertyName("max_poll_interval_ms")] public string MaxPollIntervalMs { get; set; }

        // other config
        [JsonPropertyName("config")] public Dictionary<string, string> Config { get; set; }

        [JsonPropertyName("list_instances"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? ListInstances { get; set; }
    }
}
