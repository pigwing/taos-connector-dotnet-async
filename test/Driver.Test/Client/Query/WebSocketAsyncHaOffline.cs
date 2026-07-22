using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TDengine.Driver;
using TDengine.Driver.Client.Websocket;
using TDengine.Driver.Impl.WebSocketMethods;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;
using TDengine.TMQ;
using Test.Fixture;
using Xunit;

namespace Driver.Test.Client.Query
{
    [Collection("WebSocket async collection")]
    public sealed class WebSocketAsyncHaOffline
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ConnectionRequestsInstancesOnlyWhenAdapterHaIsEnabled(bool adapterHa)
        {
            bool? observedListInstances = null;
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                var connect = await ReceiveConnectRequestAsync(socket, token).ConfigureAwait(false);
                observedListInstances = connect["args"]?["list_instances"]?.Value<bool>();
                await SendConnectResponseAsync(socket, connect, null, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var client = new WSClientAsync(CreateBuilder(server.Port, adapterHa));
            await client.ConnectAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);

            Assert.Equal(adapterHa ? true : (bool?)null, observedListInstances);
        }

        [Fact]
        public async Task DiscoveredAddressIsSharedAndUsedWhenOriginalSeedIsUnavailable()
        {
            AdapterClusterRegistry.Clear();
            await using var discoveredServer = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                var connect = await ReceiveConnectRequestAsync(socket, token).ConfigureAwait(false);
                Assert.True(connect["args"]?["list_instances"]?.Value<bool>());
                await SendConnectResponseAsync(socket, connect, null, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var seedServer = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                var connect = await ReceiveConnectRequestAsync(socket, token).ConfigureAwait(false);
                await SendConnectResponseAsync(socket, connect,
                        new[] { $"127.0.0.1:{discoveredServer.Port}" }, token)
                    .ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });
            var seedServerDisposed = false;

            try
            {
                var builder = CreateBuilder(seedServer.Port, true);
                var discoveryClient = new WSClientAsync(builder);
                await discoveryClient.ConnectAsync().ConfigureAwait(false);
                await discoveryClient.DisposeAsync().ConfigureAwait(false);
                Assert.True(AdapterClusterRegistry.Count >= 2);

                await seedServer.DisposeAsync().ConfigureAwait(false);
                seedServerDisposed = true;

                var failoverClient = new WSClientAsync(builder);
                await failoverClient.ConnectAsync().ConfigureAwait(false);
                await failoverClient.DisposeAsync().ConfigureAwait(false);

                await discoveredServer.WaitForAcceptedConnectionsAsync(1, TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
                Assert.Equal(1, discoveredServer.AcceptedConnections);
            }
            finally
            {
                if (!seedServerDisposed)
                {
                    await seedServer.DisposeAsync().ConfigureAwait(false);
                }

                AdapterClusterRegistry.Clear();
            }
        }

        [Fact]
        public void DiscoveredInstancesAreValidatedDeduplicatedAndBounded()
        {
            var parsed = AdapterHAHelper.ParseInstances(new[]
            {
                null!,
                string.Empty,
                "host:0",
                "HOST:6041",
                "host:6041",
                "[::1]:6041",
                "host:70000"
            }, TDengineConstant.ProtocolWebSocket, false);

            Assert.NotNull(parsed);
            Assert.Equal(2, parsed!.Count);

            var beyondExaminationLimit = new string[AdapterHAHelper.MaximumExaminedInstances + 1];
            beyondExaminationLimit[beyondExaminationLimit.Length - 1] = "ignored:6041";
            Assert.Null(AdapterHAHelper.ParseInstances(beyondExaminationLimit,
                TDengineConstant.ProtocolWebSocket, false));

            var oversizedCluster = new string[AdapterClusterRegistry.MaximumClusterAddresses + 100];
            for (var i = 0; i < oversizedCluster.Length; i++)
            {
                oversizedCluster[i] = $"adapter-{i}:6041";
            }

            var bounded = AdapterHAHelper.ParseInstances(oversizedCluster,
                TDengineConstant.ProtocolWebSocket, false);
            Assert.NotNull(bounded);
            Assert.Equal(AdapterClusterRegistry.MaximumClusterAddresses, bounded!.Count);
        }

