using System;
using TDengine.Data.Client;
using TDengine.Driver;
using Xunit;

namespace Data.Tests
{
    public class TDengineConnectionStringBuilderTests
    {
        [Fact]
        public void DefaultNative_ShouldSetDefaultValues()
        {
            var builder = new TDengineConnectionStringBuilder("");

            builder.DefaultNative();

            Assert.Equal(6030, builder.Port);
            Assert.Equal("localhost", builder.Host);
            Assert.Equal(TDengineConstant.ProtocolNative, builder.Protocol);
        }

        [Fact]
        public void DefaultWebSocket_ShouldSetDefaultValues()
        {
            var builder = new TDengineConnectionStringBuilder("");

            builder.DefaultWebSocket();

            Assert.Equal(6041, builder.Port);
            Assert.Equal("localhost", builder.Host);
            Assert.Equal(TDengineConstant.ProtocolWebSocket, builder.Protocol);
        }

        [Fact]
        public void Parse()
        {
            var builder =
                new TDengineConnectionStringBuilder(
                    "host=127.0.0.1;port=6030;username=root;password=taosdata;protocol=Native;db=test");
            Assert.Equal("127.0.0.1", builder.Host);
            Assert.Equal(6030, builder.Port);
            Assert.Equal("root", builder.Username);
            Assert.Equal("taosdata", builder.Password);
            Assert.Equal("test", builder.Database);
            Assert.Equal(TDengineConstant.ProtocolNative, builder.Protocol);
            builder.Clear();
            Assert.Equal(string.Empty, builder.Host);
            Assert.Equal(0, builder.Port);
            Assert.Equal(string.Empty, builder.Username);
            Assert.Equal(string.Empty, builder.Password);
            Assert.Equal(string.Empty, builder.Database);
            Assert.Equal(TDengineConstant.ProtocolNative, builder.Protocol);
            builder.Database = "test2";
            Assert.Equal("test2", builder.Database);
            builder.Remove("db");
            Assert.Equal(string.Empty, builder.Database);
        }

        [Fact]
        public void ParseWebSocket()
        {
            var builder = new TDengineConnectionStringBuilder(
                "host=127.0.0.1;" +
                "port=6041;" +
                "username=root;" +
                "password=taosdata;" +
                "protocol=WebSocket;" +
                "db=test;" +
                "enableCompression=true;" +
                "connTimeout=00:00:10;" +
                "readTimeout=00:00:20;" +
                "writeTimeout=00:00:30;" +
                "timezone=UTC;" +
                "useSSL=true;" +
                "token=123456;" +
                "autoReconnect=true;" +
                "reconnectIntervalMs=10;" +
                "reconnectRetryCount=5");
            Assert.Equal("127.0.0.1", builder.Host);
            Assert.Equal(6041, builder.Port);
            Assert.Equal("root", builder.Username);
            Assert.Equal("taosdata", builder.Password);
            Assert.Equal("test", builder.Database);
            Assert.Equal(TDengineConstant.ProtocolWebSocket, builder.Protocol);
            Assert.True(builder.EnableCompression);
            Assert.Equal(10, builder.ConnTimeout.TotalSeconds);
            Assert.Equal(20, builder.ReadTimeout.TotalSeconds);
            Assert.Equal(30, builder.WriteTimeout.TotalSeconds);
            Assert.Equal("UTC", builder.Timezone.Id);
            Assert.True(builder.UseSSL);
            Assert.Equal("123456", builder.Token);
            Assert.True(builder.AutoReconnect);
            Assert.Equal(10, builder.ReconnectIntervalMs);
            Assert.Equal(5, builder.ReconnectRetryCount);
        }

        [Fact]
        public void ParseWebSocketPoolingOptions()
        {
            var builder = new TDengineConnectionStringBuilder(
                "protocol=WebSocket;" +
                "host=127.0.0.1;" +
                "pooling=true;" +
                "min pool size=2;" +
                "maximumPoolSize=8;" +
                "connectionTimeout=1500ms;" +
                "keepaliveTime=45s;" +
                "maxLifetime=30m;" +
                "housekeepingInterval=5s;" +
                "creationRetryBackoff=25ms;" +
                "maxCreationRetryBackoff=1s;" +
                "leakDetectionThreshold=10s");

            Assert.True(builder.Pooling);
            Assert.Equal(2, builder.MinPoolSize);
            Assert.Equal(8, builder.MaxPoolSize);
            Assert.Equal(TimeSpan.FromMilliseconds(1500), builder.PoolConnectionTimeout);
            Assert.Equal(TimeSpan.FromSeconds(45), builder.PoolKeepaliveTime);
            Assert.Equal(TimeSpan.FromMinutes(30), builder.PoolMaxLifetime);
            Assert.Equal(TimeSpan.FromSeconds(5), builder.PoolHousekeepingInterval);
            Assert.Equal(TimeSpan.FromMilliseconds(25), builder.PoolCreationRetryBackoff);
            Assert.Equal(TimeSpan.FromSeconds(1), builder.PoolMaxCreationRetryBackoff);
            Assert.Equal(TimeSpan.FromSeconds(10), builder.PoolLeakDetectionThreshold);
            Assert.True(builder.TryGetValue("maxPoolSize", out var maxPoolSize));
            Assert.Equal(8, maxPoolSize);
        }

        [Fact]
        public void PoolingOptions_ResetWithRemoveAndClear()
        {
            var builder = new TDengineConnectionStringBuilder(
                "protocol=WebSocket;host=127.0.0.1;pooling=true;minPoolSize=1;maxPoolSize=3");

            Assert.True(builder.Remove("pooling"));
            Assert.False(builder.Pooling);
            Assert.True(builder.Remove("max pool size"));
            Assert.Equal(10, builder.MaxPoolSize);

            builder.Clear();

            Assert.False(builder.Pooling);
            Assert.Equal(0, builder.MinPoolSize);
            Assert.Equal(10, builder.MaxPoolSize);
            Assert.Equal(TimeSpan.FromSeconds(30), builder.PoolConnectionTimeout);
            Assert.Equal(TimeSpan.FromMinutes(2), builder.PoolKeepaliveTime);
            Assert.Equal(TimeSpan.FromMinutes(30), builder.PoolMaxLifetime);
        }

        [Fact]
        public void ParseWebSocket_Official321Options()
        {
            var builder = new TDengineConnectionStringBuilder(
                "host=192.168.1.18:6341,192.168.1.19:6341;" +
                "username=root;" +
                "password=taosdata;" +
                "protocol=WebSocket;" +
                "bearerToken=token-value;" +
                "connectionTimezone=UTC");

            Assert.Equal("192.168.1.18:6341,192.168.1.19:6341", builder.Host);
            Assert.Equal("token-value", builder.BearerToken);
            Assert.Equal("UTC", builder.ConnectionTimezone.Id);
        }

        [Fact]
        public void ParseWebSocket_RejectsBothTimezoneOptions()
        {
            var ex = Assert.Throws<ArgumentException>(() => new TDengineConnectionStringBuilder(
                "protocol=WebSocket;host=192.168.1.18;timezone=UTC;connectionTimezone=UTC"));

            Assert.Contains("connectionTimezone and timezone", ex.Message);
        }
    }
}
