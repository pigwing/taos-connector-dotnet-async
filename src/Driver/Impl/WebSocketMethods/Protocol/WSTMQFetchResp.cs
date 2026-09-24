using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSTMQFetchResp : IWSBaseResp
    {
        [JsonPropertyName("code")] public int Code { get; set; }

        [JsonPropertyName("message")] public string Message { get; set; }

        [JsonPropertyName("action")] public string Action { get; set; }

        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("timing")] public long Timing { get; set; }

        [JsonPropertyName("message_id")] public ulong MessageId { get; set; }

        [JsonPropertyName("completed")] public bool Completed { get; set; }

        [JsonPropertyName("table_name")] public string TableName { get; set; }

        [JsonPropertyName("rows")] public int Rows { get; set; }

        [JsonPropertyName("fields_count")] public int FieldsCount { get; set; }

        [JsonPropertyName("fields_names")] public List<string> FieldsNames { get; set; }

        [JsonPropertyName("fields_types")] public List<byte> FieldsTypes { get; set; }

        [JsonPropertyName("fields_lengths")] public List<long> FieldsLengths { get; set; }

        [JsonPropertyName("precision")] public int Precision { get; set; }
    }
}