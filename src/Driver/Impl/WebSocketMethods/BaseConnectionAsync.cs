using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
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
        private readonly TimeSpan _connTimeout;
        private readonly string _addr;
        private readonly ConcurrentDictionary<ulong, TaskCompletionSource<WsMessage>> _pendingRequests =
            new ConcurrentDictionary<ulong, TaskCompletionSource<WsMessage>>();
        private readonly SemaphoreSlim _sendSemaphore = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _closeCts = new CancellationTokenSource();
        private readonly object _exitLock = new object();
        private Task _receiveLoopTask;
        private bool _exit;
        private int _disposed;

        private static readonly TimeSpan DefaultConnTimeout = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

        public WebSocketState State => _client.State;

        protected BaseConnectionAsync(string addr, TimeSpan connectTimeout = default,
            TimeSpan readTimeout = default, TimeSpan writeTimeout = default, bool enableCompression = false)
        {
            _client = new ClientWebSocket();
            _client.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
#if NET6_0_OR_GREATER
            if (enableCompression)
            {
                _client.Options.DangerousDeflateOptions = new WebSocketDeflateOptions
                {
                    ClientMaxWindowBits = 15,
                    ServerMaxWindowBits = 15,
                    ClientContextTakeover = true,
                    ServerContextTakeover = true
                };
            }
#endif
            _addr = addr;
            _connTimeout = connectTimeout == default ? DefaultConnTimeout : connectTimeout;
            _readTimeout = readTimeout == default ? DefaultReadTimeout : readTimeout;
            _writeTimeout = writeTimeout == default ? DefaultWriteTimeout : writeTimeout;
        }

        protected async Task ClientConnectAsync(CancellationToken cancellationToken = default)
        {
            using (var timeoutCts = new CancellationTokenSource(_connTimeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken))
            {
                await _client.ConnectAsync(new Uri(_addr), linkedCts.Token).ConfigureAwait(false);
            }

            if (_client.State != WebSocketState.Open)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECT_FAILED,
                    $"connect to {_addr} fail");
            }

            _receiveLoopTask = Task.Run(ReceiveLoop);
            try
            {
                var versionResp = await SendJsonBackJsonAsync<WSVersionReq, WSVersionResp>(
                    WSAction.Version, new WSVersionReq(), 0, cancellationToken).ConfigureAwait(false);
                TDengineVersion.CheckVersionCompatibility(versionResp.Version);
            }
            catch
            {
                await CloseAsync().ConfigureAwait(false);
                throw;
            }
        }

        protected static ulong _GetReqId()
        {
            return (ulong)ReqId.GetReqId();
        }

        protected static void WriteUInt64ToBytes(byte[] byteArray, ulong value, int offset)
        {
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(byteArray.AsSpan(offset, sizeof(ulong)), value);
#else
            byteArray[offset] = (byte)value;
            byteArray[offset + 1] = (byte)(value >> 8);
            byteArray[offset + 2] = (byte)(value >> 16);
            byteArray[offset + 3] = (byte)(value >> 24);
            byteArray[offset + 4] = (byte)(value >> 32);
            byteArray[offset + 5] = (byte)(value >> 40);
            byteArray[offset + 6] = (byte)(value >> 48);
            byteArray[offset + 7] = (byte)(value >> 56);
#endif
        }

        protected static void WriteUInt32ToBytes(byte[] byteArray, uint value, int offset)
        {
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(byteArray.AsSpan(offset, sizeof(uint)), value);
#else
            byteArray[offset] = (byte)value;
            byteArray[offset + 1] = (byte)(value >> 8);
            byteArray[offset + 2] = (byte)(value >> 16);
            byteArray[offset + 3] = (byte)(value >> 24);
#endif
        }

        protected static void WriteUInt16ToBytes(byte[] byteArray, ushort value, int offset)
        {
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(byteArray.AsSpan(offset, sizeof(ushort)), value);
#else
            byteArray[offset] = (byte)value;
            byteArray[offset + 1] = (byte)(value >> 8);
#endif
        }

        protected static ulong ReadUInt64FromBytes(byte[] byteArray, int offset)
        {
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                byteArray.AsSpan(offset, sizeof(ulong)));
#else
            return byteArray[offset]
                   | ((ulong)byteArray[offset + 1] << 8)
                   | ((ulong)byteArray[offset + 2] << 16)
                   | ((ulong)byteArray[offset + 3] << 24)
                   | ((ulong)byteArray[offset + 4] << 32)
                   | ((ulong)byteArray[offset + 5] << 40)
                   | ((ulong)byteArray[offset + 6] << 48)
                   | ((ulong)byteArray[offset + 7] << 56);