        [Fact]
        public async Task TmqSubscribeRequestsAndReturnsAdapterInstances()
        {
            const string discovered = "adapter-2:6041";
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                var version = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendVersionResponseAsync(socket, version, token).ConfigureAwait(false);

                var subscribe = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                Assert.Equal(WSTMQAction.TMQSubscribe, WebSocketTestProtocol.GetAction(subscribe));
                Assert.True(subscribe["args"]?["list_instances"]?.Value<bool>());
                await WebSocketTestProtocol.SendResponseAsync(socket, WSTMQAction.TMQSubscribe,
                    WebSocketTestProtocol.GetRequestId(subscribe),
                    new JObject { ["list_instances"] = new JArray(discovered) }, false, token)
                    .ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var options = CreateTmqOptions(server.Port);
            var connection = new TMQConnectionAsync(options, TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
            await connection.ConnectAsync().ConfigureAwait(false);
            var response = await connection.SubscribeAsync(new List<string> { "topic" }, options, true,
                    CancellationToken.None)
                .ConfigureAwait(false);
            await connection.CloseAsync().ConfigureAwait(false);

            Assert.Equal(new[] { discovered }, response.ListInstances);
        }

        [Fact]
        public async Task TmqConsumerSubscribeSnapshotsArbitraryEnumerable()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                var version = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendVersionResponseAsync(socket, version, token).ConfigureAwait(false);

                var subscribe = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                Assert.Equal(WSTMQAction.TMQSubscribe, WebSocketTestProtocol.GetAction(subscribe));
                Assert.Equal("topic-a", subscribe["args"]?["topics"]?[0]?.Value<string>());
                Assert.Equal("topic-b", subscribe["args"]?["topics"]?[1]?.Value<string>());
                await WebSocketTestProtocol.SendResponseAsync(socket, WSTMQAction.TMQSubscribe,
                    WebSocketTestProtocol.GetRequestId(subscribe), new JObject(), false, token)
                    .ConfigureAwait(false);
            });

            var config = TestConnectionOptions.WebSocketTmqConfig(false);
            config["td.connect.ip"] = "127.0.0.1";
            config["td.connect.port"] = server.Port.ToString(CultureInfo.InvariantCulture);
            IConsumer<Dictionary<string, object>>? consumer = null;
            try
            {
                consumer = new ConsumerBuilder<Dictionary<string, object>>(config).Build();
                Assert.Throws<ArgumentNullException>(() =>
                    consumer.Subscribe((IEnumerable<string>)null!));
                Assert.Throws<ArgumentNullException>(() => consumer.Subscribe((string)null!));
                var topics = new[] { "topic-a", "topic-b" };
                consumer.Subscribe(topics);
                topics[0] = "mutated";

                var topicsField = consumer.GetType().GetField("_topics",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(topicsField);
                var snapshot = Assert.IsType<List<string>>(topicsField!.GetValue(consumer));
                Assert.Equal(new[] { "topic-a", "topic-b" }, snapshot);
            }
            finally
            {
                consumer?.Close();
            }
        }

        private static ConnectionStringBuilder CreateBuilder(int port, bool adapterHa)
        {
            return new ConnectionStringBuilder(
                $"protocol=WebSocket;host=127.0.0.1;port={port};username=root;password=taosdata;" +
                $"adapterHA={adapterHa.ToString().ToLowerInvariant()};connTimeout=00:00:01;" +
                "readTimeout=00:00:03;writeTimeout=00:00:02");
        }

        private static TMQOptions CreateTmqOptions(int port)
        {
            return new TMQOptions(new Dictionary<string, string>
            {
                ["td.connect.ip"] = "127.0.0.1",
                ["td.connect.port"] = port.ToString(CultureInfo.InvariantCulture),
                ["td.connect.user"] = "root",
                ["td.connect.pass"] = "taosdata",
                ["group.id"] = "ha-offline-group",
                ["client.id"] = "ha-offline-client",
                ["auto.offset.reset"] = "earliest",
                ["useSSL"] = "false"
            });
        }

        private static async Task<JObject> ReceiveConnectRequestAsync(WebSocket socket,
            CancellationToken cancellationToken)
        {
            var version = await WebSocketTestProtocol.ReceiveJsonAsync(socket, cancellationToken)
                .ConfigureAwait(false);
            await WebSocketTestProtocol.SendVersionResponseAsync(socket, version, cancellationToken)
                .ConfigureAwait(false);

            var connect = await WebSocketTestProtocol.ReceiveJsonAsync(socket, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(WebSocketTestProtocol.GetAction(connect), WSAction.Conn,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Expected the WebSocket connection request.");
            }

            return connect;
        }

        private static Task SendConnectResponseAsync(WebSocket socket, JObject request, string[]? instances,
            CancellationToken cancellationToken)
        {
            var properties = new JObject();
            if (instances != null)
            {
                properties["list_instances"] = new JArray(instances);
            }

            return WebSocketTestProtocol.SendResponseAsync(socket, WSAction.Conn,
                WebSocketTestProtocol.GetRequestId(request), properties, false, cancellationToken);
        }

        private static async Task CompleteCloseHandshakeAsync(WebSocket socket,
            CancellationToken cancellationToken)
        {
            while (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseSent)
            {
                var message = await WebSocketTestProtocol.ReceiveAsync(socket, cancellationToken)
                    .ConfigureAwait(false);
                if (message.MessageType != WebSocketMessageType.Close)
                {
                    continue;
                }

                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                return;
            }
        }
    }
}
