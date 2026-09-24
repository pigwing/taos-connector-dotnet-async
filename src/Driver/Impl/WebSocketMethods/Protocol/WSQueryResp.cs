using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSQueryResp : IWSBaseResp, IWSMetaResp
    {
        [JsonPropertyName("code")] public int Code { get; set; }

        [JsonPropertyName("message")] public string Message { get; set; }

        [JsonPropertyName("action")] public string Action { get; set; }

        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("timing")] public long Timing { get; set; }

        [JsonPropertyName("id")] public ulong ResultId { get; set; }

        [JsonPropertyName("is_update")] public bool IsUpdate { get; set; }

        [JsonPropertyName("affected_rows")] public int AffectedRows { get; set; }

        [JsonPropertyName("fields_count")] public int FieldsCount { get; set; }

        [JsonPropertyName("fields_names")] public string[] FieldsNames { get; set; }

        [JsonPropertyName("fields_types")] public byte[] FieldsTypes { get; set; }

        [JsonPropertyName("fields_lengths")] public long[] FieldsLengths { get; set; }

        [JsonPropertyName("precision")] public int Precision { get; set; }

        [JsonPropertyName("fields_precisions")] public byte[] FieldsPrecisions { get; set; }

        [JsonPropertyName("fields_scales")] public byte[] FieldsScales { get; set; }
    }
}