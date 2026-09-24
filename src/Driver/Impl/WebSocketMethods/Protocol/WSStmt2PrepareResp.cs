using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TDengine.Driver.Impl.WebSocketMethods.Protocol
{
    public class WSStmt2PrepareResp:IWSBaseResp
    {
        [JsonPropertyName("code")] public int Code { get; set; }

        [JsonPropertyName("message")] public string Message { get; set; }

        [JsonPropertyName("action")] public string Action { get; set; }

        [JsonPropertyName("req_id")] public ulong ReqId { get; set; }

        [JsonPropertyName("timing")] public long Timing { get; set; }

        [JsonPropertyName("stmt_id")] public ulong StmtId { get; set; }
        
        [JsonPropertyName("is_insert")] public bool IsInsert { get; set; }
        
        [JsonPropertyName("fields")] public List<Stmt2AllField> Fields { get; set; }
        
        [JsonPropertyName("fields_count")] public int FieldsCount { get; set; }
    }
    
    public class Stmt2AllField
    {
        [JsonPropertyName("name")] public string Name { get; set; }

        [JsonPropertyName("field_type")] public sbyte FieldType { get; set; }

        [JsonPropertyName("precision")] public byte Precision { get; set; }

        [JsonPropertyName("scale")] public byte Scale { get; set; }

        [JsonPropertyName("bytes")] public int Bytes { get; set; }
        
        [JsonPropertyName("bind_type")] public byte BindType { get; set; }
    }
}