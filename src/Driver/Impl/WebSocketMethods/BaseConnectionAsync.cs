using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private readonly TimeSpan _connTimeout;
        private readonly string _addr;
        private readonly string _safeAddr;
        private readonly ConcurrentDictionary<ulong, TaskCompletionSource<WsMessage>> _pendingRequests =
            new ConcurrentDictionary<ulong, TaskCompletionSource<WsMessage>>();
        private readonly Dictionary<ulong, LinkedListNode<ulong>> _ignoredResponseIds =
            new Dictionary<ulong, LinkedListNode<ulong>>();
        private readonly LinkedList<ulong> _ignoredResponseOrder = new LinkedList<ulong>();
        private readonly SemaphoreSlim _sendSemaphore = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _closeCts = new CancellationTokenSource();
        private readonly CancellationToken _closeToken;
        private readonly object _exitLock = new object();
        private readonly object _closeTaskLock = new object();
        private readonly object _closeCtsLock = new object();
        private Task _receiveLoopTask;
        private Task _closeTask;
        private volatile bool _exit;
        private int _disposed;
        private int _pendingRequestCount;
        private int _closeOperationCount;
        private int _closeCtsDisposed;

        private static readonly TimeSpan DefaultConnTimeout = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);
        private static readonly Encoding Utf8Encoding = new UTF8Encoding(false, true);
        internal const int MaximumMessageSize = 256 * 1024 * 1024;
        internal const int MaximumTextMessageSize = 16 * 1024 * 1024;
        internal const int MaximumPendingRequests = 4096;
        private const int ReceiveBufferSize = 8 * 1024;
        internal const int MaximumIgnoredResponseIds = 8192;

        public WebSocketState State
        {
            get
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    return WebSocketState.Closed;
                }

                try
                {
                    return _client.State;
                }
                catch (ObjectDisposedException)
                {
                    return WebSocketState.Closed;
                }
            }
        }

        protected BaseConnectionAsync(string addr, TimeSpan connectTimeout = default,
            TimeSpan readTimeout = default, TimeSpan writeTimeout = default, bool enableCompression = false)
        {
            _closeToken = _closeCts.Token;
            _client = new ClientWebSocket();
            _client.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
#if NET9_0_OR_GREATER
            _client.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
#endif
#if NET6_0_OR_GREATER
            if (enableCompression)
            {
                _client.Options.DangerousDeflateOptions = new WebSocketDeflateOptions
                {
                    ClientMaxWindowBits = 15,
                    ServerMaxWindowBits = 15,
                    ClientContextTakeover = false,
                    ServerContextTakeover = false
                };
            }
#endif
            ValidateTimeout(connectTimeout, nameof(connectTimeout));
            ValidateTimeout(readTimeout, nameof(readTimeout));
            ValidateTimeout(writeTimeout, nameof(writeTimeout));
            _addr = addr;
            _safeAddr = SanitizeAddress(addr);
            _connTimeout = connectTimeout == default ? DefaultConnTimeout : connectTimeout;
            _readTimeout = readTimeout == default ? DefaultReadTimeout : readTimeout;
            _writeTimeout = writeTimeout == default ? DefaultWriteTimeout : writeTimeout;
        }

        private static void ValidateTimeout(TimeSpan timeout, string paramName)
        {
            TimeoutHelper.ValidateTimerTimeout(timeout, paramName, true);
        }

        protected async Task ClientConnectAsync(CancellationToken cancellationToken = default)
        {
            if (!TryEnterCloseOperation())
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                    "websocket connection is closed");
            }

            try
            {
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closeToken))
                {
                    linkedCts.CancelAfter(_connTimeout);
                    try
                    {
                        await _client.ConnectAsync(new Uri(_addr), linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_closeToken.IsCancellationRequested &&
                                                              !cancellationToken.IsCancellationRequested)
                    {
                        throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                            "websocket connection was closed while connecting");
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                             !_closeToken.IsCancellationRequested)
                    {
                        throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECT_TIMEOUT,
                            $"websocket connection timed out after {_connTimeout.TotalMilliseconds:0} ms");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("websocket connection was canceled", cancellationToken);
                    }
                    catch (Exception) when (_closeToken.IsCancellationRequested &&
                                            !cancellationToken.IsCancellationRequested)
                    {
                        throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                            "websocket connection was closed while connecting");
                    }
                    catch (Exception e)
                    {
                        throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECT_FAILED,
                            $"websocket connection to {_safeAddr} failed ({DescribeException(e)})");
                    }
                }

                if (State != WebSocketState.Open)
                {
                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECT_FAILED,
                        "websocket connection failed");
                }

                _receiveLoopTask = ReceiveLoop();
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
            finally
            {
                ExitCloseOperation();
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
            if (request == null) throw new ArgumentNullException(nameof(request));
            return await SendBinaryBackBytesCoreAsync(request, request.Length, reqId, false, cancellationToken)
                .ConfigureAwait(false);
        }

        private static string SanitizeAddress(string address)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                return "configured endpoint";
            }

            try
            {
                var builder = new UriBuilder(uri)
                {
                    UserName = string.Empty,
                    Password = string.Empty,
                    Query = string.Empty,
                    Fragment = string.Empty
                };
                return builder.Uri.GetLeftPart(UriPartial.Path);
            }
            catch
            {
                return "configured endpoint";
            }
        }

        private static string DescribeException(Exception exception)
        {
            var webSocketException = exception as WebSocketException;
            return webSocketException == null
                ? exception.GetType().Name
                : $"WebSocketException/{webSocketException.WebSocketErrorCode}";
        }

        protected async Task<byte[]> SendPooledBinaryBackBytesAsync(byte[] request, int requestLength, ulong reqId,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (requestLength < 0 || requestLength > request.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(requestLength));
            }

            return await SendBinaryBackBytesCoreAsync(request, requestLength, reqId, true, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<byte[]> SendBinaryBackBytesCoreAsync(byte[] request, int requestLength, ulong reqId,
            bool returnToPool, CancellationToken cancellationToken)
        {
            var responseMessage = await SendBinaryAndWaitAsync(request, requestLength, reqId, returnToPool,
                    cancellationToken)
                .ConfigureAwait(false);

            if (responseMessage != null && responseMessage.MessageType == WebSocketMessageType.Binary)
            {
                ValidateBinaryResponseRequestId(responseMessage.Message, reqId, "binary response");
                return responseMessage.Message;
            }

            if (responseMessage == null || responseMessage.MessageType != WebSocketMessageType.Text)
            {
                throw CloseForUnexpectedMessage("receive unexpected websocket message type",
                    DescribeMessage(reqId, responseMessage));
            }

            WSBaseResp resp;
            string response = null;
            try
            {
                response = Utf8Encoding.GetString(responseMessage.Message);
                resp = JsonConvert.DeserializeObject<WSBaseResp>(response);
            }
            catch (Exception e)
            {
                throw CloseForUnexpectedMessage("receive unexpected message",
                    DescribeMessage(reqId, responseMessage) + ",error:" + e.Message);
            }

            if (resp == null)
            {
                throw CloseForUnexpectedMessage("receive empty json message",
                    DescribeMessage(reqId, responseMessage));
            }

            ValidateResponseRequestId(resp, reqId, "binary request error response");
            if (resp.Code == 0)
            {
                throw CloseForUnexpectedMessage("binary request returned a successful text response",
                    DescribeMessage(reqId, responseMessage));
            }

            throw new TDengineError(resp.Code, resp.Message);
        }

        protected async Task<T> SendBinaryBackJsonAsync<T>(byte[] request, ulong reqId,
            CancellationToken cancellationToken = default) where T : IWSBaseResp
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return await SendBinaryBackJsonCoreAsync<T>(request, request.Length, reqId, false, null, cancellationToken)
                .ConfigureAwait(false);
        }

        protected async Task<T> SendBinaryBackJsonAsync<T>(byte[] request, ulong reqId, string expectedAction,
            CancellationToken cancellationToken = default) where T : IWSBaseResp
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return await SendBinaryBackJsonCoreAsync<T>(request, request.Length, reqId, false, expectedAction,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        protected async Task<T> SendBinaryBackJsonAsync<T>(byte[] request, int requestLength, ulong reqId,
            CancellationToken cancellationToken = default) where T : IWSBaseResp
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (requestLength < 0 || requestLength > request.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(requestLength));
            }

            return await SendBinaryBackJsonCoreAsync<T>(request, requestLength, reqId, false, null, cancellationToken)
                .ConfigureAwait(false);
        }

        protected async Task<T> SendBinaryBackJsonAsync<T>(byte[] request, int requestLength, ulong reqId,
            string expectedAction, CancellationToken cancellationToken = default) where T : IWSBaseResp
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (requestLength < 0 || requestLength > request.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(requestLength));
            }

            return await SendBinaryBackJsonCoreAsync<T>(request, requestLength, reqId, false, expectedAction,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        protected async Task<T> SendPooledBinaryBackJsonAsync<T>(byte[] request, int requestLength, ulong reqId,
            CancellationToken cancellationToken = default) where T : IWSBaseResp
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (requestLength < 0 || requestLength > request.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(requestLength));
            }

            return await SendBinaryBackJsonCoreAsync<T>(request, requestLength, reqId, true, null, cancellationToken)
                .ConfigureAwait(false);
        }

        protected async Task<T> SendPooledBinaryBackJsonAsync<T>(byte[] request, int requestLength, ulong reqId,
            string expectedAction, CancellationToken cancellationToken = default) where T : IWSBaseResp
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (requestLength < 0 || requestLength > request.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(requestLength));
            }

            return await SendBinaryBackJsonCoreAsync<T>(request, requestLength, reqId, true, expectedAction,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<T> SendBinaryBackJsonCoreAsync<T>(byte[] request, int requestLength, ulong reqId,
            bool returnToPool, string expectedAction, CancellationToken cancellationToken) where T : IWSBaseResp
        {
            var responseMessage = await SendBinaryAndWaitAsync(request, requestLength, reqId, returnToPool,
                    cancellationToken)
                .ConfigureAwait(false);

            if (responseMessage == null || responseMessage.MessageType != WebSocketMessageType.Text)
            {
                throw CloseForUnexpectedMessage("receive unexpected binary message",
                    DescribeMessage(reqId, responseMessage));
            }

            T resp;
            try
            {
                var response = Utf8Encoding.GetString(responseMessage.Message);
                resp = JsonConvert.DeserializeObject<T>(response);
            }
            catch (Exception e)
            {
                throw CloseForUnexpectedMessage("receive unexpected json message: " + e.Message,
                    DescribeMessage(reqId, responseMessage));
            }

            if (resp == null)
            {
                throw CloseForUnexpectedMessage("receive empty json message",
                    DescribeMessage(reqId, responseMessage));
            }

            ValidateResponseRequestId(resp, reqId, "binary response");
            if (expectedAction != null && !IsExpectedResponseAction(resp.Action, expectedAction))
            {
                throw CloseForUnexpectedMessage($"binary response returned unexpected action {resp.Action}",
                    DescribeMessage(reqId, responseMessage));
            }

            if (resp.Code == 0) return resp;
            throw new TDengineError(resp.Code, resp.Message);
        }

        private static bool IsExpectedResponseAction(string actualAction, string expectedAction)
        {
            if (string.Equals(actualAction, expectedAction, StringComparison.Ordinal))
            {
                return true;
            }

            return (string.Equals(expectedAction, WSAction.BinaryQuery, StringComparison.Ordinal) &&
                    string.Equals(actualAction, WSAction.Query, StringComparison.Ordinal)) ||
                   (string.Equals(expectedAction, WSAction.Query, StringComparison.Ordinal) &&
                    string.Equals(actualAction, WSAction.BinaryQuery, StringComparison.Ordinal));
        }

        protected async Task<T2> SendJsonBackJsonAsync<T1, T2>(string action, T1 req, ulong reqId,
            CancellationToken cancellationToken = default) where T2 : IWSBaseResp
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = SerializeJsonRequest(action, req);
            var responseMessage = await SendTextAndWaitAsync(request, reqId, cancellationToken)
                .ConfigureAwait(false);

            if (responseMessage == null || responseMessage.MessageType != WebSocketMessageType.Text)
            {
                throw CloseForUnexpectedMessage("receive unexpected binary message",
                    DescribeMessage(action, reqId, responseMessage));
            }

            T2 resp;
            string response = null;
            try
            {
                response = Utf8Encoding.GetString(responseMessage.Message);
                resp = JsonConvert.DeserializeObject<T2>(response);
            }
            catch (Exception e)
            {
                throw CloseForUnexpectedMessage($"receive unexpected message: {e.Message}",
                    DescribeMessage(action, reqId, responseMessage));
            }

            if (resp == null)
            {
                throw CloseForUnexpectedMessage("receive empty json message",
                    DescribeMessage(action, reqId, responseMessage));
            }

            ValidateResponseRequestId(resp, reqId, "json response");
            if (resp.Action != action)
            {
                throw CloseForUnexpectedMessage($"receive unexpected action {resp.Action}",
                    DescribeMessage(action, reqId, responseMessage));
            }

            if (resp.Code == 0) return resp;
            throw new TDengineError(resp.Code, resp.Message);
        }

        protected async Task<byte[]> SendJsonBackBytesAsync<T>(string action, T req, ulong reqId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = SerializeJsonRequest(action, req);
            var responseMessage = await SendTextAndWaitAsync(request, reqId, cancellationToken)
                .ConfigureAwait(false);

            if (responseMessage != null && responseMessage.MessageType == WebSocketMessageType.Binary)
            {
                ValidateBinaryResponseRequestId(responseMessage.Message, reqId, "json binary response");
                return responseMessage.Message;
            }

            if (responseMessage == null || responseMessage.MessageType != WebSocketMessageType.Text)
            {
                throw CloseForUnexpectedMessage("receive unexpected websocket message type",
                    DescribeMessage(action, reqId, responseMessage));
            }

            WSBaseResp resp;
            string response = null;
            try
            {
                response = Utf8Encoding.GetString(responseMessage.Message);
                resp = JsonConvert.DeserializeObject<WSBaseResp>(response);
            }
            catch (Exception e)
            {
                throw CloseForUnexpectedMessage("receive unexpected message: " + e.Message,
                    DescribeMessage(action, reqId, responseMessage));
            }

            if (resp == null)
            {
                throw CloseForUnexpectedMessage("receive empty json message",
                    DescribeMessage(action, reqId, responseMessage));
            }

            ValidateResponseRequestId(resp, reqId, "json request error response");
            if (resp.Action != action)
            {
                throw CloseForUnexpectedMessage($"receive unexpected action {resp.Action}",
                    DescribeMessage(action, reqId, responseMessage));
            }

            if (resp.Code == 0)
            {
                throw CloseForUnexpectedMessage("json request returned a successful text response",
                    DescribeMessage(action, reqId, responseMessage));
            }

            throw new TDengineError(resp.Code, resp.Message);
        }

        private static string DescribeMessage(ulong reqId, WsMessage message)
        {
            return DescribeMessage(null, reqId, message);
        }

        private static string DescribeMessage(string action, ulong reqId, WsMessage message)
        {
            var actionPart = string.IsNullOrEmpty(action) ? string.Empty : "action:" + action + ",";
            var length = message == null || message.Message == null ? 0 : message.Message.Length;
            var messageType = message == null ? WebSocketMessageType.Close : message.MessageType;
            return $"{actionPart}reqId:0x{reqId:x},messageType:{messageType},length:{length}";
        }

        protected async Task SendJsonAsync<T>(string action, T req, ulong reqId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = SerializeJsonRequest(action, req);
            if (!IsOneWayResponse(action) && !TryMarkIgnoredResponse(reqId))
            {
                var error = CreateIgnoredResponseLimitError();
                DoClose(error);
                throw CreateRequestException(reqId, false, error);
            }

            try
            {
                await SendTextMessageAsync(request, reqId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (!IsOneWayResponse(action))
                {
                    RemoveIgnoredResponse(reqId);
                }

                throw;
            }
        }

        private static string SerializeJsonRequest<T>(string action, T req)
        {
            return JsonConvert.SerializeObject(new WSActionReq<T>
            {
                Action = action,
                Args = req
            });
        }

        protected TDengineError CloseForUnexpectedMessage(string message, string details = null)
        {
            var error = details == null
                ? new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE, message)
                : new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE, message, details);
            DoClose(error);
            return error;
        }

        private void ValidateResponseRequestId(IWSBaseResp response, ulong expectedRequestId,
            string operation)
        {
            if (response.ReqId != expectedRequestId)
            {
                throw CloseForUnexpectedMessage($"{operation} returned an unexpected request id");
            }
        }

        private void ValidateBinaryResponseRequestId(byte[] response, ulong expectedRequestId,
            string operation)
        {
            if (response == null || response.Length < 16)
            {
                throw CloseForUnexpectedMessage($"{operation} is too short");
            }

            var flag = ReadUInt64FromBytes(response, 0);
            var requestId = ReadUInt64FromBytes(response, 8);
            if (flag == 0xffffffffffffffff)
            {
                if (response.Length < 34)
                {
                    throw CloseForUnexpectedMessage($"{operation} raw block header is too short");
                }

                requestId = ReadUInt64FromBytes(response, 26);
            }

            if (requestId != expectedRequestId)
            {
                throw CloseForUnexpectedMessage($"{operation} returned an unexpected request id");
            }
        }

        private TaskCompletionSource<WsMessage> AddTask(ulong reqId)
        {
            lock (_exitLock)
            {
                if (_exit)
                {
                    throw CreateRequestException(reqId, false,
                        new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                            "websocket connection is closed"));
                }

                if (_pendingRequestCount >= MaximumPendingRequests)
                {
                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_PENDING_REQUEST_LIMIT,
                        $"websocket pending request limit of {MaximumPendingRequests} has been reached");
                }

                if (_ignoredResponseIds.ContainsKey(reqId))
                {
                    throw new InvalidOperationException($"Request with reqId '0x{reqId:x}' already exists.");
                }

                var tcs = CreateTaskCompletionSource();
                _pendingRequestCount++;
                if (!_pendingRequests.TryAdd(reqId, tcs))
                {
                    _pendingRequestCount--;
                    throw new InvalidOperationException($"Request with reqId '0x{reqId:x}' already exists.");
                }

                return tcs;
            }
        }

        private bool TryRemoveTask(ulong reqId, out TaskCompletionSource<WsMessage> tcs)
        {
            lock (_exitLock)
            {
                if (!_pendingRequests.TryRemove(reqId, out tcs))
                {
                    return false;
                }

                _pendingRequestCount--;
                return true;
            }
        }

        private bool TryMarkIgnoredResponse(ulong reqId)
        {
            lock (_exitLock)
            {
                if (_exit)
                {
                    throw CreateRequestException(reqId, false,
                        new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                            "websocket connection is closed"));
                }

                if (_pendingRequests.ContainsKey(reqId) || _ignoredResponseIds.ContainsKey(reqId))
                {
                    throw new InvalidOperationException($"Request with reqId '0x{reqId:x}' already exists.");
                }

                return AddIgnoredResponseUnsafe(reqId);
            }
        }

        private bool AddIgnoredResponseUnsafe(ulong reqId)
        {
            if (_ignoredResponseIds.Count >= MaximumIgnoredResponseIds)
            {
                return false;
            }

            var node = _ignoredResponseOrder.AddLast(reqId);
            _ignoredResponseIds.Add(reqId, node);
            return true;
        }

        private static TDengineError CreateIgnoredResponseLimitError()
        {
            return new TDengineError((int)TDengineError.InternalErrorCode.WS_PENDING_REQUEST_LIMIT,
                $"websocket canceled-request tombstone limit of {MaximumIgnoredResponseIds} has been reached; " +
                "the connection must be closed to preserve response ordering safety");
        }

        private bool RemoveIgnoredResponse(ulong reqId)
        {
            lock (_exitLock)
            {
                if (!_ignoredResponseIds.TryGetValue(reqId, out var node))
                {
                    return false;
                }

                _ignoredResponseIds.Remove(reqId);
                _ignoredResponseOrder.Remove(node);
                return true;
            }
        }

        private bool TryCancelPendingRequest(ulong reqId, out TaskCompletionSource<WsMessage> tcs)
        {
            var closeConnection = false;
            lock (_exitLock)
            {
                if (!_pendingRequests.TryRemove(reqId, out tcs))
                {
                    return false;
                }

                _pendingRequestCount--;
                if (!_exit)
                {
                    closeConnection = !AddIgnoredResponseUnsafe(reqId);
                }
            }

            if (closeConnection)
            {
                DoClose(CreateIgnoredResponseLimitError());
            }

            return true;
        }

        private void RemovePendingRequestAfterUncertainSend(ulong reqId)
        {
            var closeConnection = false;
            lock (_exitLock)
            {
                if (!_pendingRequests.TryRemove(reqId, out _))
                {
                    return;
                }

                _pendingRequestCount--;
                if (!_exit)
                {
                    closeConnection = !AddIgnoredResponseUnsafe(reqId);
                }
            }

            if (closeConnection)
            {
                DoClose(CreateIgnoredResponseLimitError());
            }
        }

        private static TaskCompletionSource<WsMessage> CreateTaskCompletionSource()
        {
#if NET5_0_OR_GREATER || NETSTANDARD2_0_OR_GREATER
            return new TaskCompletionSource<WsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
#else
            return new TaskCompletionSource<WsMessage>();
#endif
        }

        private async Task<WsMessage> SendTextAndWaitAsync(string request, ulong reqId,
            CancellationToken cancellationToken)
        {
            var tcs = AddTask(reqId);
            try
            {
                try
                {
                    await SendTextMessageAsync(request, reqId, cancellationToken).ConfigureAwait(false);
                }
                catch (TDengineWebSocketRequestException e) when (e.RequestMayHaveBeenSent)
                {
                    RemovePendingRequestAfterUncertainSend(reqId);
                    throw;
                }

                return await WaitForResponseAsync(reqId, tcs, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TryRemoveTask(reqId, out _);
            }
        }

        private async Task<WsMessage> SendBinaryAndWaitAsync(byte[] request, int requestLength, ulong reqId,
            bool returnToPool, CancellationToken cancellationToken)
        {
            TaskCompletionSource<WsMessage> tcs = null;
            var bufferReturned = false;
            try
            {
                tcs = AddTask(reqId);
                try
                {
                    try
                    {
                        await SendBinaryMessageAsync(request, requestLength, reqId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (TDengineWebSocketRequestException e) when (e.RequestMayHaveBeenSent)
                    {
                        RemovePendingRequestAfterUncertainSend(reqId);
                        throw;
                    }
                }
                finally
                {
                    if (returnToPool)
                    {
                        ReturnPooledBuffer(request, requestLength);
                        bufferReturned = true;
                    }
                }

                return await WaitForResponseAsync(reqId, tcs, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (returnToPool && !bufferReturned)
                {
                    ReturnPooledBuffer(request, requestLength);
                }

                if (tcs != null)
                {
                    TryRemoveTask(reqId, out _);
                }
            }
        }

        private async Task<WsMessage> WaitForResponseAsync(ulong reqId, TaskCompletionSource<WsMessage> tcs,
            CancellationToken cancellationToken)
        {
            if (!TryEnterCloseOperation())
            {
                throw CreateRequestException(reqId, true,
                    new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                        "websocket connection is closed"));
            }

            try
            {
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closeToken))
                {
                    linkedCts.CancelAfter(_readTimeout);
                    var cancellationState = new PendingRequestCancellation(this, reqId);
                    using (linkedCts.Token.Register(state => ((PendingRequestCancellation)state).Cancel(),
                               cancellationState))
                    {
                        try
                        {
                            var response = await tcs.Task.ConfigureAwait(false);
                            if (response.Exception != null)
                            {
                                throw CreateRequestException(reqId, true, response.Exception);
                            }

                            return response;
                        }
                        catch (TaskCanceledException)
                        {
                            if (_closeToken.IsCancellationRequested)
                            {
                                throw CreateRequestException(reqId, true,
                                    new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                                        "websocket connection is closed"));
                            }

                            if (cancellationToken.IsCancellationRequested)
                            {
                                throw CreateRequestException(reqId, true,
                                    new OperationCanceledException(
                                        "websocket request was canceled after it may have reached the server",
                                        cancellationToken));
                            }

                            throw CreateRequestException(reqId, true,
                                new TimeoutException($"Request timed out. reqId: 0x{reqId:x}"));
                        }
                    }
                }
            }
            finally
            {
                ExitCloseOperation();
            }
        }

        protected static void ReturnPooledBuffer(byte[] buffer, int length)
        {
            if (buffer == null)
            {
                return;
            }

            var clearLength = Math.Max(0, Math.Min(length, buffer.Length));
            if (clearLength != 0)
            {
                Array.Clear(buffer, 0, clearLength);
            }

            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }

        private static TDengineWebSocketRequestException CreateRequestException(ulong reqId,
            bool requestMayHaveBeenSent, Exception innerException)
        {
            var code = requestMayHaveBeenSent
                ? (int)TDengineError.InternalErrorCode.WS_REQUEST_OUTCOME_UNKNOWN
                : innerException is TDengineError error
                    ? error.Code
                    : (int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED;
            var message = requestMayHaveBeenSent
                ? $"websocket request 0x{reqId:x} may have reached the server; its outcome is unknown"
                : $"websocket request 0x{reqId:x} was not sent";
            return new TDengineWebSocketRequestException(code, message, reqId, requestMayHaveBeenSent,
                innerException);
        }

        private async Task SendMessageAsync(ArraySegment<byte> data, WebSocketMessageType messageType, ulong reqId,
            CancellationToken cancellationToken)
        {
            if (!IsAvailable())
            {
                throw CreateRequestException(reqId, false,
                    new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                        "websocket connection is closed"));
            }

            if (!TryEnterCloseOperation())
            {
                throw CreateRequestException(reqId, false,
                    new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                        "websocket connection is closed"));
            }

            try
            {
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closeToken))
                {
                    linkedCts.CancelAfter(_writeTimeout);
                    var sendLockAcquired = false;
                    var sendStarted = false;
                    try
                    {
                        await _sendSemaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                        sendLockAcquired = true;
                        if (!IsAvailable())
                        {
                            throw CreateRequestException(reqId, false,
                                new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                                    "websocket connection is closed"));
                        }

                        sendStarted = true;
                        await _client.SendAsync(data, messageType, true, linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (TDengineWebSocketRequestException)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        if (!sendStarted)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                throw new OperationCanceledException("websocket send was canceled before it started",
                                    cancellationToken);
                            }

                            var preSendFailure = _closeToken.IsCancellationRequested
                                ? new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                                    "websocket connection is closed")
                                : new TDengineError((int)TDengineError.InternalErrorCode.WS_WRITE_TIMEOUT,
                                    "write timeout while waiting for the websocket send gate");
                            throw CreateRequestException(reqId, false, preSendFailure);
                        }

                        Exception sendFailure;
                        if (cancellationToken.IsCancellationRequested)
                        {
                            sendFailure = new OperationCanceledException(
                                "websocket send was canceled after it started", cancellationToken);
                        }
                        else if (_closeToken.IsCancellationRequested)
                        {
                            sendFailure = new TDengineError(
                                (int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                                "websocket connection is closed");
                        }
                        else
                        {
                            sendFailure = new TDengineError((int)TDengineError.InternalErrorCode.WS_WRITE_TIMEOUT,
                                "write message timeout");
                        }

                        DoClose(sendFailure);
                        throw CreateRequestException(reqId, true, sendFailure);
                    }
                    catch (Exception e)
                    {
                        if (!sendStarted)
                        {
                            throw CreateRequestException(reqId, false, e);
                        }

                        if (_closeToken.IsCancellationRequested)
                        {
                            e = new TDengineError((int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                                "websocket connection is closed");
                        }

                        DoClose(e);
                        throw CreateRequestException(reqId, true, e);
                    }
                    finally
                    {
                        if (sendLockAcquired)
                        {
                            _sendSemaphore.Release();
                        }
                    }
                }
            }
            finally
            {
                ExitCloseOperation();
            }
        }

        private async Task SendTextMessageAsync(string request, ulong reqId, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            cancellationToken.ThrowIfCancellationRequested();
            byte[] rented = null;
            var length = Utf8Encoding.GetByteCount(request);
            if (length > MaximumTextMessageSize)
            {
                throw CreateRequestException(reqId, false,
                    new ArgumentException($"WebSocket text message exceeds the {MaximumTextMessageSize} byte limit.",
                        nameof(request)));
            }

            try
            {
                rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
                Utf8Encoding.GetBytes(request, 0, request.Length, rented, 0);
                var data = new ArraySegment<byte>(rented, 0, length);
                await SendMessageAsync(data, WebSocketMessageType.Text, reqId, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (rented != null)
                {
                    ReturnPooledBuffer(rented, length);
                }
            }
        }

        private async Task SendBinaryMessageAsync(byte[] request, int requestLength, ulong reqId,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            cancellationToken.ThrowIfCancellationRequested();
            if (requestLength < 0 || requestLength > request.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(requestLength));
            }

            if (requestLength > MaximumMessageSize)
            {
                throw CreateRequestException(reqId, false,
                    new ArgumentOutOfRangeException(nameof(requestLength),
                        $"WebSocket binary message exceeds the {MaximumMessageSize} byte limit."));
            }

            var data = new ArraySegment<byte>(request, 0, requestLength);
            await SendMessageAsync(data, WebSocketMessageType.Binary, reqId, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task ReceiveLoop()
        {
            Exception exception = null;
            byte[] buffer = null;
            try
            {
                buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
                while (State == WebSocketState.Open)
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
            catch (OperationCanceledException) when (_closeToken.IsCancellationRequested)
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
                if (buffer != null)
                {
                    ReturnPooledBuffer(buffer, ReceiveBufferSize);
                }

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
                return new ReceivedMessage(CopyMessage(buffer, result.Count,
                    GetMaximumIncomingMessageSize(result.MessageType)), result.MessageType);
            }

            var messageType = result.MessageType;
            var maximumMessageSize = GetMaximumIncomingMessageSize(messageType);
            var messageLength = 0;
            byte[] messageBuffer = null;
            try
            {
                messageBuffer = ArrayPool<byte>.Shared.Rent(Math.Min(maximumMessageSize,
                    Math.Max(ReceiveBufferSize, result.Count)));
                AppendFrame(buffer, result.Count, ref messageBuffer, ref messageLength, maximumMessageSize);
                do
                {
                    result = await ReceiveFrameAsync(buffer).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return new ReceivedMessage(null, WebSocketMessageType.Close);
                    }

                    if (result.MessageType != messageType)
                    {
                        throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                            "websocket message type changed between fragments");
                    }

                    AppendFrame(buffer, result.Count, ref messageBuffer, ref messageLength, maximumMessageSize);
                } while (!result.EndOfMessage);

                return new ReceivedMessage(CopyMessage(messageBuffer, messageLength, maximumMessageSize),
                    messageType);
            }
            finally
            {
                if (messageBuffer != null)
                {
                    ReturnPooledBuffer(messageBuffer, messageLength);
                }
            }
        }

        private static int GetMaximumIncomingMessageSize(WebSocketMessageType messageType)
        {
            return messageType == WebSocketMessageType.Text ? MaximumTextMessageSize : MaximumMessageSize;
        }

        private static byte[] CopyMessage(byte[] source, int count, int maximumMessageSize)
        {
            if (count < 0 || count > maximumMessageSize)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    $"websocket message exceeds the {maximumMessageSize} byte limit");
            }

            if (count == 0)
            {
                return new byte[0];
            }

            var result = new byte[count];
            Buffer.BlockCopy(source, 0, result, 0, count);
            return result;
        }

        private static void AppendFrame(byte[] source, int count, ref byte[] destination, ref int length,
            int maximumMessageSize)
        {
            if (count < 0 || length > maximumMessageSize - count)
            {
                throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    $"websocket message exceeds the {maximumMessageSize} byte limit");
            }

            var requiredLength = length + count;
            if (requiredLength > destination.Length)
            {
                var doubledLength = destination.Length > maximumMessageSize / 2
                    ? maximumMessageSize
                    : destination.Length * 2;
                var newLength = Math.Max(requiredLength, doubledLength);
                var replacement = ArrayPool<byte>.Shared.Rent(newLength);
                Buffer.BlockCopy(destination, 0, replacement, 0, length);
                ReturnPooledBuffer(destination, length);
                destination = replacement;
            }

            Buffer.BlockCopy(source, 0, destination, length, count);
            length = requiredLength;
        }

        private async Task<WebSocketReceiveResult> ReceiveFrameAsync(byte[] buffer)
        {
            return await _client.ReceiveAsync(new ArraySegment<byte>(buffer, 0, ReceiveBufferSize),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        private bool IsExpectedLocalCloseException(Exception exception)
        {
            if (!_closeToken.IsCancellationRequested)
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

                    if (TryRemoveTask(reqId, out tcs))
                    {
                        tcs.TrySetResult(new WsMessage(bytes, messageType, null));
                    }
                    else if (!RemoveIgnoredResponse(reqId))
                    {
                        throw new TDengineError(
                            (int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                            $"receive binary response for unknown request id 0x{reqId:x}");
                    }

                    break;
                case WebSocketMessageType.Text:
                    WSDispatchResp resp;
                    try
                    {
                        resp = JsonConvert.DeserializeObject<WSDispatchResp>(Utf8Encoding.GetString(bytes));
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

                    if (IsOneWayResponse(resp.Action))
                    {
                        if (IsRequestIdTracked(resp.ReqId))
                        {
                            throw new TDengineError(
                                (int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                                $"receive cleanup response for a tracked request id 0x{resp.ReqId:x}");
                        }

                        RemoveIgnoredResponse(resp.ReqId);
                        break;
                    }

                    if (TryRemoveTask(resp.ReqId, out tcs))
                    {
                        tcs.TrySetResult(new WsMessage(bytes, messageType, null));
                    }
                    else if (RemoveIgnoredResponse(resp.ReqId))
                    {
                        ScheduleLateResponseCleanup(resp);
                    }
                    else
                    {
                        throw new TDengineError(
                            (int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                            $"receive text response for unknown request id 0x{resp.ReqId:x}");
                    }

                    break;
                default:
                    throw new TDengineError((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                        "receive unexpected message type");
            }
        }

        private void ScheduleLateResponseCleanup(WSDispatchResp response)
        {
            if (response.Code != 0)
            {
                return;
            }

            LateResponseResourceKind resourceKind;
            ulong resourceId;
            if ((string.Equals(response.Action, WSAction.Query, StringComparison.Ordinal) ||
                 string.Equals(response.Action, WSAction.BinaryQuery, StringComparison.Ordinal)) &&
                !response.IsUpdate)
            {
                resourceKind = LateResponseResourceKind.Result;
                resourceId = response.ResultId;
            }
            else if (string.Equals(response.Action, WSAction.STMT2Init, StringComparison.Ordinal))
            {
                resourceKind = LateResponseResourceKind.Statement;
                resourceId = response.StmtId;
            }
            else if (string.Equals(response.Action, WSAction.STMT2Result, StringComparison.Ordinal))
            {
                resourceKind = LateResponseResourceKind.Result;
                resourceId = response.ResultId;
            }
            else
            {
                return;
            }

            if (resourceId == 0)
            {
                return;
            }

            var cleanupTask = CleanupLateResponseResourceAsync(resourceKind, resourceId);
            ObserveFaultedTask(cleanupTask);
        }

        private async Task CleanupLateResponseResourceAsync(LateResponseResourceKind resourceKind,
            ulong resourceId)
        {
            try
            {
                var reqId = _GetReqId();
                if (resourceKind == LateResponseResourceKind.Result)
                {
                    await SendJsonAsync(WSAction.FreeResult, new WSFreeResultReq
                    {
                        ReqId = reqId,
                        ResultId = resourceId
                    }, reqId, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await SendJsonAsync(WSAction.STMT2Close, new WSStmt2CloseReq
                    {
                        ReqId = reqId,
                        StmtId = resourceId
                    }, reqId, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                DoClose(new TDengineError(
                    (int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE,
                    "failed to release a server resource returned after websocket request cancellation",
                    DescribeException(e)));
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

        private sealed class WSDispatchResp : WSBaseResp
        {
            [JsonProperty("id")] public ulong ResultId { get; set; }

            [JsonProperty("stmt_id")] public ulong StmtId { get; set; }

            [JsonProperty("is_update")] public bool IsUpdate { get; set; }
        }

        private enum LateResponseResourceKind
        {
            Result,
            Statement
        }

        private static bool IsOneWayResponse(string action)
        {
            return string.Equals(action, WSAction.FreeResult, StringComparison.Ordinal) ||
                   string.Equals(action, WSAction.STMT2Close, StringComparison.Ordinal);
        }

        private bool IsRequestIdTracked(ulong reqId)
        {
            lock (_exitLock)
            {
                return _pendingRequests.ContainsKey(reqId) || _ignoredResponseIds.ContainsKey(reqId);
            }
        }

        private sealed class PendingRequestCancellation
        {
            private readonly BaseConnectionAsync _connection;
            private readonly ulong _requestId;

            internal PendingRequestCancellation(BaseConnectionAsync connection, ulong requestId)
            {
                _connection = connection;
                _requestId = requestId;
            }

            internal void Cancel()
            {
                if (_connection.TryCancelPendingRequest(_requestId, out var pending))
                {
                    // The response may race with connection shutdown. The caller must
                    // always be released even when the late response no longer needs tracking.
                    pending.TrySetCanceled();
                }
            }
        }

        private bool TryEnterCloseOperation()
        {
            lock (_closeCtsLock)
            {
                if (_closeCtsDisposed != 0 || Volatile.Read(ref _disposed) == 1 || _exit)
                {
                    return false;
                }

                checked
                {
                    _closeOperationCount++;
                }

                return true;
            }
        }

        private void ExitCloseOperation()
        {
            var disposeCloseCts = false;
            lock (_closeCtsLock)
            {
                if (_closeOperationCount > 0)
                {
                    _closeOperationCount--;
                }

                if (_closeOperationCount == 0 && Volatile.Read(ref _disposed) == 1 && _closeCtsDisposed == 0)
                {
                    _closeCtsDisposed = 1;
                    disposeCloseCts = true;
                }

            }

            if (disposeCloseCts)
            {
                _closeCts.Dispose();
            }
        }

        private void TryDisposeCloseToken()
        {
            var disposeCloseCts = false;
            lock (_closeCtsLock)
            {
                if (_closeOperationCount == 0 && _closeCtsDisposed == 0)
                {
                    _closeCtsDisposed = 1;
                    disposeCloseCts = true;
                }
            }

            if (disposeCloseCts)
            {
                _closeCts.Dispose();
            }
        }

        private void CancelCloseToken()
        {
            try
            {
                _closeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception e)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "WebSocket close cancellation callback failed: " + e.GetType().Name);
            }
        }

        private void AbortClient()
        {
            try
            {
                _client.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (WebSocketException)
            {
            }
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

            if (e == null)
            {
                CancelCloseToken();
            }

            foreach (var kvp in _pendingRequests)
            {
                if (!TryRemoveTask(kvp.Key, out var pending))
                {
                    continue;
                }

                if (e != null)
                {
                    pending.TrySetResult(new WsMessage(null, WebSocketMessageType.Close, e));
                }
                else
                {
                    pending.TrySetCanceled();
                }
            }

            if (e != null)
            {
                CancelCloseToken();
            }

            return true;
        }

        private void DoClose(Exception e = null)
        {
            if (!BeginClose(e)) return;
            AbortClient();
            DisposeClient();
        }

        private async Task CloseClientOutputAsync()
        {
            var acquiredSendLock = false;
            try
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    return;
                }

                acquiredSendLock = await _sendSemaphore.WaitAsync(CloseTimeout).ConfigureAwait(false);
                if (!acquiredSendLock)
                {
                    AbortClient();
                    return;
                }

                var state = State;
                if (state == WebSocketState.Open || state == WebSocketState.CloseReceived)
                {
                    var closeTask = _client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty,
                        CancellationToken.None);
                    if (!await WaitTaskAsync(closeTask, CloseTimeout).ConfigureAwait(false))
                    {
                        AbortClient();
                        ObserveFaultedTask(closeTask);
                    }
                }
                else if (state != WebSocketState.Closed && state != WebSocketState.CloseSent)
                {
                    AbortClient();
                }
            }
            catch
            {
                AbortClient();
            }
            finally
            {
                if (acquiredSendLock)
                {
                    _sendSemaphore.Release();
                }
            }
        }

        private static async Task<bool> WaitTaskAsync(Task task, TimeSpan timeout)
        {
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return true;
            }

            using (var timeoutCts = new CancellationTokenSource())
            {
                var delayTask = Task.Delay(timeout, timeoutCts.Token);
                if (await Task.WhenAny(task, delayTask).ConfigureAwait(false) != task)
                {
                    return false;
                }

                timeoutCts.Cancel();
                await task.ConfigureAwait(false);
                return true;
            }
        }

        private static void ObserveFaultedTask(Task task)
        {
            task.ContinueWith(t => GC.KeepAlive(t.Exception), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task WaitReceiveLoopAsync()
        {
            var task = _receiveLoopTask;
            if (task == null)
            {
                return;
            }

            if (task.IsCompleted)
            {
                ObserveFaultedTask(task);
                return;
            }

            if (!await WaitTaskAsync(task, CloseTimeout).ConfigureAwait(false))
            {
                AbortClient();
                if (!await WaitTaskAsync(task, CloseTimeout).ConfigureAwait(false))
                {
                    ObserveFaultedTask(task);
                    return;
                }
            }

            ObserveFaultedTask(task);
        }

        private void DisposeClient()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                TryDisposeCloseToken();
                return;
            }

            try
            {
                _client.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                lock (_exitLock)
                {
                    _ignoredResponseIds.Clear();
                    _ignoredResponseOrder.Clear();
                }
                TryDisposeCloseToken();
            }
        }

        public async Task CloseAsync()
        {
            Task closeTask;
            lock (_closeTaskLock)
            {
                if (_closeTask == null)
                {
                    _closeTask = CloseOnceAsync();
                }

                closeTask = _closeTask;
            }

            await closeTask.ConfigureAwait(false);
        }

        internal async Task InvalidateAsync(Exception reason = null)
        {
            reason = reason ?? new TDengineError(
                (int)TDengineError.InternalErrorCode.WS_CONNECTION_CLOSED,
                "websocket connection was invalidated");
            BeginClose(reason);
            AbortClient();

            Task closeTask;
            lock (_closeTaskLock)
            {
                if (_closeTask == null)
                {
                    _closeTask = FinalizeInvalidatedConnectionAsync();
                }

                closeTask = _closeTask;
            }

            await closeTask.ConfigureAwait(false);
        }

        private async Task FinalizeInvalidatedConnectionAsync()
        {
            AbortClient();
            await WaitReceiveLoopAsync().ConfigureAwait(false);
            DisposeClient();
        }

        private async Task CloseOnceAsync()
        {
            if (BeginClose())
            {
                await CloseClientOutputAsync().ConfigureAwait(false);
            }

            // A receive or send failure may have started closing the socket before
            // CloseAsync was called. Closing still owns the receive-loop drain contract.
            await WaitReceiveLoopAsync().ConfigureAwait(false);
            DisposeClient();
        }

        public bool IsAvailable(Exception e = null)
        {
            lock (_exitLock)
            {
                if (_exit) return false;
            }

            if (State != WebSocketState.Open)
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
