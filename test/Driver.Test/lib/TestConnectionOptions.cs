using System;
using System.Collections.Generic;
using TDengine.Driver;

namespace Test.Fixture
{
    internal static class TestConnectionOptions
    {
        public static string Host { get; } = GetEnvironmentValue("TEST_HOST", "192.168.1.18");

        public static int NativePort { get; } = GetEnvironmentInt32("TEST_NATIVE_PORT", 6030);

        public static int WebSocketPort { get; } = GetEnvironmentInt32("TEST_WS_PORT", 6341);

        public static string Username { get; } = GetEnvironmentValue("TEST_USER", "root");

        public static string Password { get; } = GetEnvironmentValue("TEST_PASSWORD", "taosdata");

        public static string NativeConnectionString(string? database = null)
        {
            return AppendDatabase(
                $"host={Host};port={NativePort};username={Username};password={Password};protocol={TDengineConstant.ProtocolNative}",
                database);
        }

        public static string WebSocketConnectionString(string? database = null)
        {
            return AppendDatabase(
                $"protocol={TDengineConstant.ProtocolWebSocket};host={Host};port={WebSocketPort};useSSL=false;username={Username};password={Password};enableCompression=true",
                database);
        }

        public static Dictionary<string, string> NativeTmqConfig(bool autoCommit)
        {
            var config = BaseTmqConfig(autoCommit);
            config["td.connect.ip"] = Host;
            config["td.connect.user"] = Username;
            config["td.connect.pass"] = Password;
            config["td.connect.port"] = NativePort.ToString();
            return config;
        }

        public static Dictionary<string, string> WebSocketTmqConfig(bool autoCommit)
        {
            var config = BaseTmqConfig(autoCommit);
            config["td.connect.type"] = TDengineConstant.ProtocolWebSocket;
            config["td.connect.ip"] = Host;
            config["td.connect.user"] = Username;
            config["td.connect.pass"] = Password;
            config["td.connect.port"] = WebSocketPort.ToString();
            config["useSSL"] = "false";
            config["ws.message.enableCompression"] = "true";
            return config;
        }

        private static Dictionary<string, string> BaseTmqConfig(bool autoCommit)
        {
            var config = new Dictionary<string, string>
            {
                { "group.id", "test" },
                { "auto.offset.reset", "earliest" },
                { "client.id", "test_tmq_c" },
                { "enable.auto.commit", autoCommit ? "true" : "false" },
                { "msg.with.table.name", "true" },
                { "session.timeout.ms", "12000" },
                { "max.poll.interval.ms", "300000" }
            };

            if (autoCommit)
            {
                config["auto.commit.interval.ms"] = "100";
            }

            return config;
        }

        private static string AppendDatabase(string connectionString, string? database)
        {
            return string.IsNullOrWhiteSpace(database)
                ? connectionString
                : $"{connectionString};db={database}";
        }

        private static string GetEnvironmentValue(string name, string defaultValue)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
        }

        private static int GetEnvironmentInt32(string name, int defaultValue)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return int.TryParse(value, out var parsed) ? parsed : defaultValue;
        }
    }
}
