using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSStmtGetTagFieldsResp : IWSBaseResp
    {
        [JsonPropertyName("code")] public int Code { get; set; }

        [JsonPropertyName("message")] public string Message { get; set; }

        [JsonPropertyName("action")] public string Action { get; set; }

        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("timing")] public long Timing { get; set; }

        [JsonPropertyName("stmt_id")] public ulong StmtId { get; set; }

        [JsonPropertyName("fields")] public List<StmtField> Fields { get; set; }
    }

    public class StmtField
    {
        [JsonPropertyName("name")] public string Name { get; set; }

        [JsonPropertyName("field_type")] public sbyte FieldType { get; set; }

        [JsonPropertyName("precision")] public byte Precision { get; set; }

        [JsonPropertyName("scale")] public byte Scale { get; set; }

        [JsonPropertyName("bytes")] public int Bytes { get; set; }
    }
}