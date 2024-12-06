using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;

namespace TDengine.Driver.Impl.WebSocketMethods
{

    public class BaseConnectionAsync
    {
        private readonly ClientWebSocket _client;

        private readonly TimeSpan _readTimeout;

        private readonly TimeSpan _writeTimeout;

        private ulong _reqId;

        private readonly TimeSpan _defaultConnTimeout = TimeSpan.FromMinutes(1.0);

        private readonly TimeSpan _defaultReadTimeout = TimeSpan.FromMinutes(5.0);

        private readonly TimeSpan _defaultWriteTimeout = TimeSpan.FromSeconds(10.0);

        private readonly TimeSpan _connTimeout;

        private readonly string _addr;

        public WebSocketState State => _client.State;

        protected BaseConnectionAsync(string addr, TimeSpan connectTimeout = default(TimeSpan),
            TimeSpan readTimeout = default(TimeSpan), TimeSpan writeTimeout = default(TimeSpan),
            bool enableCompression = false)
        {
            _client = new ClientWebSocket();
            _client.Options.KeepAliveInterval = TimeSpan.FromSeconds(30.0);
#if NET6_0_OR_GREATER
            if (enableCompression)
            {
                _client.Options.DangerousDeflateOptions = new WebSocketDeflateOptions()
                {
                    ClientMaxWindowBits = 15, // Default value
                    ServerMaxWindowBits = 15, // Default value
                    ClientContextTakeover = true, // Default value
                    ServerContextTakeover = true // Default value
                };
            }
#endif

            if (connectTimeout == default(TimeSpan))
            {
                connectTimeout = _defaultConnTimeout;
            }

            if (readTimeout == default(TimeSpan))
            {
                readTimeout = _defaultReadTimeout;
            }

            if (writeTimeout == default(TimeSpan))
            {
                writeTimeout = _defaultWriteTimeout;
            }

            _addr = addr;
            _connTimeout = connectTimeout;
            _readTimeout = readTimeout;
            _writeTimeout = writeTimeout;
        }

        protected async Task ClientConnectAsync()
        {
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                cts.CancelAfter(_connTimeout);
                await _client.ConnectAsync(new Uri(_addr), cts.Token).ConfigureAwait(continueOnCapturedContext: false);
            }

            if (_client.State != WebSocketState.Open)
            {
                throw new TDengineError(61445, "connect to " + _addr + " fail");
            }
        }

        protected ulong _GetReqId()
        {
            _reqId++;
            return _reqId;
        }

        protected static void WriteUInt64ToBytes(byte[] byteArray, ulong value, int offset)
        {
            byteArray[offset] = (byte)value;
            byteArray[offset + 1] = (byte)(value >> 8);
            byteArray[offset + 2] = (byte)(value >> 16);
            byteArray[offset + 3] = (byte)(value >> 24);
            byteArray[offset + 4] = (byte)(value >> 32);
            byteArray[offset + 5] = (byte)(value >> 40);
            byteArray[offset + 6] = (byte)(value >> 48);
            byteArray[offset + 7] = (byte)(value >> 56);
        }

        protected static void WriteUInt32ToBytes(byte[] byteArray, uint value, int offset)
        {
            byteArray[offset] = (byte)value;
            byteArray[offset + 1] = (byte)(value >> 8);
            byteArray[offset + 2] = (byte)(value >> 16);
            byteArray[offset + 3] = (byte)(value >> 24);
        }

        protected static void WriteUInt16ToBytes(byte[] byteArray, ushort value, int offset)
        {
            byteArray[offset] = (byte)value;
            byteArray[offset + 1] = (byte)(value >> 8);
        }

        protected async Task<byte[]> SendBinaryBackBytesAsync(byte[] request)
        {
            await SendBinaryAsync(request);
            var result = await ReceiveAsync();
            if (result.Item2 == WebSocketMessageType.Binary)
            {
                return result.Item1;
            }

            var resp = JsonConvert.DeserializeObject<IWSBaseResp>(Encoding.UTF8.GetString(result.Item1));
            throw new TDengineError(resp.Code, resp.Message, request, Encoding.UTF8.GetString(result.Item1));
        }

