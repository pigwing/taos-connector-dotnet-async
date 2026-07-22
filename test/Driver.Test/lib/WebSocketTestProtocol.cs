using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Test.Fixture
{
    internal static class WebSocketTestProtocol
    {
        private const int MaximumTestMessageSize = 16 * 1024 * 1024;

        internal static async Task<JObject> ReceiveJsonAsync(WebSocket webSocket,
            CancellationToken cancellationToken)
        {
            var message = await ReceiveAsync(webSocket, cancellationToken).ConfigureAwait(false);
            if (message.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Expected a text WebSocket message.");
            }

            return JObject.Parse(Encoding.UTF8.GetString(message.Bytes));
        }

        internal static async Task<WebSocketTestMessage> ReceiveAsync(WebSocket webSocket,
            CancellationToken cancellationToken)
        {
            var rented = ArrayPool<byte>.Shared.Rent(8 * 1024);
            try
            {
                using (var stream = new MemoryStream())
                {
                    WebSocketMessageType? messageType = null;
                    while (true)
                    {
                        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(rented), cancellationToken)
                            .ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return new WebSocketTestMessage(Array.Empty<byte>(), WebSocketMessageType.Close);
                        }

                        if (messageType.HasValue && messageType.Value != result.MessageType)
                        {
                            throw new InvalidDataException("The WebSocket message type changed between fragments.");
                        }

                        messageType = result.MessageType;
                        if (stream.Length > MaximumTestMessageSize - result.Count)
                        {
                            throw new InvalidDataException("The WebSocket test message exceeded the size limit.");
                        }

                        stream.Write(rented, 0, result.Count);
                        if (result.EndOfMessage)
                        {
                            return new WebSocketTestMessage(stream.ToArray(), messageType.Value);
                        }
                    }
                }
            }
            finally
            {
                Array.Clear(rented, 0, rented.Length);
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        internal static ulong GetRequestId(JObject request)
        {
            var token = request["args"]?["req_id"] ?? request["req_id"];
            if (token == null)
            {
                throw new InvalidDataException("The WebSocket request did not contain req_id.");
            }

            return token.Value<ulong>();
        }

        internal static string? GetAction(JObject request)
        {
            return request["action"]?.Value<string>();
        }

        internal static ulong GetBinaryRequestId(byte[] request)
        {
            if (request == null || request.Length < sizeof(ulong))
            {
                throw new InvalidDataException("The binary WebSocket request is too short.");
            }

            return BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(0, sizeof(ulong)));
        }

        internal static Task SendVersionResponseAsync(WebSocket webSocket, JObject request,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(GetAction(request), "version", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Expected the initial version request.");
            }

            return SendResponseAsync(webSocket, "version", 0, new JObject
            {
                ["version"] = "3.3.6.0"
            }, false, cancellationToken);
        }

        internal static Task SendResponseAsync(WebSocket webSocket, string action, ulong requestId,
            JObject additionalProperties, bool fragmented, CancellationToken cancellationToken)
        {
            var response = new JObject
            {
                ["code"] = 0,
                ["message"] = string.Empty,
                ["action"] = action,
                ["req_id"] = requestId,
                ["timing"] = 0
            };
            if (additionalProperties != null)
            {
                foreach (var property in additionalProperties.Properties())
                {
                    response[property.Name] = property.Value;
                }
            }

            return SendTextAsync(webSocket, response.ToString(Formatting.None), fragmented, cancellationToken);
        }

        internal static Task SendErrorResponseAsync(WebSocket webSocket, string action, ulong requestId, int code,
            string message, CancellationToken cancellationToken)
        {
            var response = new JObject
            {
                ["code"] = code,
                ["message"] = message,
                ["action"] = action,
                ["req_id"] = requestId,
                ["timing"] = 0
            };
            return SendTextAsync(webSocket, response.ToString(Formatting.None), false, cancellationToken);
        }

        internal static async Task SendTextAsync(WebSocket webSocket, string text, bool fragmented,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (!fragmented || bytes.Length < 2)
            {
                await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var split = bytes.Length / 2;
            await webSocket.SendAsync(new ArraySegment<byte>(bytes, 0, split), WebSocketMessageType.Text, false,
                cancellationToken).ConfigureAwait(false);
            await webSocket.SendAsync(new ArraySegment<byte>(bytes, split, bytes.Length - split),
                WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }

        internal static async Task SendBinaryAsync(WebSocket webSocket, byte[] bytes, bool fragmented,
            CancellationToken cancellationToken)
        {
            if (!fragmented || bytes.Length < 2)
            {
                await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var split = bytes.Length / 2;
            await webSocket.SendAsync(new ArraySegment<byte>(bytes, 0, split), WebSocketMessageType.Binary, false,
                cancellationToken).ConfigureAwait(false);
            await webSocket.SendAsync(new ArraySegment<byte>(bytes, split, bytes.Length - split),
                WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        }
    }

    internal sealed class WebSocketTestMessage
    {
        internal WebSocketTestMessage(byte[] bytes, WebSocketMessageType messageType)
        {
            Bytes = bytes;
            MessageType = messageType;
        }

        internal byte[] Bytes { get; }

        internal WebSocketMessageType MessageType { get; }
    }
}