#endif
        }

        protected async Task<byte[]> SendBinaryBackBytesAsync(byte[] request, ulong reqId,
            CancellationToken cancellationToken = default)
        {
            var responseMessage = await SendAndWaitAsync(reqId,
                () => SendBinaryAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (responseMessage.Exception != null) throw responseMessage.Exception;

            if (responseMessage.MessageType == WebSocketMessageType.Binary)
            {
                return responseMessage.Message;
            }

            WSBaseResp resp;
            string response = null;
            try
            {
                response = Encoding.UTF8.GetString(responseMessage.Message);
                resp = JsonConvert.DeserializeObject<WSBaseResp>(response);
            }
            catch (Exception e)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "receive unexpected message", e.Message);
            }

            if (resp == null)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "receive empty json message", request, response);
            }

            throw new TDengineError(resp.Code, resp.Message, request, response);
        }

        protected async Task<T> SendBinaryBackJsonAsync<T>(byte[] request, ulong reqId,
            CancellationToken cancellationToken = default) where T : IWSBaseResp
        {
            var responseMessage = await SendAndWaitAsync(reqId,
                () => SendBinaryAsync(request, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (responseMessage.Exception != null) throw responseMessage.Exception;

            if (responseMessage.MessageType != WebSocketMessageType.Text)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "receive unexpected binary message");
            }

            var response = Encoding.UTF8.GetString(responseMessage.Message);
            var resp = JsonConvert.DeserializeObject<T>(response);
            if (resp == null)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "receive empty json message", response);
            }

            if (resp.Code == 0) return resp;
            throw new TDengineError(resp.Code, resp.Message);
        }

        protected async Task<T2> SendJsonBackJsonAsync<T1, T2>(string action, T1 req, ulong reqId,
            CancellationToken cancellationToken = default) where T2 : IWSBaseResp
        {
            string request = null;
            var responseMessage = await SendAndWaitAsync(reqId, async () =>
            {
                request = await SendJsonAsync(action, req, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            if (responseMessage.Exception != null) throw responseMessage.Exception;

            if (responseMessage.MessageType != WebSocketMessageType.Text)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "receive unexpected binary message", responseMessage.Message, request);
            }

            T2 resp;
            string response = null;
            try
            {
                response = Encoding.UTF8.GetString(responseMessage.Message);
                resp = JsonConvert.DeserializeObject<T2>(response);
            }
            catch (Exception e)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    $"receive unexpected message: {e}",
                    "req:" + request + ";resp:" + response);
            }

            if (resp == null)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "receive empty json message", "req:" + request + ";resp:" + response);
            }

            if (resp.Action != action)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    $"receive unexpected action {resp.Action},req:{request}",
                    response);
            }

            if (resp.Code == 0) return resp;
            throw new TDengineError(resp.Code, resp.Message);
        }

        protected Task<T2> SendJsonBackJsonAsync<T1, T2>(string action, T1 req,
            CancellationToken cancellationToken = default) where T2 : IWSBaseResp
        {
            return SendJsonBackJsonAsync<T1, T2>(action, req, ExtractReqId(req), cancellationToken);
        }

        protected async Task<byte[]> SendJsonBackBytesAsync<T>(string action, T req, ulong reqId,
            CancellationToken cancellationToken = default)
        {
            string request = null;
            var responseMessage = await SendAndWaitAsync(reqId, async () =>
            {
                request = await SendJsonAsync(action, req, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            if (responseMessage.Exception != null) throw responseMessage.Exception;

            if (responseMessage.MessageType == WebSocketMessageType.Binary)
            {
                return responseMessage.Message;
            }

            WSBaseResp resp;
            string response = null;
            try
            {
                response = Encoding.UTF8.GetString(responseMessage.Message);
                resp = JsonConvert.DeserializeObject<WSBaseResp>(response);
            }
            catch (Exception)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "receive unexpected message",
                    "req:" + request + ";resp:" + response);
            }

            if (resp == null)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "receive empty json message", "req:" + request + ";resp:" + response);
            }

            throw new TDengineError(resp.Code, resp.Message, response);
        }

        protected Task<byte[]> SendJsonBackBytesAsync<T>(string action, T req,
            CancellationToken cancellationToken = default)
        {
            return SendJsonBackBytesAsync(action, req, ExtractReqId(req), cancellationToken);
        }

        private static ulong ExtractReqId<T>(T req)
        {
            var property = typeof(T).GetProperty("ReqId", BindingFlags.Instance | BindingFlags.Public);
            if (property == null)
            {
                return _GetReqId();
            }

            var value = property.GetValue(req, null);
            if (value == null)
            {
                return _GetReqId();
            }

            return Convert.ToUInt64(value);
        }

        protected async Task<string> SendJsonAsync<T>(string action, T req,
            CancellationToken cancellationToken = default)
        {
            var request = JsonConvert.SerializeObject(new WSActionReq<T>
            {
                Action = action,
                Args = req
            });
            await SendTextAsync(request, cancellationToken).ConfigureAwait(false);
            return request;
        }

        private TaskCompletionSource<WsMessage> AddTask(ulong reqId)
        {
            lock (_exitLock)
            {
                if (_exit)
                {
                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                        "websocket connection is closed");
                }

                var tcs = CreateTaskCompletionSource();
                if (!_pendingRequests.TryAdd(reqId, tcs))
                {
                    throw new InvalidOperationException($"Request with reqId '0x{reqId:x}' already exists.");
                }

                return tcs;
            }
        }

        private static TaskCompletionSource<WsMessage> CreateTaskCompletionSource()
        {
#if NET5_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            return new TaskCompletionSource<WsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
#else
            return new TaskCompletionSource<WsMessage>();
#endif
        }

        private async Task<WsMessage> SendAndWaitAsync(ulong reqId, Func<Task> send,
            CancellationToken cancellationToken)
        {
            var tcs = AddTask(reqId);
            try
            {
                await send().ConfigureAwait(false);
            }
            catch
            {
                _pendingRequests.TryRemove(reqId, out _);
                throw;
            }

            using (var timeoutCts = new CancellationTokenSource(_readTimeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken, _closeCts.Token))
            using (linkedCts.Token.Register(() =>
                   {
                       if (_pendingRequests.TryRemove(reqId, out var removedTcs))
                       {
                           removedTcs.TrySetCanceled();
                       }
                   }))
            {
                try
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    if (_closeCts.IsCancellationRequested)
                    {
                        throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                            "websocket connection is closed");
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    throw new TimeoutException($"Request timed out. reqId: 0x{reqId:x}");
                }
            }
        }

        private async Task SendAsync(ArraySegment<byte> data, WebSocketMessageType messageType,
            CancellationToken cancellationToken)
        {
            if (!IsAvailable())
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                    "websocket connection is closed");
            }

            using (var timeoutCts = new CancellationTokenSource(_writeTimeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken, _closeCts.Token))
            {
                try
                {
                    await _client.SendAsync(data, messageType, true, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (_closeCts.IsCancellationRequested)
                    {
                        throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                            "websocket connection is closed");
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_WRITE_TIMEOUT,
                        "write message timeout");
                }
            }
        }

        private async Task SendTextAsync(string request, CancellationToken cancellationToken)
        {
            await WaitSendSemaphoreAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var data = new ArraySegment<byte>(Encoding.UTF8.GetBytes(request));
                await SendAsync(data, WebSocketMessageType.Text, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }

        private async Task SendBinaryAsync(byte[] request, CancellationToken cancellationToken)
        {
            await WaitSendSemaphoreAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var data = new ArraySegment<byte>(request);
                await SendAsync(data, WebSocketMessageType.Binary, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }

        private async Task WaitSendSemaphoreAsync(CancellationToken cancellationToken)
        {
            using (var timeoutCts = new CancellationTokenSource(_writeTimeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken, _closeCts.Token))
            {
                try
                {
                    await _sendSemaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (_closeCts.IsCancellationRequested)
                    {
                        throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                            "websocket connection is closed");
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_WRITE_TIMEOUT,
                        "wait send lock timeout");
                }
            }
        }

        private async Task ReceiveLoop()
        {
            Exception exception = null;
            try
            {
                var buffer = new byte[1024 * 8];
                while (_client.State == WebSocketState.Open)
                {
                    var message = await ReceiveMessageAsync(buffer).ConfigureAwait(false);
                    if (message.MessageType == WebSocketMessageType.Close)
                    {
                        if (!IsClosing())
                        {
                            exception = new TDengineError(
                                (int)TDengineError.InternalErrorCode.WS_RECEIVE_CLOSE_FRAME,
                                "receive websocket close frame");
                        }

                        break;
                    }

                    DispatchResponse(message.Bytes, message.MessageType);
                }
            }
            catch (OperationCanceledException) when (_closeCts.IsCancellationRequested)
            {
                // Expected when CloseAsync/DisposeAsync wakes the background receive loop.
            }
            catch (Exception e)
            {
                if (!IsExpectedLocalCloseException(e))
                {
                    exception = e;
                }
            }
            finally
            {
                DoClose(exception);
            }
        }

        private async Task<ReceivedMessage> ReceiveMessageAsync(byte[] buffer)
        {
            var result = await ReceiveFrameAsync(buffer).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new ReceivedMessage(null, WebSocketMessageType.Close);
            }

            if (result.EndOfMessage)
            {
                var bytes = new byte[result.Count];
                Buffer.BlockCopy(buffer, 0, bytes, 0, result.Count);
                return new ReceivedMessage(bytes, result.MessageType);
            }

            using (var memoryStream = new MemoryStream(Math.Max(buffer.Length, result.Count)))
            {
                var messageType = result.MessageType;
                memoryStream.Write(buffer, 0, result.Count);
                do
                {
                    result = await ReceiveFrameAsync(buffer).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return new ReceivedMessage(null, WebSocketMessageType.Close);
                    }

                    memoryStream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                return new ReceivedMessage(memoryStream.ToArray(), messageType);
            }
        }

        private async Task<WebSocketReceiveResult> ReceiveFrameAsync(byte[] buffer)
        {
            return await _client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None)
                .ConfigureAwait(false);
        }

        private bool IsExpectedLocalCloseException(Exception exception)
        {
            if (!_closeCts.IsCancellationRequested)
            {
                return false;
            }

            if (exception is OperationCanceledException)
            {
                return true;
            }

            if (exception is WebSocketException || exception is IOException)
            {
                return true;
            }

            var aggregateException = exception as AggregateException;
            if (aggregateException != null && aggregateException.InnerException != null)
            {
                return IsExpectedLocalCloseException(aggregateException.InnerException);
            }

            return false;
        }

        private void DispatchResponse(byte[] bytes, WebSocketMessageType messageType)
        {
            TaskCompletionSource<WsMessage> tcs;
            switch (messageType)
            {
                case WebSocketMessageType.Binary:
                    if (bytes.Length < 16)
                    {
                        throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                            $"binary message length is less than 16, length:{bytes.Length}");
                    }

                    var flag = ReadUInt64FromBytes(bytes, 0);
                    var reqId = ReadUInt64FromBytes(bytes, 8);
                    if (flag == 0xffffffffffffffff)
                    {
                        if (bytes.Length < 34)
                        {
                            throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                                $"binary raw block message length is less than 34, length:{bytes.Length}");
                        }

                        reqId = ReadUInt64FromBytes(bytes, 26);
                    }

                    if (_pendingRequests.TryRemove(reqId, out tcs))
                    {
                        tcs.TrySetResult(new WsMessage(bytes, messageType, null));
                    }

                    break;
                case WebSocketMessageType.Text:
                    WSBaseResp resp;
                    try
                    {
                        resp = JsonConvert.DeserializeObject<WSBaseResp>(Encoding.UTF8.GetString(bytes));
                    }
                    catch (Exception e)
                    {
                        throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                            "receive unexpected message", e.Message);
                    }

                    if (resp == null)
                    {
                        throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                            "receive empty json message");
                    }

                    if (_pendingRequests.TryRemove(resp.ReqId, out tcs))
                    {
                        tcs.TrySetResult(new WsMessage(bytes, messageType, null));
                    }

                    break;
                default:
                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                        "receive unexpected message type");
            }
        }

        private struct ReceivedMessage
        {
            public ReceivedMessage(byte[] bytes, WebSocketMessageType messageType)
            {
                Bytes = bytes;
                MessageType = messageType;
            }

            public byte[] Bytes { get; }

            public WebSocketMessageType MessageType { get; }
        }

        private bool IsClosing()
        {
            lock (_exitLock)
            {
                return _exit;
            }
        }

        private bool BeginClose(Exception e = null)
        {
            lock (_exitLock)
            {
                if (_exit) return false;
                _exit = true;
            }

            _closeCts.Cancel();
            foreach (var kvp in _pendingRequests)
            {
                if (e != null)
                {
                    kvp.Value.TrySetResult(new WsMessage(null, WebSocketMessageType.Close, e));
                }
                else
                {
                    kvp.Value.TrySetCanceled();
                }
            }

            _pendingRequests.Clear();
            return true;
        }

        private void DoClose(Exception e = null)
        {
            if (!BeginClose(e)) return;
            _client.Abort();
            DisposeClient();
        }

        private async Task CloseClientOutputAsync()
        {
            var acquiredSendLock = false;
            try
            {
                acquiredSendLock = await _sendSemaphore.WaitAsync(CloseTimeout).ConfigureAwait(false);
                if (!acquiredSendLock)
                {
                    _client.Abort();
                    return;
                }

                var state = _client.State;
                if (state == WebSocketState.Open || state == WebSocketState.CloseReceived)
                {
                    var closeTask = _client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty,
                        CancellationToken.None);
                    if (await Task.WhenAny(closeTask, Task.Delay(CloseTimeout)).ConfigureAwait(false) == closeTask)
                    {
                        await closeTask.ConfigureAwait(false);
                    }
                    else
                    {
                        _client.Abort();
                        _ = ObserveCloseTaskAsync(closeTask);
                    }
                }
                else if (state != WebSocketState.Closed && state != WebSocketState.CloseSent)
                {
                    _client.Abort();
                }
            }
            catch
            {
                _client.Abort();
            }
            finally
            {
                if (acquiredSendLock)
                {
                    _sendSemaphore.Release();
                }
            }
        }

        private static async Task ObserveCloseTaskAsync(Task closeTask)
        {
            try
            {
                await closeTask.ConfigureAwait(false);
            }
            catch
            {
                // The connection was already aborted because close output exceeded the shutdown timeout.
            }
        }

        private async Task WaitReceiveLoopAsync()
        {
            var task = _receiveLoopTask;
            if (task == null || task.IsCompleted)
            {
                return;
            }

            if (await Task.WhenAny(task, Task.Delay(CloseTimeout)).ConfigureAwait(false) != task)
            {
                _client.Abort();
                if (await Task.WhenAny(task, Task.Delay(CloseTimeout)).ConfigureAwait(false) != task)
                {
                    return;
                }
            }

            await task.ConfigureAwait(false);
        }

        private void DisposeClient()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _client.Dispose();
            _closeCts.Dispose();
        }

        public async Task CloseAsync()
        {
            if (BeginClose())
            {
                await CloseClientOutputAsync().ConfigureAwait(false);
                await WaitReceiveLoopAsync().ConfigureAwait(false);
                DisposeClient();
            }
        }

        public bool IsAvailable(Exception e = null)
        {
            lock (_exitLock)
            {
                if (_exit) return false;
            }

            if (_client.State != WebSocketState.Open)
                return false;

            switch (e)
            {
                case null:
                    return true;
                case WebSocketException _:
                    return false;
                case AggregateException ae:
                    if (ae.InnerException is WebSocketException) return false;
                    if (ae.InnerException is TDengineError inner)
                    {
                        return BaseConnection.IsConnectionAvailableByTdengineError(inner);
                    }

                    return true;
                case TDengineError te:
                    return BaseConnection.IsConnectionAvailableByTdengineError(te);
                default:
                    return true;
            }
        }
    }
}
