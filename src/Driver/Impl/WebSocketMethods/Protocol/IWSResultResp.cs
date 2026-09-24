using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public interface IWSMetaResp
    {
        [JsonPropertyName("fields_count")] int FieldsCount { get; set; }

        [JsonPropertyName("fields_names")] string[] FieldsNames { get; set; }

        [JsonPropertyName("fields_types")] byte[] FieldsTypes { get; set; }

        [JsonPropertyName("fields_lengths")] long[] FieldsLengths { get; set; }

        [JsonPropertyName("precision")] int Precision { get; set; }

        [JsonPropertyName("fields_precisions")] byte[] FieldsPrecisions { get; set; }

        [JsonPropertyName("fields_scales")] byte[] FieldsScales { get; set; }
    }
}