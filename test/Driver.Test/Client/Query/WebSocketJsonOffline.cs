using System;
using TDengine.Driver.Impl.WebSocketMethods;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;
using Xunit;

namespace Driver.Test.Client.Query
{
    public sealed class WebSocketJsonOffline
    {
        [Fact]
        public void SerializeUsesProtocolNamesAndOmitsNullOptionalProperties()
        {
            var json = WsJson.Serialize(new WSConnReq
            {
                ReqId = ulong.MaxValue,
                User = "root",
                Password = "secret",
                Db = "db",
                ListInstances = null
            });

            Assert.Contains("\"req_id\":18446744073709551615", json, StringComparison.Ordinal);
            Assert.Contains("\"password\":\"secret\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("list_instances", json, StringComparison.Ordinal);
        }

        [Fact]
        public void DeserializeAcceptsNumericMetadataArrays()
        {
            var response = WsJson.Deserialize<WSQueryResp>(
                "{\"code\":0,\"message\":\"\",\"action\":\"query\",\"req_id\":7," +
                "\"fields_types\":[1,2,255],\"fields_precisions\":[0,3],\"fields_scales\":[0,1]}");

            Assert.Equal(new byte[] { 1, 2, 255 }, response.FieldsTypes);
            Assert.Equal(new byte[] { 0, 3 }, response.FieldsPrecisions);
            Assert.Equal(new byte[] { 0, 1 }, response.FieldsScales);
        }

        [Fact]
        public void DeserializeAcceptsBase64MetadataArrays()
        {
            var response = WsJson.Deserialize<WSQueryResp>(
                "{\"fields_types\":\"AQL/\",\"fields_precisions\":\"AAI=\",\"fields_scales\":\"/w==\"}");

            Assert.Equal(new byte[] { 1, 2, 255 }, response.FieldsTypes);
            Assert.Equal(new byte[] { 0, 2 }, response.FieldsPrecisions);
            Assert.Equal(new byte[] { 255 }, response.FieldsScales);
        }
    }
}