        protected async Task<T> SendBinaryBackJsonAsync<T>(byte[] request) where T : IWSBaseResp
        {
            await SendBinaryAsync(request);
            var result = await ReceiveAsync();
            if (result.Item2 != WebSocketMessageType.Text)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "receive unexpected binary message");
            }

            var resp = JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(result.Item1));
            if (resp.Code == 0) return resp;
            throw new TDengineError(resp.Code, resp.Message);
        }

        protected async Task<T2> SendJsonBackJsonAsync<T1, T2>(string action, T1 req) where T2 : IWSBaseResp
        {
            var reqStr = await SendJsonAsync(action, req);
            var result = await ReceiveAsync();
            if (result.Item2 != WebSocketMessageType.Text)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "receive unexpected binary message", result.Item1, reqStr);
            }

            var resp = JsonConvert.DeserializeObject<T2>(Encoding.UTF8.GetString(result.Item1));
            // Console.WriteLine(Encoding.UTF8.GetString(respBytes));
            if (resp.Action != action)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    $"receive unexpected action {resp.Action},req:{reqStr}",
                    Encoding.UTF8.GetString(result.Item1));
            }

            if (resp.Code == 0) return resp;
            throw new TDengineError(resp.Code, resp.Message);
        }

        protected async Task<byte[]> SendJsonBackBytesAsync<T>(string action, T req)
        {
            var reqStr = await SendJsonAsync(action, req);
            var result = await ReceiveAsync();
            if (result.Item2 == WebSocketMessageType.Binary)
            {
                return result.Item1;
            }

            var resp = JsonConvert.DeserializeObject<IWSBaseResp>(Encoding.UTF8.GetString(result.Item1));
            throw new TDengineError(resp.Code, resp.Message, reqStr);
        }

        protected async Task<string> SendJsonAsync<T>(string action, T req)
        {
            string request = JsonConvert.SerializeObject((object)new WSActionReq<T>
            {
                Action = action,
                Args = req
            });
            await SendTextAsync(request);
            return request;
        }

        private async Task SendAsync(ArraySegment<byte> data, WebSocketMessageType messageType)
        {
            if (!IsAvailable())
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                    "websocket connection is closed");
            }

            using (var cts = new CancellationTokenSource())
            {
                cts.CancelAfter(_writeTimeout);
                try
                {
                    await _client.SendAsync(data, messageType, true, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_WRITE_TIMEOUT,
                        "write message timeout");
                }
            }
        }

        private async Task SendTextAsync(string request)
        {
            var data = new ArraySegment<byte>(Encoding.UTF8.GetBytes(request));
            await SendAsync(data, WebSocketMessageType.Text);
        }

        private async Task SendBinaryAsync(byte[] request)
        {
            var data = new ArraySegment<byte>(request);
            await SendAsync(data, WebSocketMessageType.Binary);
        }

        private async Task<Tuple<byte[], WebSocketMessageType>> ReceiveAsync()
        {
            if (!IsAvailable())
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                    "websocket connection is closed");
            }

            using (var cts = new CancellationTokenSource())
            {
                cts.CancelAfter(_readTimeout);
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    int bufferSize = 1024 * 4;
                    byte[] buffer = new byte[bufferSize];
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _client.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token)
                            .ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await _client
                                .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                                .ConfigureAwait(false);
                            throw new TDengineError((int)TDengineError.InternalErrorCode.WS_RECEIVE_CLOSE_FRAME,
                                "receive websocket close frame");
                        }

                        memoryStream.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    return Tuple.Create(memoryStream.ToArray(), result.MessageType);
                }
            }

            
        }

        public async Task CloseAsync()
        {
            try
            {
                await _client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch (Exception)
            {
                // ignored
            }
        }

        public bool IsAvailable(Exception e = null)
        {
            if (_client.State != WebSocketState.Open)
                return false;

            switch (e)
            {
                case null:
                    return true;
                case WebSocketException _:
                    return false;
                case AggregateException ae:
                    return !(ae.InnerException is WebSocketException);
                case TDengineError te:
                    return te.Code != (int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED &&
                           te.Code != (int)TDengineError.InternalErrorCode.WS_RECEIVE_CLOSE_FRAME &&
                           te.Code != (int)TDengineError.InternalErrorCode.WS_WRITE_TIMEOUT;
                default:
                    return true;
            }
        }
    }
}
