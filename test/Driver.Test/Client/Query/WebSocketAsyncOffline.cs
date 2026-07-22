using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TDengine.Driver;
using TDengine.Driver.Client;
using TDengine.Driver.Client.Websocket;
using TDengine.Driver.Impl.WebSocketMethods;
using TDengine.Driver.Impl.WebSocketMethods.Protocol;
using Test.Fixture;
using Xunit;

namespace Driver.Test.Client.Query
{
    [Collection("WebSocket async collection")]
    public sealed class WebSocketAsyncOffline
    {
        [Fact]
        public async Task OutOfOrderResponsesAreDispatchedByRequestId()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var first = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var second = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var firstId = WebSocketTestProtocol.GetBinaryRequestId(first.Bytes);
                var secondId = WebSocketTestProtocol.GetBinaryRequestId(second.Bytes);

                await SendUpdateResponseAsync(socket, secondId, 22, false, token).ConfigureAwait(false);
                await SendUpdateResponseAsync(socket, firstId, 11, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var firstTask = connection.BinaryQueryAsync("select first", 101);
            var secondTask = connection.BinaryQueryAsync("select second", 202);

            var firstResponse = await firstTask.ConfigureAwait(false);
            var secondResponse = await secondTask.ConfigureAwait(false);

            Assert.Equal(101UL, firstResponse.ReqId);
            Assert.Equal(11, firstResponse.AffectedRows);
            Assert.Equal(202UL, secondResponse.ReqId);
            Assert.Equal(22, secondResponse.AffectedRows);
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task FragmentedTextAndBinaryResponsesAreReassembled()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var query = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var queryId = WebSocketTestProtocol.GetBinaryRequestId(query.Bytes);
                await SendUpdateResponseAsync(socket, queryId, 7, true, token).ConfigureAwait(false);

                var fetch = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var fetchId = WebSocketTestProtocol.GetBinaryRequestId(fetch.Bytes);
                var response = new byte[24];
                BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(8), fetchId);
                BinaryPrimitives.WriteInt64LittleEndian(response.AsSpan(16), 0x12345678);
                await WebSocketTestProtocol.SendBinaryAsync(socket, response, true, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var queryResponse = await connection.BinaryQueryAsync("select fragmented", 303)
                .ConfigureAwait(false);
            Assert.Equal(7, queryResponse.AffectedRows);

            var binaryResponse = await connection.FetchRawBlockBinaryAsync(42).ConfigureAwait(false);
            Assert.Equal(24, binaryResponse.Length);
            Assert.Equal(0x12345678, BinaryPrimitives.ReadInt64LittleEndian(binaryResponse.AsSpan(16)));
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task CancellationAfterSendMarksOutcomeUnknownAndLateResponseIsIgnored()
        {
            var requestReceived = NewCompletionSource<bool>();
            var releaseLateResponse = NewCompletionSource<bool>();
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var canceledRequest = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var canceledId = WebSocketTestProtocol.GetBinaryRequestId(canceledRequest.Bytes);
                requestReceived.TrySetResult(true);
                await releaseLateResponse.Task.ConfigureAwait(false);
                await SendUpdateResponseAsync(socket, canceledId, 1, false, token).ConfigureAwait(false);

                var nextRequest = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var nextId = WebSocketTestProtocol.GetBinaryRequestId(nextRequest.Bytes);
                await SendUpdateResponseAsync(socket, nextId, 2, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            var canceledTask = connection.BinaryQueryAsync("insert canceled", 401, cancellation.Token);
            await requestReceived.Task.ConfigureAwait(false);
            cancellation.Cancel();

            var exception = await Assert.ThrowsAsync<TDengineWebSocketRequestException>(
                () => canceledTask).ConfigureAwait(false);
            Assert.True(exception.RequestMayHaveBeenSent);
            Assert.Equal(401UL, exception.RequestId);
            Assert.True(connection.IsAvailable());

            releaseLateResponse.TrySetResult(true);
            var nextResponse = await connection.BinaryQueryAsync("select still_usable", 402)
                .ConfigureAwait(false);
            Assert.Equal(2, nextResponse.AffectedRows);
            Assert.True(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task CanceledRequestIdCannotBeReusedUntilLateResponseIsConsumed()
        {
            var requestReceived = NewCompletionSource<bool>();
            var releaseLateResponse = NewCompletionSource<bool>();
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var canceledRequest = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var canceledId = WebSocketTestProtocol.GetBinaryRequestId(canceledRequest.Bytes);
                requestReceived.TrySetResult(true);
                await releaseLateResponse.Task.ConfigureAwait(false);
                await SendUpdateResponseAsync(socket, canceledId, 1, false, token).ConfigureAwait(false);

                var barrierRequest = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var barrierId = WebSocketTestProtocol.GetBinaryRequestId(barrierRequest.Bytes);
                await SendUpdateResponseAsync(socket, barrierId, 2, false, token).ConfigureAwait(false);

                var reusedRequest = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var reusedId = WebSocketTestProtocol.GetBinaryRequestId(reusedRequest.Bytes);
                await SendUpdateResponseAsync(socket, reusedId, 3, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            var canceledTask = connection.BinaryQueryAsync("select canceled", 411, cancellation.Token);
            await requestReceived.Task.ConfigureAwait(false);
            cancellation.Cancel();
            await Assert.ThrowsAsync<TDengineWebSocketRequestException>(() => canceledTask)
                .ConfigureAwait(false);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => connection.BinaryQueryAsync("select premature reuse", 411)).ConfigureAwait(false);

            releaseLateResponse.TrySetResult(true);
            Assert.Equal(2, (await connection.BinaryQueryAsync("select barrier", 412)
                .ConfigureAwait(false)).AffectedRows);
            Assert.Equal(3, (await connection.BinaryQueryAsync("select reused", 411)
                .ConfigureAwait(false)).AffectedRows);
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task CanceledQueryLateResultIsFreedAndConnectionRemainsUsable()
        {
            var requestReceived = NewCompletionSource<bool>();
            var releaseLateResponse = NewCompletionSource<bool>();
            var cleanupObserved = NewCompletionSource<bool>();
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var query = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var requestId = WebSocketTestProtocol.GetBinaryRequestId(query.Bytes);
                requestReceived.TrySetResult(true);
                await releaseLateResponse.Task.ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.BinaryQuery, requestId,
                    new JObject
                    {
                        ["is_update"] = false,
                        ["id"] = 9001UL,
                        ["fields_count"] = 0
                    }, false, token).ConfigureAwait(false);

                var cleanup = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                Assert.Equal(WSAction.FreeResult, WebSocketTestProtocol.GetAction(cleanup));
                Assert.Equal(9001UL, cleanup["args"]?["id"]?.Value<ulong>());
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.FreeResult,
                    WebSocketTestProtocol.GetRequestId(cleanup), new JObject(), false, token)
                    .ConfigureAwait(false);
                cleanupObserved.TrySetResult(true);

                var validation = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendVersionResponseAsync(socket, validation, token)
                    .ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            var queryTask = connection.BinaryQueryAsync("select canceled_result", 421, cancellation.Token);
            await requestReceived.Task.ConfigureAwait(false);
            cancellation.Cancel();
            await Assert.ThrowsAsync<TDengineWebSocketRequestException>(() => queryTask).ConfigureAwait(false);

            releaseLateResponse.TrySetResult(true);
            await cleanupObserved.Task.ConfigureAwait(false);
            await connection.ValidateConnectionAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.True(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task CanceledStatementInitLateResponseClosesStatementAndConnectionRemainsUsable()
        {
            var requestReceived = NewCompletionSource<bool>();
            var releaseLateResponse = NewCompletionSource<bool>();
            var cleanupObserved = NewCompletionSource<bool>();
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var init = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                Assert.Equal(WSAction.STMT2Init, WebSocketTestProtocol.GetAction(init));
                requestReceived.TrySetResult(true);
                await releaseLateResponse.Task.ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Init,
                    WebSocketTestProtocol.GetRequestId(init), new JObject { ["stmt_id"] = 9002UL }, false, token)
                    .ConfigureAwait(false);

                var cleanup = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                Assert.Equal(WSAction.STMT2Close, WebSocketTestProtocol.GetAction(cleanup));
                Assert.Equal(9002UL, cleanup["args"]?["stmt_id"]?.Value<ulong>());
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Close,
                    WebSocketTestProtocol.GetRequestId(cleanup), new JObject(), false, token)
                    .ConfigureAwait(false);
                cleanupObserved.TrySetResult(true);

                var validation = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendVersionResponseAsync(socket, validation, token)
                    .ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            var initTask = connection.Stmt2InitAsync(422, cancellation.Token);
            await requestReceived.Task.ConfigureAwait(false);
            cancellation.Cancel();
            await Assert.ThrowsAsync<TDengineWebSocketRequestException>(() => initTask).ConfigureAwait(false);

            releaseLateResponse.TrySetResult(true);
            await cleanupObserved.Task.ConfigureAwait(false);
            await connection.ValidateConnectionAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.True(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task CanceledStatementResultLateResponseFreesResultAndConnectionRemainsUsable()
        {
            var requestReceived = NewCompletionSource<bool>();
            var releaseLateResponse = NewCompletionSource<bool>();
            var cleanupObserved = NewCompletionSource<bool>();
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var result = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                Assert.Equal(WSAction.STMT2Result, WebSocketTestProtocol.GetAction(result));
                requestReceived.TrySetResult(true);
                await releaseLateResponse.Task.ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Result,
                    WebSocketTestProtocol.GetRequestId(result), new JObject
                    {
                        ["stmt_id"] = 9003UL,
                        ["id"] = 9004UL,
                        ["fields_count"] = 0
                    }, false, token).ConfigureAwait(false);

                var cleanup = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                Assert.Equal(WSAction.FreeResult, WebSocketTestProtocol.GetAction(cleanup));
                Assert.Equal(9004UL, cleanup["args"]?["id"]?.Value<ulong>());
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.FreeResult,
                    WebSocketTestProtocol.GetRequestId(cleanup), new JObject(), false, token)
                    .ConfigureAwait(false);
                cleanupObserved.TrySetResult(true);

                var validation = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendVersionResponseAsync(socket, validation, token)
                    .ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            var resultTask = connection.Stmt2UseResultAsync(9003, cancellation.Token);
            await requestReceived.Task.ConfigureAwait(false);
            cancellation.Cancel();
            await Assert.ThrowsAsync<TDengineWebSocketRequestException>(() => resultTask).ConfigureAwait(false);

            releaseLateResponse.TrySetResult(true);
            await cleanupObserved.Task.ConfigureAwait(false);
            await connection.ValidateConnectionAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.True(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task LateResponseCleanupFailureInvalidatesConnection()
        {
            var requestReceived = NewCompletionSource<bool>();
            var releaseLateResponse = NewCompletionSource<bool>();
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var query = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var requestId = WebSocketTestProtocol.GetBinaryRequestId(query.Bytes);
                requestReceived.TrySetResult(true);
                await releaseLateResponse.Task.ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.BinaryQuery, requestId,
                    new JObject
                    {
                        ["is_update"] = false,
                        ["id"] = 9005UL,
                        ["fields_count"] = 0
                    }, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port, TimeSpan.FromMilliseconds(80));
            await connection.ConnectAsync().ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            var queryTask = connection.BinaryQueryAsync("select cleanup_failure", 423, cancellation.Token);
            await requestReceived.Task.ConfigureAwait(false);
            cancellation.Cancel();
            await Assert.ThrowsAsync<TDengineWebSocketRequestException>(() => queryTask).ConfigureAwait(false);

            var sendGate = GetSendGate(connection);
            await sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                releaseLateResponse.TrySetResult(true);
                var stopwatch = Stopwatch.StartNew();
                while (connection.IsAvailable() && stopwatch.Elapsed < TimeSpan.FromSeconds(2))
                {
                    await Task.Delay(10).ConfigureAwait(false);
                }

                Assert.False(connection.IsAvailable());
            }
            finally
            {
                sendGate.Release();
            }

            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task SendGateTimeoutDoesNotCloseHealthyConnection()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var request = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var requestId = WebSocketTestProtocol.GetBinaryRequestId(request.Bytes);
                await SendUpdateResponseAsync(socket, requestId, 9, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port, TimeSpan.FromMilliseconds(80));
            await connection.ConnectAsync().ConfigureAwait(false);
            var field = typeof(BaseConnectionAsync).GetField("_sendSemaphore",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var sendGate = Assert.IsType<SemaphoreSlim>(field!.GetValue(connection));
            await sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var exception = await Assert.ThrowsAsync<TDengineWebSocketRequestException>(
                    () => connection.BinaryQueryAsync("select blocked", 501)).ConfigureAwait(false);
                Assert.False(exception.RequestMayHaveBeenSent);
                var error = Assert.IsType<TDengineError>(exception.InnerException);
                Assert.Equal((int)TDengineError.InternalErrorCode.WS_WRITE_TIMEOUT, error.Code);
                Assert.True(connection.IsAvailable());
            }
            finally
            {
                sendGate.Release();
            }

            var response = await connection.BinaryQueryAsync("select recovered", 502).ConfigureAwait(false);
            Assert.Equal(9, response.AffectedRows);
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task ClientUsesImmutableBuilderSnapshot()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var builder = CreateBuilder(server.Port);
            var client = new WSClientAsync(builder);
            builder.Host = "203.0.113.10";
            builder.Port = 1;

            await client.ConnectAsync().ConfigureAwait(false);
            Assert.True(client.ConnectionAvailable());
            await client.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task CloseAsyncInterruptsStalledHttpUpgrade()
        {
            await using var server = new SingleConnectionTcpServer(
                (_, token) => Task.Delay(Timeout.Infinite, token));
            var connection = CreateConnection(server.Port, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
            var connectTask = connection.ConnectAsync();
            await server.WaitForAcceptedConnectionAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            var closeTask = connection.CloseAsync();
            await AssertCompletesAsync(closeTask, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Assert.ThrowsAnyAsync<Exception>(() => connectTask).ConfigureAwait(false);
            Assert.False(connection.IsAvailable());
        }

        [Fact]
        public async Task ConnectFailureDoesNotLeakTokenInExceptionText()
        {
            await using var server = new SingleConnectionTcpServer(async (stream, token) =>
            {
                await ReadHttpHeadersAsync(stream, token).ConfigureAwait(false);
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                await stream.WriteAsync(response, 0, response.Length, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            });

            const string secret = "offline-super-secret-token";
            var builder = CreateBuilder(server.Port);
            builder.Token = secret;
            var client = new WSClientAsync(builder);
            var exception = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync())
                .ConfigureAwait(false);

            Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("?token=", exception.ToString(), StringComparison.OrdinalIgnoreCase);
            await client.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task PrepareFailureClearsPreviouslyPreparedState()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var init = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                var initId = WebSocketTestProtocol.GetRequestId(init);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Init, initId,
                    new JObject { ["stmt_id"] = 1UL }, false, token).ConfigureAwait(false);

                var prepare = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                var prepareId = WebSocketTestProtocol.GetRequestId(prepare);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Prepare, prepareId,
                    new JObject
                    {
                        ["stmt_id"] = 1UL,
                        ["is_insert"] = false,
                        ["fields_count"] = 0
                    }, false, token).ConfigureAwait(false);

                var failedPrepare = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token)
                    .ConfigureAwait(false);
                var failedPrepareId = WebSocketTestProtocol.GetRequestId(failedPrepare);
                await WebSocketTestProtocol.SendErrorResponseAsync(socket, WSAction.STMT2Prepare,
                    failedPrepareId, 0x123, "prepare failed", token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var client = new WSClientAsync(CreateBuilder(server.Port));
            await client.ConnectAsync().ConfigureAwait(false);
            var stmt = await client.StmtInitAsync(601).ConfigureAwait(false);
            await stmt.PrepareAsync("select ?").ConfigureAwait(false);
            Assert.False(stmt.IsInsert());

            await Assert.ThrowsAsync<TDengineError>(() => stmt.PrepareAsync("bad sql"))
                .ConfigureAwait(false);
            Assert.Throws<InvalidOperationException>(() => stmt.IsInsert());
            await stmt.DisposeAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task InitialZeroStatementIdIsRejectedWithoutSendingClose()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var init = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                var initId = WebSocketTestProtocol.GetRequestId(init);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Init, initId,
                    new JObject { ["stmt_id"] = 0UL }, false, token).ConfigureAwait(false);

                try
                {
                    var next = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                    if (next.MessageType == WebSocketMessageType.Text)
                    {
                        var request = JObject.Parse(Encoding.UTF8.GetString(next.Bytes));
                        if (string.Equals(WebSocketTestProtocol.GetAction(request), WSAction.STMT2Close,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("A zero statement id must not be closed on the server.");
                        }
                    }
                }
                catch (WebSocketException)
                {
                }
                catch (IOException)
                {
                }

                await TryCloseOutputAsync(socket).ConfigureAwait(false);
            });

            var client = new WSClientAsync(CreateBuilder(server.Port));
            await client.ConnectAsync().ConfigureAwait(false);
            var exception = await Assert.ThrowsAsync<TDengineError>(() => client.StmtInitAsync(602))
                .ConfigureAwait(false);
            Assert.Equal((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE, exception.Code);
            Assert.False(client.ConnectionAvailable());
            await client.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task StatementBindSendsRequestedLengthInsteadOfRentedCapacity()
        {
            var receivedLength = NewCompletionSource<int>();
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var bind = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                receivedLength.TrySetResult(bind.Bytes.Length);
                var requestId = WebSocketTestProtocol.GetBinaryRequestId(bind.Bytes);
                await WebSocketTestProtocol.SendResponseAsync(socket, "stmt2_bind", requestId,
                    new JObject { ["stmt_id"] = 7UL }, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(1024);
            try
            {
                await connection.Stmt2BindAsync(7, buffer, 37).ConfigureAwait(false);
                Assert.Equal(37, await receivedLength.Task.ConfigureAwait(false));
            }
            finally
            {
                Array.Clear(buffer, 0, buffer.Length);
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public void PooledStatementBindPayloadMatchesNonPooledPayloadAfterDirtyBufferReuse()
        {
            using var stmt = new BindPayloadTestStmt();
            stmt.Initialize();
            stmt.BindRow(new object[] { 123 });
            stmt.AddBatch();
            stmt.Exec();
            var expected = stmt.LastPayload;
            Assert.NotNull(expected);

            stmt.Initialize();
            stmt.BindRow(new object[] { 123 });
            stmt.AddBatch();

            var dirty = ArrayPool<byte>.Shared.Rent(expected!.Length);
            for (var i = 0; i < dirty.Length; i++)
            {
                dirty[i] = 0xa5;
            }

            ArrayPool<byte>.Shared.Return(dirty, clearArray: false);
            var actual = stmt.CapturePooledPayload();
            Assert.Equal(expected, actual);

            var cleanup = ArrayPool<byte>.Shared.Rent(expected.Length);
            Array.Clear(cleanup, 0, cleanup.Length);
            ArrayPool<byte>.Shared.Return(cleanup, clearArray: false);
        }

        [Fact]
        public void StatementObjectListCacheHasAggregateCapacityLimit()
        {
            const int columnCount = 32;
            const int rowCount = 2049;
            using var stmt = new CacheRetentionTestStmt();
            stmt.Initialize(columnCount);
            var values = new int[rowCount];
            var columns = new Array[columnCount];
            for (var i = 0; i < columns.Length; i++)
            {
                columns[i] = values;
            }

            stmt.BindColumn(stmt.GetColFields(), columns);
            stmt.AddBatch();
            stmt.Exec();

            var queueField = typeof(AbstractStmt).GetField("_objectListQueue",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var capacityField = typeof(AbstractStmt).GetField("_cachedObjectListCapacity",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(queueField);
            Assert.NotNull(capacityField);
            var queue = Assert.IsType<Queue<List<object>>>(queueField!.GetValue(stmt));
            var retainedCapacity = 0;
            foreach (var list in queue)
            {
                retainedCapacity += list.Capacity;
            }

            Assert.InRange(retainedCapacity, 1, 65536);
            Assert.Equal(retainedCapacity, Assert.IsType<int>(capacityField!.GetValue(stmt)));
            Assert.True(queue.Count < columnCount);
        }

        [Fact]
        public async Task SynchronousFetchFailureDoesNotLeaveStaleRowsState()
        {
            var attempts = 0;
            var failure = new InvalidOperationException("synchronous fetch failure");
            var rows = new WSRowsAsync(1, CreateSingleIntMetadata(),
                WSRowsAsync.TestFetchRawBlockAccessor.Instance,
                (_, _) =>
                {
                    Interlocked.Increment(ref attempts);
                    throw failure;
                }, TimeZoneInfo.Utc);

            var first = await Assert.ThrowsAsync<InvalidOperationException>(() => rows.ReadAsync())
                .ConfigureAwait(false);
            var second = await Assert.ThrowsAsync<InvalidOperationException>(() => rows.ReadAsync())
                .ConfigureAwait(false);
            Assert.Same(failure, first);
            Assert.Same(failure, second);
            Assert.Equal(2, attempts);
            await rows.DisposeAsync().ConfigureAwait(false);
        }

        [Theory]
        [InlineData((int)TDengineDataType.TSDB_DATA_TYPE_DECIMAL64, 1, 0, true)]
        [InlineData((int)TDengineDataType.TSDB_DATA_TYPE_DECIMAL64, 18, 18, true)]
        [InlineData((int)TDengineDataType.TSDB_DATA_TYPE_DECIMAL, 19, 0, true)]
        [InlineData((int)TDengineDataType.TSDB_DATA_TYPE_DECIMAL, 38, 38, true)]
        [InlineData((int)TDengineDataType.TSDB_DATA_TYPE_DECIMAL64, 0, 0, false)]
        [InlineData((int)TDengineDataType.TSDB_DATA_TYPE_DECIMAL64, 19, 0, false)]
        [InlineData((int)TDengineDataType.TSDB_DATA_TYPE_DECIMAL, 18, 0, false)]
        [InlineData((int)TDengineDataType.TSDB_DATA_TYPE_DECIMAL, 39, 0, false)]
        [InlineData((int)TDengineDataType.TSDB_DATA_TYPE_DECIMAL, 20, 21, false)]
        public void DecimalMetadataMatchesTdengineStorageType(int dataType, int precision, int scale,
            bool valid)
        {
            var metadata = CreateSingleIntMetadata();
            metadata.FieldsTypes[0] = (byte)dataType;
            metadata.FieldsLengths[0] = dataType == (int)TDengineDataType.TSDB_DATA_TYPE_DECIMAL64
                ? sizeof(long)
                : 16;
            metadata.FieldsPrecisions[0] = (byte)precision;
            metadata.FieldsScales[0] = (byte)scale;

            if (!valid)
            {
                Assert.Throws<InvalidDataException>(() => CreateOfflineRows(metadata));
                return;
            }

            using var rows = CreateOfflineRows(metadata);
            Assert.Equal(precision, rows.GetFieldPrecision(0));
            Assert.Equal(scale, rows.GetFieldScale(0));
        }

        [Fact]
        public void DecimalMetadataRequiresPrecisionAndScale()
        {
            var metadata = CreateSingleIntMetadata();
            metadata.FieldsTypes[0] = (byte)TDengineDataType.TSDB_DATA_TYPE_DECIMAL64;
            metadata.FieldsLengths[0] = sizeof(long);
            metadata.FieldsPrecisions = null;

            Assert.Throws<InvalidDataException>(() => CreateOfflineRows(metadata));

            metadata.FieldsPrecisions = new byte[] { 18 };
            metadata.FieldsScales = null;
            Assert.Throws<InvalidDataException>(() => CreateOfflineRows(metadata));
        }

        [Fact]
        public async Task FetchErrorMessageUsesStrictUtf8Decoding()
        {
            var response = CreateFetchResponse(1, 0x123, new byte[] { 0xc3, 0x28 }, true);
            var rows = CreateOfflineRows(CreateSingleIntMetadata(), response);

            await Assert.ThrowsAsync<DecoderFallbackException>(() => rows.ReadAsync()).ConfigureAwait(false);
            await rows.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task SuccessfulFetchDoesNotDecodeUnusedMessageBytes()
        {
            var response = CreateFetchResponse(1, 0, new byte[] { 0xc3, 0x28 }, true);
            var rows = CreateOfflineRows(CreateSingleIntMetadata(), response);

            Assert.False(await rows.ReadAsync().ConfigureAwait(false));
            await rows.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task CompletedFetchRejectsTrailingBytes()
        {
            var response = CreateCompletedFetchResponse(1);
            Array.Resize(ref response, response.Length + 1);
            var rows = CreateOfflineRows(CreateSingleIntMetadata(), response);

            await Assert.ThrowsAsync<InvalidDataException>(() => rows.ReadAsync()).ConfigureAwait(false);
            await rows.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task FetchRejectsMismatchedResultId()
        {
            var rows = CreateOfflineRows(CreateSingleIntMetadata(), CreateCompletedFetchResponse(2));

            await Assert.ThrowsAsync<InvalidDataException>(() => rows.ReadAsync()).ConfigureAwait(false);
            await rows.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task ValueGettersAreRejectedWhileReadAsyncIsInProgress()
        {
            var fetch = NewCompletionSource<byte[]>();
            var rows = new WSRowsAsync(1, CreateSingleIntMetadata(),
                WSRowsAsync.TestFetchRawBlockAccessor.Instance,
                (_, _) => fetch.Task, TimeZoneInfo.Utc);

            var readTask = rows.ReadAsync();
            Assert.Throws<InvalidOperationException>(() => rows.GetInt32(0));
            fetch.TrySetResult(CreateCompletedFetchResponse(1));
            Assert.False(await readTask.ConfigureAwait(false));
            await rows.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task MalformedBinaryResponseFailsAllPendingRequestsWithRootCause()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendBinaryAsync(socket, new byte[8], false, token)
                    .ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var firstTask = connection.BinaryQueryAsync("select malformed_one", 701);
            var secondTask = connection.BinaryQueryAsync("select malformed_two", 702);

            var first = await Assert.ThrowsAsync<TDengineWebSocketRequestException>(() => firstTask)
                .ConfigureAwait(false);
            var second = await Assert.ThrowsAsync<TDengineWebSocketRequestException>(() => secondTask)
                .ConfigureAwait(false);
            AssertRootCauseIsUnexpectedMessage(first);
            AssertRootCauseIsUnexpectedMessage(second);
            Assert.False(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task TmqPreSendCancellationLeavesConnectionUsable()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                var version = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendVersionResponseAsync(socket, version, token).ConfigureAwait(false);
                var poll = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                var requestId = WebSocketTestProtocol.GetRequestId(poll);
                if (requestId != 802)
                {
                    throw new InvalidDataException("The pre-canceled TMQ request reached the server.");
                }

                await WebSocketTestProtocol.SendResponseAsync(socket, WSTMQAction.TMQPoll, requestId,
                    new JObject { ["have_message"] = false }, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var options = new TMQOptions(new Dictionary<string, string>
            {
                ["td.connect.ip"] = "127.0.0.1",
                ["td.connect.port"] = server.Port.ToString(),
                ["useSSL"] = "false"
            });
            var connection = new TMQConnectionAsync(options, TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            await connection.ConnectAsync().ConfigureAwait(false);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => connection.PollAsync(801, 1, cancellation.Token)).ConfigureAwait(false);

            var response = await connection.PollAsync(802, 1, CancellationToken.None).ConfigureAwait(false);
            Assert.False(response.HaveMessage);
            Assert.True(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task TmqRawDataPollCanBeFetchedAsRawBytes()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                var version = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendVersionResponseAsync(socket, version, token).ConfigureAwait(false);

                var poll = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSTMQAction.TMQPoll,
                    WebSocketTestProtocol.GetRequestId(poll), new JObject
                    {
                        ["have_message"] = true,
                        ["topic"] = "raw_topic",
                        ["database"] = "raw_db",
                        ["vgroup_id"] = 0,
                        ["message_type"] = (int)TMQ_RES.TMQ_RES_RAWDATA,
                        ["message_id"] = 99UL,
                        ["offset"] = 7L
                    }, false, token).ConfigureAwait(false);

                var fetchRaw = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                Assert.Equal(WSTMQAction.TMQFetchRaw, WebSocketTestProtocol.GetAction(fetchRaw));
                Assert.Equal(99UL, fetchRaw["args"]?["message_id"]?.Value<ulong>());
                var fetchRequestId = WebSocketTestProtocol.GetRequestId(fetchRaw);
                var rawResponse = new byte[34];
                BinaryPrimitives.WriteUInt64LittleEndian(rawResponse.AsSpan(0), ulong.MaxValue);
                BinaryPrimitives.WriteUInt64LittleEndian(rawResponse.AsSpan(26), fetchRequestId);
                await WebSocketTestProtocol.SendBinaryAsync(socket, rawResponse, false, token)
                    .ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var options = new TMQOptions(new Dictionary<string, string>
            {
                ["td.connect.ip"] = "127.0.0.1",
                ["td.connect.port"] = server.Port.ToString(),
                ["useSSL"] = "false"
            });
            var connection = new TMQConnectionAsync(options, TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            await connection.ConnectAsync().ConfigureAwait(false);

            var pollResponse = await connection.PollAsync(803, 1, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.Equal(TMQ_RES.TMQ_RES_RAWDATA, (TMQ_RES)pollResponse.MessageType);
            var raw = await connection.FetchRawBlockAsync(804, pollResponse.MessageId, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.Equal(34, raw.Length);
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task TmqPollRejectsMalformedMessageMetadata()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                var version = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendVersionResponseAsync(socket, version, token).ConfigureAwait(false);
                var poll = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSTMQAction.TMQPoll,
                    WebSocketTestProtocol.GetRequestId(poll), new JObject
                    {
                        ["have_message"] = true,
                        ["vgroup_id"] = 0,
                        ["message_type"] = (int)TMQ_RES.TMQ_RES_DATA,
                        ["message_id"] = 1UL
                    }, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateTmqConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var exception = await Assert.ThrowsAsync<TDengineError>(
                () => connection.PollAsync(805, 1, CancellationToken.None)).ConfigureAwait(false);

            Assert.Equal((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE, exception.Code);
            Assert.False(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task TmqAssignmentRejectsMissingAssignmentList()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                var version = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendVersionResponseAsync(socket, version, token).ConfigureAwait(false);
                var assignment = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token)
                    .ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSTMQAction.TMQGetTopicAssignment,
                    WebSocketTestProtocol.GetRequestId(assignment), new JObject(), false, token)
                    .ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateTmqConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var exception = await Assert.ThrowsAsync<TDengineError>(
                () => connection.AssignmentAsync(806, "topic_a", CancellationToken.None))
                .ConfigureAwait(false);

            Assert.Equal((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE, exception.Code);
            Assert.False(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task TmqConcurrentPollAndCommitResponsesAreDispatchedByRequestId()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                var version = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendVersionResponseAsync(socket, version, token).ConfigureAwait(false);
                var first = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                var second = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                var poll = string.Equals(WebSocketTestProtocol.GetAction(first), WSTMQAction.TMQPoll,
                    StringComparison.Ordinal) ? first : second;
                var commit = ReferenceEquals(poll, first) ? second : first;

                await WebSocketTestProtocol.SendResponseAsync(socket, WSTMQAction.TMQCommit,
                    WebSocketTestProtocol.GetRequestId(commit), new JObject(), false, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSTMQAction.TMQPoll,
                    WebSocketTestProtocol.GetRequestId(poll), new JObject { ["have_message"] = false }, false,
                    token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateTmqConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var pollTask = connection.PollAsync(811, 1, CancellationToken.None);
            var commitTask = connection.CommitAsync(812, CancellationToken.None);

            await Task.WhenAll(pollTask, commitTask).ConfigureAwait(false);
            var pollResponse = await pollTask.ConfigureAwait(false);
            var commitResponse = await commitTask.ConfigureAwait(false);
            Assert.False(pollResponse.HaveMessage);
            Assert.Equal(811UL, pollResponse.ReqId);
            Assert.Equal(812UL, commitResponse.ReqId);
            Assert.True(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task TmqCloseCompletesAndFailsPendingPoll()
        {
            var pollReceived = NewCompletionSource<bool>();
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                var version = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendVersionResponseAsync(socket, version, token).ConfigureAwait(false);
                await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                pollReceived.TrySetResult(true);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateTmqConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var pollTask = connection.PollAsync(813, 1000, CancellationToken.None);
            await pollReceived.Task.ConfigureAwait(false);

            await AssertCompletesAsync(connection.CloseAsync(), TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var exception = await Assert.ThrowsAsync<TDengineWebSocketRequestException>(() => pollTask)
                .ConfigureAwait(false);
            Assert.Equal(813UL, exception.RequestId);
            Assert.True(exception.RequestMayHaveBeenSent);
            Assert.False(connection.IsAvailable());
        }

        [Fact]
        public async Task TmqCommittedRejectsMismatchedOffsetCount()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                var version = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendVersionResponseAsync(socket, version, token).ConfigureAwait(false);
                var committed = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSTMQAction.TMQCommitted,
                    WebSocketTestProtocol.GetRequestId(committed),
                    new JObject { ["committed"] = new JArray(1L) }, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateTmqConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var topicVgroups = new List<WSTopicVgroupId>
            {
                new WSTopicVgroupId { Topic = "topic_a", VGroupId = 0 },
                new WSTopicVgroupId { Topic = "topic_a", VGroupId = 1 }
            };
            var exception = await Assert.ThrowsAsync<TDengineError>(
                () => connection.CommittedAsync(814, topicVgroups, CancellationToken.None)).ConfigureAwait(false);

            Assert.Equal((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE, exception.Code);
            Assert.False(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task InvalidUtf8TextResponseInvalidatesConnection()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                await socket.SendAsync(new ArraySegment<byte>(new byte[] { 0xc3, 0x28 }),
                    WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var exception = await Assert.ThrowsAsync<TDengineWebSocketRequestException>(
                () => connection.BinaryQueryAsync("select invalid_utf8", 805)).ConfigureAwait(false);

            var protocolError = exception.InnerException as TDengineError;
            Assert.True(exception.InnerException is WebSocketException ||
                        protocolError?.Code == (int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE);
            Assert.False(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task BinaryQueryActionMismatchInvalidatesConnection()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var query = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, "unexpected_query_action",
                    WebSocketTestProtocol.GetBinaryRequestId(query.Bytes), new JObject
                    {
                        ["is_update"] = true,
                        ["affected_rows"] = 1,
                        ["fields_count"] = 0
                    }, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var exception = await Assert.ThrowsAsync<TDengineError>(
                () => connection.BinaryQueryAsync("select wrong_action", 806)).ConfigureAwait(false);

            Assert.Equal((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE, exception.Code);
            Assert.False(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Theory]
        [InlineData(WSAction.BinaryQuery)]
        [InlineData(WSAction.Query)]
        public async Task BinaryQueryAcceptsSupportedResponseActions(string responseAction)
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var query = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, responseAction,
                    WebSocketTestProtocol.GetBinaryRequestId(query.Bytes), new JObject
                    {
                        ["is_update"] = true,
                        ["affected_rows"] = 1,
                        ["fields_count"] = 0
                    }, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var response = await connection.BinaryQueryAsync("select supported_action", 8061)
                .ConfigureAwait(false);

            Assert.Equal(1, response.AffectedRows);
            Assert.True(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task BinaryQueryRejectsInvalidUtf16WithoutUsingTheConnection()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var query = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                await SendUpdateResponseAsync(socket, WebSocketTestProtocol.GetBinaryRequestId(query.Bytes), 4,
                    false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var invalidSql = "select '" + new string((char)0xd800, 1) + "'";
            await Assert.ThrowsAsync<EncoderFallbackException>(
                () => connection.BinaryQueryAsync(invalidSql, 8061)).ConfigureAwait(false);

            Assert.Equal(4, (await connection.BinaryQueryAsync("select valid", 8062)
                .ConfigureAwait(false)).AffectedRows);
            Assert.True(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task StatementBindActionMismatchInvalidatesConnection()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var bind = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, "unexpected_stmt2_bind_action",
                    WebSocketTestProtocol.GetBinaryRequestId(bind.Bytes), new JObject { ["stmt_id"] = 7UL },
                    false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var exception = await Assert.ThrowsAsync<TDengineError>(
                () => connection.Stmt2BindAsync(7, new byte[37], 37)).ConfigureAwait(false);

            Assert.Equal((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE, exception.Code);
            Assert.False(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task StatementPrepareRejectsDecimalMetadataOutsideTypeRange()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var init = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Init,
                    WebSocketTestProtocol.GetRequestId(init), new JObject { ["stmt_id"] = 77UL }, false, token)
                    .ConfigureAwait(false);

                var prepare = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Prepare,
                    WebSocketTestProtocol.GetRequestId(prepare), new JObject
                    {
                        ["stmt_id"] = 77UL,
                        ["is_insert"] = true,
                        ["fields_count"] = 1,
                        ["fields"] = new JArray(new JObject
                        {
                            ["name"] = "value",
                            ["field_type"] = (int)TDengineDataType.TSDB_DATA_TYPE_DECIMAL64,
                            ["precision"] = 19,
                            ["scale"] = 2,
                            ["bytes"] = sizeof(long),
                            ["bind_type"] = (int)TaosFieldType.TAOS_FIELD_COL
                        })
                    }, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var client = new WSClientAsync(CreateBuilder(server.Port));
            await client.ConnectAsync().ConfigureAwait(false);
            var stmt = await client.StmtInitAsync(807).ConfigureAwait(false);
            var exception = await Assert.ThrowsAsync<TDengineError>(() => stmt.PrepareAsync("insert into t values(?)"))
                .ConfigureAwait(false);

            Assert.Equal((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE, exception.Code);
            Assert.False(client.ConnectionAvailable());
            await stmt.DisposeAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public void FloatGetterPreservesNaNAndInfinitiesButRejectsFiniteOverflow()
        {
            var reader = new BlockReader(0, 1, 0,
                new[] { (byte)TDengineDataType.TSDB_DATA_TYPE_DOUBLE }, new byte[] { 0 }, TimeZoneInfo.Utc);

            reader.SetBlock(CreateSingleDoubleBlock(double.NaN));
            Assert.True(float.IsNaN(reader.GetFloat(0, 0)));
            reader.SetBlock(CreateSingleDoubleBlock(double.PositiveInfinity));
            Assert.Equal(float.PositiveInfinity, reader.GetFloat(0, 0));
            reader.SetBlock(CreateSingleDoubleBlock(double.NegativeInfinity));
            Assert.Equal(float.NegativeInfinity, reader.GetFloat(0, 0));
            reader.SetBlock(CreateSingleDoubleBlock(double.MaxValue));
            Assert.Throws<InvalidCastException>(() => reader.GetFloat(0, 0));
        }

        [Fact]
        public void Decimal64GetterHandlesLongMinValue()
        {
            var payload = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(payload, long.MinValue);
            var reader = CreateSingleColumnReader(TDengineDataType.TSDB_DATA_TYPE_DECIMAL64, 0, payload);

            Assert.Equal(-9223372036854775808m, reader.GetDecimal(0, 0));
            Assert.Equal("-9223372036854775808", reader.GetString(0, 0));
        }

        [Fact]
        public void Decimal128GetterRejectsMagnitudeLargerThanSystemDecimal()
        {
            var payload = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), (ulong)uint.MaxValue + 1UL);
            var reader = CreateSingleColumnReader(TDengineDataType.TSDB_DATA_TYPE_DECIMAL, 0, payload);

            Assert.Throws<OverflowException>(() => reader.GetDecimal(0, 0));
        }

        [Fact]
        public void Decimal128GetterSupportsScale28AndRejectsScale29()
        {
            var payload = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(payload, 1UL);
            var scale28 = CreateSingleColumnReader(TDengineDataType.TSDB_DATA_TYPE_DECIMAL, 28, payload);
            var scale29 = CreateSingleColumnReader(TDengineDataType.TSDB_DATA_TYPE_DECIMAL, 29, payload);

            Assert.Equal(0.0000000000000000000000000001m, scale28.GetDecimal(0, 0));
            Assert.Throws<OverflowException>(() => scale29.GetDecimal(0, 0));
            Assert.Equal("0.00000000000000000000000000001", scale29.GetString(0, 0));
        }

        [Fact]
        public void VarbinaryRejectsNegativeDataOffset()
        {
            var block = CreateSingleVariableBlock(TDengineDataType.TSDB_DATA_TYPE_VARBINARY,
                new byte[] { 1, 2, 3 });
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(37), -2);
            var reader = new BlockReader(0, 1, 0,
                new[] { (byte)TDengineDataType.TSDB_DATA_TYPE_VARBINARY }, new byte[] { 0 }, TimeZoneInfo.Utc);
            reader.SetBlock(block);

            Assert.Throws<InvalidDataException>(() => reader.Read(0, 0));
            Assert.Throws<InvalidDataException>(() => reader.IsDBNull(0, 0));
        }

        [Fact]
        public void GeometryRejectsDataOffsetOutsideColumnSegment()
        {
            var block = CreateSingleVariableBlock(TDengineDataType.TSDB_DATA_TYPE_GEOMETRY,
                new byte[] { 1, 2, 3 });
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(37), int.MaxValue);
            var reader = new BlockReader(0, 1, 0,
                new[] { (byte)TDengineDataType.TSDB_DATA_TYPE_GEOMETRY }, new byte[] { 0 }, TimeZoneInfo.Utc);
            reader.SetBlock(block);

            Assert.Throws<InvalidDataException>(() => reader.Read(0, 0));
        }

        [Fact]
        public void BlobRejectsLengthLargerThanSupportedRange()
        {
            var block = CreateSingleVariableBlock(TDengineDataType.TSDB_DATA_TYPE_BLOB,
                new byte[] { 1 }, sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(41), uint.MaxValue);
            var reader = new BlockReader(0, 1, 0,
                new[] { (byte)TDengineDataType.TSDB_DATA_TYPE_BLOB }, new byte[] { 0 }, TimeZoneInfo.Utc);
            reader.SetBlock(block);

            Assert.Throws<InvalidDataException>(() => reader.Read(0, 0));
            Assert.Throws<InvalidDataException>(() => reader.GetBytes(0, 0, 0, null, 0, 0));
        }

        [Fact]
        public void NcharChunkedGettersPreserveOffsetsWithoutTemporaryPayloadAllocations()
        {
            const string value = "A\ud83d\ude00B";
            var payload = new UTF32Encoding(false, false, true).GetBytes(value);
            var block = CreateSingleVariableBlock(TDengineDataType.TSDB_DATA_TYPE_NCHAR, payload);
            var reader = new BlockReader(0, 1, 0,
                new[] { (byte)TDengineDataType.TSDB_DATA_TYPE_NCHAR }, new byte[] { 0 }, TimeZoneInfo.Utc);
            reader.SetBlock(block);

            Assert.Equal(4, reader.GetChars(0, 0, 0, null, 0, 0));
            Assert.Equal('A', reader.GetChar(0, 0));
            var chars = new char[2];
            Assert.Equal(2, reader.GetChars(0, 0, 1, chars, 0, chars.Length));
            Assert.Equal("\ud83d\ude00", new string(chars));

            var expectedUtf8 = Encoding.UTF8.GetBytes(value);
            Assert.Equal(expectedUtf8.Length, reader.GetBytes(0, 0, 0, null, 0, 0));
            var bytes = new byte[3];
            Assert.Equal(bytes.Length, reader.GetBytes(0, 0, 2, bytes, 0, bytes.Length));
            Assert.Equal(expectedUtf8[2], bytes[0]);
            Assert.Equal(expectedUtf8[3], bytes[1]);
            Assert.Equal(expectedUtf8[4], bytes[2]);

            reader.GetChars(0, 0, 1, chars, 0, chars.Length);
            reader.GetBytes(0, 0, 2, bytes, 0, bytes.Length);
            reader.GetChar(0, 0);
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long copied = 0;
            for (var i = 0; i < 100; i++)
            {
                copied += reader.GetChars(0, 0, 1, chars, 0, chars.Length);
                copied += reader.GetBytes(0, 0, 2, bytes, 0, bytes.Length);
                copied += reader.GetChar(0, 0);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.True(copied > 0);
            Assert.InRange(allocated, 0, 128);
        }

        [Fact]
        public async Task OversizedTextResponseClosesConnectionAndFailsPendingRequest()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var chunk = new byte[64 * 1024];
                Array.Fill(chunk, (byte)'x');
                try
                {
                    for (var i = 0; i < 257; i++)
                    {
                        await socket.SendAsync(new ArraySegment<byte>(chunk), WebSocketMessageType.Text,
                            i == 256, token).ConfigureAwait(false);
                    }
                }
                catch (WebSocketException)
                {
                }
                catch (IOException)
                {
                }
            });

            var connection = CreateConnection(server.Port, TimeSpan.FromSeconds(5));
            await connection.ConnectAsync().ConfigureAwait(false);
            var exception = await Assert.ThrowsAsync<TDengineWebSocketRequestException>(
                () => connection.BinaryQueryAsync("select oversized", 901)).ConfigureAwait(false);
            AssertRootCauseIsUnexpectedMessage(exception);
            Assert.False(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task CancellationAndCloseRaceAlwaysCompletesPendingRequest()
        {
            var requestReceived = new SemaphoreSlim(0);
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                requestReceived.Release();
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            for (var i = 0; i < 32; i++)
            {
                var connection = CreateConnection(server.Port);
                await connection.ConnectAsync().ConfigureAwait(false);
                using var cancellation = new CancellationTokenSource();
                var requestTask = connection.BinaryQueryAsync("select close_race", (ulong)(1000 + i),
                    cancellation.Token);
                Assert.True(await requestReceived.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false));

                var start = NewCompletionSource<bool>();
                var cancelTask = Task.Run(async () =>
                {
                    await start.Task.ConfigureAwait(false);
                    cancellation.Cancel();
                });
                var closeTask = Task.Run(async () =>
                {
                    await start.Task.ConfigureAwait(false);
                    await connection.CloseAsync().ConfigureAwait(false);
                });
                start.TrySetResult(true);

                await Task.WhenAll(cancelTask, closeTask).ConfigureAwait(false);
                var completed = await Task.WhenAny(requestTask, Task.Delay(TimeSpan.FromSeconds(2)))
                    .ConfigureAwait(false);
                Assert.Same(requestTask, completed);
                await Assert.ThrowsAnyAsync<Exception>(() => requestTask).ConfigureAwait(false);
            }
        }

        [Fact]
        public async Task OneWayResponseCollidingWithPendingRequestInvalidatesConnection()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var request = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var requestId = WebSocketTestProtocol.GetBinaryRequestId(request.Bytes);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.FreeResult, requestId,
                    new JObject(), false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var exception = await Assert.ThrowsAsync<TDengineWebSocketRequestException>(
                    () => connection.BinaryQueryAsync("select one_way_collision", 1101))
                .ConfigureAwait(false);

            AssertRootCauseIsUnexpectedMessage(exception);
            Assert.False(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task UnknownResponseRequestIdInvalidatesConnection()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var request = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var requestId = WebSocketTestProtocol.GetBinaryRequestId(request.Bytes);
                await SendUpdateResponseAsync(socket, requestId + 1, 19, false, token).ConfigureAwait(false);
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var exception = await Assert.ThrowsAsync<TDengineWebSocketRequestException>(
                    () => connection.BinaryQueryAsync("select unknown_response", 1151))
                .ConfigureAwait(false);

            AssertRootCauseIsUnexpectedMessage(exception);
            Assert.False(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task DuplicatePendingRequestIdIsRejectedWithoutASecondSend()
        {
            var firstReceived = NewCompletionSource<bool>();
            var releaseResponse = NewCompletionSource<bool>();
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var request = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var requestId = WebSocketTestProtocol.GetBinaryRequestId(request.Bytes);
                firstReceived.TrySetResult(true);
                await releaseResponse.Task.ConfigureAwait(false);
                await SendUpdateResponseAsync(socket, requestId, 1, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var first = connection.BinaryQueryAsync("select first", 1201);
            await firstReceived.Task.ConfigureAwait(false);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => connection.BinaryQueryAsync("select duplicate", 1201)).ConfigureAwait(false);
            releaseResponse.TrySetResult(true);
            Assert.Equal(1, (await first.ConfigureAwait(false)).AffectedRows);
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task RowsDisposeCompletesWhenFetchIgnoresCancellation()
        {
            var fetchStarted = NewCompletionSource<bool>();
            var fetchCompletion = NewCompletionSource<byte[]>();
            var rows = new WSRowsAsync(1, CreateSingleIntMetadata(),
                WSRowsAsync.TestFetchRawBlockAccessor.Instance,
                (_, _) =>
                {
                    fetchStarted.TrySetResult(true);
                    return fetchCompletion.Task;
                }, TimeZoneInfo.Utc, TimeSpan.FromMilliseconds(30));

            var readTask = rows.ReadAsync();
            await fetchStarted.Task.ConfigureAwait(false);
            await AssertCompletesAsync(rows.DisposeAsync().AsTask(), TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);

            fetchCompletion.TrySetResult(CreateCompletedFetchResponse(1));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => readTask).ConfigureAwait(false);
            await rows.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task PendingFetchDoesNotRetainDisposedRows()
        {
            var fetchStarted = NewCompletionSource<bool>();
            var fetchCompletion = NewCompletionSource<byte[]>();
            var rowsReference = await CreateDisposedRowsReferenceAsync(fetchStarted, fetchCompletion)
                .ConfigureAwait(false);

            await AssertCollectedAsync(rowsReference, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            fetchCompletion.TrySetResult(CreateCompletedFetchResponse(1));
        }

        [Fact]
        public async Task CallerCanceledFetchReleasesCancellationSourceAndCanBeResumed()
        {
            var fetchStarted = NewCompletionSource<bool>();
            var fetchCompletion = NewCompletionSource<byte[]>();
            var fetchCalls = 0;
            var rows = new WSRowsAsync(1, CreateSingleIntMetadata(),
                WSRowsAsync.TestFetchRawBlockAccessor.Instance,
                (_, _) =>
                {
                    Interlocked.Increment(ref fetchCalls);
                    fetchStarted.TrySetResult(true);
                    return fetchCompletion.Task;
                }, TimeZoneInfo.Utc);

            using var cancellation = new CancellationTokenSource();
            var readTask = rows.ReadAsync(cancellation.Token);
            await fetchStarted.Task.ConfigureAwait(false);
            var fetchOperation = GetPrivateInstanceField(rows, "_fetchOperation");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask).ConfigureAwait(false);

            fetchCompletion.TrySetResult(CreateCompletedFetchResponse(1));
            await WaitForPrivateIntFieldAsync(fetchOperation, "_cancellationSourceDisposed", 1)
                .ConfigureAwait(false);

            Assert.False(await rows.ReadAsync().ConfigureAwait(false));
            Assert.Equal(1, Volatile.Read(ref fetchCalls));
            await rows.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task RowsDisposeTimeoutInvalidatesPhysicalConnection()
        {
            var fetchReceived = NewCompletionSource<bool>();
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var query = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var queryId = WebSocketTestProtocol.GetBinaryRequestId(query.Bytes);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.Query, queryId,
                    new JObject
                    {
                        ["is_update"] = false,
                        ["id"] = 9001UL,
                        ["fields_count"] = 1,
                        ["precision"] = 0,
                        ["fields_names"] = new JArray("value"),
                        ["fields_types"] = new JArray((int)TDengineDataType.TSDB_DATA_TYPE_INT),
                        ["fields_lengths"] = new JArray(sizeof(int)),
                        ["fields_precisions"] = new JArray(0),
                        ["fields_scales"] = new JArray(0)
                    }, false, token).ConfigureAwait(false);

                await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                fetchReceived.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var response = await connection.BinaryQueryAsync("select delayed_fetch", 1161)
                .ConfigureAwait(false);
            var rows = new WSRowsAsync(response.ResultId, response, connection, TimeZoneInfo.Utc,
                TimeSpan.FromMilliseconds(30));
            var readTask = rows.ReadAsync();
            await fetchReceived.Task.ConfigureAwait(false);

            await AssertCompletesAsync(rows.DisposeAsync().AsTask(), TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            Assert.False(connection.IsAvailable());
            var readCompleted = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(2)))
                .ConfigureAwait(false);
            Assert.Same(readTask, readCompleted);
            await Assert.ThrowsAnyAsync<Exception>(() => readTask).ConfigureAwait(false);
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task TypedResponseActionMismatchInvalidatesPhysicalConnection()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.Query, 0,
                    new JObject { ["version"] = "3.3.6.0" }, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);

            var exception = await Assert.ThrowsAsync<TDengineError>(
                () => connection.ValidateConnectionAsync(CancellationToken.None)).ConfigureAwait(false);

            Assert.Equal((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE, exception.Code);
            Assert.False(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task RowsFreeResultSendFailureInvalidatesPhysicalConnection()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var query = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var queryId = WebSocketTestProtocol.GetBinaryRequestId(query.Bytes);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.Query, queryId,
                    CreateSingleIntResultProperties(9201), false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port, TimeSpan.FromMilliseconds(50));
            await connection.ConnectAsync().ConfigureAwait(false);
            var response = await connection.BinaryQueryAsync("select dispose_failure", 1301)
                .ConfigureAwait(false);
            var rows = new WSRowsAsync(response.ResultId, response, connection, TimeZoneInfo.Utc);
            var sendGate = GetSendGate(connection);
            await sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await rows.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }

            Assert.False(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task RowsDisposeUsesDrainTimeoutWhenSendGateIsStalled()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var query = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.Query,
                    WebSocketTestProtocol.GetBinaryRequestId(query.Bytes), CreateSingleIntResultProperties(9204),
                    false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port, TimeSpan.FromSeconds(30));
            await connection.ConnectAsync().ConfigureAwait(false);
            var response = await connection.BinaryQueryAsync("select stalled_rows_cleanup", 1311)
                .ConfigureAwait(false);
            var rows = new WSRowsAsync(response.ResultId, response, connection, TimeZoneInfo.Utc,
                TimeSpan.FromMilliseconds(40));
            var sendGate = GetSendGate(connection);
            await sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await AssertCompletesAsync(rows.DisposeAsync().AsTask(), TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }

            Assert.False(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task CanceledReadThenDisposeInvalidatesUnknownFetchOutcome()
        {
            var fetchReceived = NewCompletionSource<bool>();
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var query = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var queryId = WebSocketTestProtocol.GetBinaryRequestId(query.Bytes);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.Query, queryId,
                    CreateSingleIntResultProperties(9202), false, token).ConfigureAwait(false);
                await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                fetchReceived.TrySetResult(true);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var connection = CreateConnection(server.Port);
            await connection.ConnectAsync().ConfigureAwait(false);
            var response = await connection.BinaryQueryAsync("select canceled_fetch", 1302)
                .ConfigureAwait(false);
            var rows = new WSRowsAsync(response.ResultId, response, connection, TimeZoneInfo.Utc);
            using var cancellation = new CancellationTokenSource();
            var readTask = rows.ReadAsync(cancellation.Token);
            await fetchReceived.Task.ConfigureAwait(false);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask).ConfigureAwait(false);

            await AssertCompletesAsync(rows.DisposeAsync().AsTask(), TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);

            Assert.False(connection.IsAvailable());
            await connection.CloseAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task StatementCloseSendFailureInvalidatesPhysicalConnection()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var init = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Init,
                    WebSocketTestProtocol.GetRequestId(init), new JObject { ["stmt_id"] = 33UL }, false, token)
                    .ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var builder = CreateBuilder(server.Port);
            builder.WriteTimeout = TimeSpan.FromMilliseconds(50);
            var client = new WSClientAsync(builder);
            await client.ConnectAsync().ConfigureAwait(false);
            var stmt = await client.StmtInitAsync(1303).ConfigureAwait(false);
            var connection = GetClientConnection(client);
            var sendGate = GetSendGate(connection);
            await sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await stmt.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }

            Assert.False(client.ConnectionAvailable());
            await client.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task StatementDisposeUsesDrainTimeoutWhenSendGateIsStalled()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var builder = CreateBuilder(server.Port);
            builder.WriteTimeout = TimeSpan.FromSeconds(30);
            var client = new WSClientAsync(builder);
            await client.ConnectAsync().ConfigureAwait(false);
            var connection = GetClientConnection(client);
            var stmt = new WSStmtAsync(client, 61, TimeZoneInfo.Utc, connection,
                TimeSpan.FromMilliseconds(40));
            var sendGate = GetSendGate(connection);
            await sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await AssertCompletesAsync(stmt.DisposeAsync().AsTask(), TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }

            Assert.False(client.ConnectionAvailable());
            await client.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task QueryRowsConstructionAndCleanupFailureInvalidatesPhysicalConnection()
        {
            var queryReceived = NewCompletionSource<bool>();
            var releaseResponse = NewCompletionSource<bool>();
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var query = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var queryId = WebSocketTestProtocol.GetBinaryRequestId(query.Bytes);
                queryReceived.TrySetResult(true);
                await releaseResponse.Task.ConfigureAwait(false);
                var invalidMetadata = CreateSingleIntResultProperties(9203);
                invalidMetadata["fields_names"] = new JArray();
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.Query, queryId,
                    invalidMetadata, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var builder = CreateBuilder(server.Port);
            builder.WriteTimeout = TimeSpan.FromMilliseconds(80);
            var client = new WSClientAsync(builder);
            await client.ConnectAsync().ConfigureAwait(false);
            var queryTask = client.QueryAsync("select invalid_metadata", 1304);
            await queryReceived.Task.ConfigureAwait(false);
            var connection = GetClientConnection(client);
            var sendGate = GetSendGate(connection);
            await sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                releaseResponse.TrySetResult(true);
                await Assert.ThrowsAsync<AggregateException>(() => queryTask).ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }

            Assert.False(client.ConnectionAvailable());
            await client.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task StatementDisposeDuringReconnectClosesOnlyNewStatement()
        {
            var secondInitReceived = NewCompletionSource<bool>();
            var releaseSecondInit = NewCompletionSource<bool>();
            await using var server = new LoopbackWebSocketServer(async (socket, connectionNumber, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                if (connectionNumber == 1)
                {
                    var init = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                    await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Init,
                        WebSocketTestProtocol.GetRequestId(init), new JObject { ["stmt_id"] = 41UL }, false, token)
                        .ConfigureAwait(false);
                    await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
                    return;
                }

                if (connectionNumber != 2)
                {
                    throw new InvalidDataException("Statement cleanup unexpectedly opened another connection.");
                }

                var reconnectInit = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token)
                    .ConfigureAwait(false);
                secondInitReceived.TrySetResult(true);
                await releaseSecondInit.Task.ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Init,
                    WebSocketTestProtocol.GetRequestId(reconnectInit), new JObject { ["stmt_id"] = 42UL }, false,
                    token).ConfigureAwait(false);

                var close = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                Assert.Equal(WSAction.STMT2Close, WebSocketTestProtocol.GetAction(close));
                Assert.Equal(42UL, close["args"]?["stmt_id"]?.Value<ulong>());

                var query = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                var queryId = WebSocketTestProtocol.GetBinaryRequestId(query.Bytes);
                await SendUpdateResponseAsync(socket, queryId, 5, false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var builder = CreateBuilder(server.Port);
            builder.AutoReconnect = true;
            builder.ReconnectRetryCount = 2;
            builder.ReconnectIntervalMs = 1;
            var client = new WSClientAsync(builder);
            await client.ConnectAsync().ConfigureAwait(false);
            var stmt = await client.StmtInitAsync(1305).ConfigureAwait(false);
            await GetClientConnection(client).InvalidateAsync().ConfigureAwait(false);

            var reconnectTask = InvokePrivateTask(stmt, "ReconnectInternalAsync", CancellationToken.None);
            await secondInitReceived.Task.ConfigureAwait(false);
            await stmt.DisposeAsync().ConfigureAwait(false);
            releaseSecondInit.TrySetResult(true);

            await Assert.ThrowsAsync<ObjectDisposedException>(() => reconnectTask).ConfigureAwait(false);
            Assert.Equal(5, await client.ExecAsync("insert after_stmt_dispose", 1306).ConfigureAwait(false));
            Assert.Equal(2, server.AcceptedConnections);
            await client.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task FailedReprepareClosesNewStatementAndMakesStatementUnusable()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, connectionNumber, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                if (connectionNumber == 1)
                {
                    var init = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                    await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Init,
                        WebSocketTestProtocol.GetRequestId(init), new JObject { ["stmt_id"] = 51UL }, false, token)
                        .ConfigureAwait(false);
                    var prepare = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token)
                        .ConfigureAwait(false);
                    await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Prepare,
                        WebSocketTestProtocol.GetRequestId(prepare), new JObject
                        {
                            ["stmt_id"] = 51UL,
                            ["is_insert"] = false,
                            ["fields_count"] = 0
                        }, false, token).ConfigureAwait(false);
                    await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
                    return;
                }

                if (connectionNumber != 2)
                {
                    throw new InvalidDataException("Failed re-prepare unexpectedly opened another connection.");
                }

                var reconnectInit = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token)
                    .ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Init,
                    WebSocketTestProtocol.GetRequestId(reconnectInit), new JObject { ["stmt_id"] = 52UL }, false,
                    token).ConfigureAwait(false);
                var reprepare = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token)
                    .ConfigureAwait(false);
                await WebSocketTestProtocol.SendErrorResponseAsync(socket, WSAction.STMT2Prepare,
                    WebSocketTestProtocol.GetRequestId(reprepare), 0x321, "re-prepare failed", token)
                    .ConfigureAwait(false);

                var close = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                Assert.Equal(WSAction.STMT2Close, WebSocketTestProtocol.GetAction(close));
                Assert.Equal(52UL, close["args"]?["stmt_id"]?.Value<ulong>());

                var query = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                await SendUpdateResponseAsync(socket, WebSocketTestProtocol.GetBinaryRequestId(query.Bytes), 6,
                    false, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var builder = CreateBuilder(server.Port);
            builder.AutoReconnect = true;
            builder.ReconnectRetryCount = 2;
            builder.ReconnectIntervalMs = 1;
            var client = new WSClientAsync(builder);
            await client.ConnectAsync().ConfigureAwait(false);
            var stmt = await client.StmtInitAsync(1307).ConfigureAwait(false);
            await stmt.PrepareAsync("select 1").ConfigureAwait(false);
            await GetClientConnection(client).InvalidateAsync().ConfigureAwait(false);

            var exception = await Assert.ThrowsAsync<TDengineError>(
                () => InvokePrivateTask(stmt, "ReconnectAndReprepareAsync", CancellationToken.None))
                .ConfigureAwait(false);
            Assert.Equal(0x321, exception.Code);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => stmt.PrepareAsync("select 2"))
                .ConfigureAwait(false);
            Assert.Equal(6, await client.ExecAsync("insert after_failed_reprepare", 1308)
                .ConfigureAwait(false));

            await stmt.DisposeAsync().ConfigureAwait(false);
            await stmt.DisposeAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
            Assert.Equal(2, server.AcceptedConnections);
        }

        [Fact]
        public void OversizedMetadataAndTmqColumnCountsAreRejectedBeforeAllocation()
        {
            var metadata = CreateSingleIntMetadata();
            metadata.FieldsCount = BlockReader.MaximumColumnCount + 1;
            Assert.Throws<InvalidDataException>(() => new WSRowsAsync(1, metadata,
                WSRowsAsync.TestFetchRawBlockAccessor.Instance,
                (_, _) => Task.FromResult(CreateCompletedFetchResponse(1)), TimeZoneInfo.Utc));

            var block = new byte[28];
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(12), BlockReader.MaximumColumnCount + 1);
            var reader = new BlockReader(0, TimeZoneInfo.Utc);
            Assert.Throws<InvalidDataException>(() => reader.SetTMQBlock(block, 0, 0));
        }

        [Fact]
        public void FailedTmqBlockUpdatePreservesPreviouslyLoadedBlock()
        {
            var reader = new BlockReader(0, TimeZoneInfo.Utc);
            var validBlock = CreateSingleDoubleBlock(12.5);
            reader.SetTMQBlock(validBlock, 0, 0);
            Assert.Equal(12.5, reader.GetDouble(0, 0));

            var invalidBlock = (byte[])validBlock.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(invalidBlock.AsSpan(4), 28);
            Assert.Throws<InvalidDataException>(() => reader.SetTMQBlock(invalidBlock, 0, 0));

            Assert.Equal(12.5, reader.GetDouble(0, 0));
            Assert.Throws<InvalidDataException>(() => reader.SetTMQBlock(validBlock, 99, 0));
            Assert.Equal(12.5, reader.GetDouble(0, 0));
        }

        [Fact]
        public async Task CloseWaitsForReceiveLoopWhenConnectionWasAlreadyClosed()
        {
            var receiveLoopCompletion = NewCompletionSource<bool>();
            var connection = new OfflineBaseConnectionAsync();
            SetPrivateField(connection, "_receiveLoopTask", receiveLoopCompletion.Task);
            InvokePrivateMethod(connection, "DoClose");

            var closeTask = connection.CloseAsync();
            await Task.Delay(30).ConfigureAwait(false);
            Assert.False(closeTask.IsCompleted);

            receiveLoopCompletion.TrySetResult(true);
            await AssertCompletesAsync(closeTask, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        private static ConnectionAsync CreateConnection(int port, TimeSpan writeTimeout = default,
            TimeSpan connectTimeout = default)
        {
            return new ConnectionAsync($"ws://127.0.0.1:{port}/ws", "root", "taosdata", string.Empty,
                string.Empty, connectTimeout == default ? TimeSpan.FromSeconds(2) : connectTimeout,
                TimeSpan.FromSeconds(3), writeTimeout == default ? TimeSpan.FromSeconds(2) : writeTimeout);
        }

        private static TMQConnectionAsync CreateTmqConnection(int port)
        {
            var options = new TMQOptions(new Dictionary<string, string>
            {
                ["td.connect.ip"] = "127.0.0.1",
                ["td.connect.port"] = port.ToString(),
                ["useSSL"] = "false"
            });
            return new TMQConnectionAsync(options, TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static async Task<WeakReference> CreateDisposedRowsReferenceAsync(
            TaskCompletionSource<bool> fetchStarted, TaskCompletionSource<byte[]> fetchCompletion)
        {
            var rows = new WSRowsAsync(1, CreateSingleIntMetadata(),
                WSRowsAsync.TestFetchRawBlockAccessor.Instance,
                (_, _) =>
                {
                    fetchStarted.TrySetResult(true);
                    return fetchCompletion.Task;
                }, TimeZoneInfo.Utc, TimeSpan.FromMilliseconds(30));
            var readTask = rows.ReadAsync();
            await fetchStarted.Task.ConfigureAwait(false);
            await rows.DisposeAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => readTask).ConfigureAwait(false);
            return new WeakReference(rows);
        }

        private static async Task AssertCollectedAsync(WeakReference reference, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (reference.IsAlive && stopwatch.Elapsed < timeout)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(10).ConfigureAwait(false);
            }

            Assert.False(reference.IsAlive, "The pending fetch retained the disposed rows object.");
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = typeof(BaseConnectionAsync).GetField(fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(target, value);
        }

        private static object GetPrivateInstanceField(object target, string fieldName)
        {
            var field = target.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var value = field!.GetValue(target);
            Assert.NotNull(value);
            return value!;
        }

        private static async Task WaitForPrivateIntFieldAsync(object target, string fieldName, int expected)
        {
            var field = target.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(2))
            {
                if ((int)field!.GetValue(target)! == expected)
                {
                    return;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            Assert.Equal(expected, (int)field!.GetValue(target)!);
        }

        private static SemaphoreSlim GetSendGate(BaseConnectionAsync connection)
        {
            var field = typeof(BaseConnectionAsync).GetField("_sendSemaphore",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return Assert.IsType<SemaphoreSlim>(field!.GetValue(connection));
        }

        private static ConnectionAsync GetClientConnection(WSClientAsync client)
        {
            var field = typeof(WSClientAsync).GetField("_connection",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return Assert.IsType<ConnectionAsync>(field!.GetValue(client));
        }

        private static void InvokePrivateMethod(object target, string methodName)
        {
            var method = typeof(BaseConnectionAsync).GetMethod(methodName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);
            method!.Invoke(target, new object[1]);
        }

        private static Task InvokePrivateTask(object target, string methodName, params object[] arguments)
        {
            var method = target.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return Assert.IsAssignableFrom<Task>(method!.Invoke(target, arguments));
        }

        private sealed class OfflineBaseConnectionAsync : BaseConnectionAsync
        {
            internal OfflineBaseConnectionAsync()
                : base("ws://127.0.0.1:1/ws", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1))
            {
            }
        }

        private static ConnectionStringBuilder CreateBuilder(int port)
        {
            return new ConnectionStringBuilder(
                $"protocol=WebSocket;host=127.0.0.1;port={port};username=root;password=taosdata;" +
                "connTimeout=00:00:02;readTimeout=00:00:03;writeTimeout=00:00:02");
        }

        private static async Task CompleteConnectionHandshakeAsync(WebSocket socket,
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

            var requestId = WebSocketTestProtocol.GetRequestId(connect);
            await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.Conn, requestId, new JObject(),
                false, cancellationToken).ConfigureAwait(false);
        }

        private static Task SendUpdateResponseAsync(WebSocket socket, ulong requestId, int affectedRows,
            bool fragmented, CancellationToken cancellationToken)
        {
            return WebSocketTestProtocol.SendResponseAsync(socket, WSAction.BinaryQuery, requestId,
                new JObject
                {
                    ["is_update"] = true,
                    ["affected_rows"] = affectedRows,
                    ["fields_count"] = 0
                }, fragmented, cancellationToken);
        }

        private static async Task CompleteCloseHandshakeAsync(WebSocket socket,
            CancellationToken cancellationToken)
        {
            try
            {
                while (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseSent)
                {
                    var message = await WebSocketTestProtocol.ReceiveAsync(socket, cancellationToken)
                        .ConfigureAwait(false);
                    if (message.MessageType == WebSocketMessageType.Close)
                    {
                        await TryCloseOutputAsync(socket).ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (WebSocketException)
            {
            }
            catch (IOException)
            {
            }
        }

        private static async Task TryCloseOutputAsync(WebSocket socket)
        {
            try
            {
                if (socket.State == WebSocketState.CloseReceived || socket.State == WebSocketState.Open)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (WebSocketException)
            {
            }
        }

        private static async Task ReadHttpHeadersAsync(Stream stream, CancellationToken cancellationToken)
        {
            var state = 0;
            var buffer = new byte[1];
            while (state < 4)
            {
                var read = await stream.ReadAsync(buffer, 0, 1, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                var value = buffer[0];
                state = state switch
                {
                    0 or 2 => value == (byte)'\r' ? state + 1 : 0,
                    1 or 3 => value == (byte)'\n' ? state + 1 : 0,
                    _ => state
                };
            }
        }

        private static WSQueryResp CreateSingleIntMetadata()
        {
            return new WSQueryResp
            {
                FieldsCount = 1,
                FieldsNames = new[] { "value" },
                FieldsTypes = new[] { (byte)TDengineDataType.TSDB_DATA_TYPE_INT },
                FieldsLengths = new long[] { sizeof(int) },
                FieldsPrecisions = new byte[] { 0 },
                FieldsScales = new byte[] { 0 },
                Precision = 0
            };
        }

        private static WSRowsAsync CreateOfflineRows(WSQueryResp metadata, byte[]? response = null)
        {
            response ??= CreateCompletedFetchResponse(1);
            return new WSRowsAsync(1, metadata, WSRowsAsync.TestFetchRawBlockAccessor.Instance,
                (_, _) => Task.FromResult(response), TimeZoneInfo.Utc);
        }

        private static JObject CreateSingleIntResultProperties(ulong resultId)
        {
            return new JObject
            {
                ["is_update"] = false,
                ["id"] = resultId,
                ["fields_count"] = 1,
                ["precision"] = 0,
                ["fields_names"] = new JArray("value"),
                ["fields_types"] = new JArray((int)TDengineDataType.TSDB_DATA_TYPE_INT),
                ["fields_lengths"] = new JArray(sizeof(int)),
                ["fields_precisions"] = new JArray(0),
                ["fields_scales"] = new JArray(0)
            };
        }

        private static byte[] CreateCompletedFetchResponse(ulong resultId)
        {
            return CreateFetchResponse(resultId, 0, Array.Empty<byte>(), true);
        }

        private static byte[] CreateFetchResponse(ulong resultId, uint code, byte[] message, bool completed)
        {
            message ??= Array.Empty<byte>();
            var response = new byte[42 + message.Length + (code == 0 ? sizeof(ulong) + 1 : 0)];
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(16), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(34), code);
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(38), (uint)message.Length);
            message.AsSpan().CopyTo(response.AsSpan(42));
            if (code == 0)
            {
                var resultIdOffset = 42 + message.Length;
                BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(resultIdOffset), resultId);
                response[resultIdOffset + sizeof(ulong)] = completed ? (byte)1 : (byte)0;
            }

            return response;
        }

        private static byte[] CreateSingleDoubleBlock(double value)
        {
            const int blockLength = 46;
            var block = new byte[blockLength];
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(0), 1);
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(4), blockLength);
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(12), 1);
            block[28] = (byte)TDengineDataType.TSDB_DATA_TYPE_DOUBLE;
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(33), sizeof(double));
            BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(38), BitConverter.DoubleToInt64Bits(value));
            return block;
        }

        private static BlockReader CreateSingleColumnReader(TDengineDataType type, byte scale, byte[] payload)
        {
            var reader = new BlockReader(0, 1, 0, new[] { (byte)type }, new[] { scale }, TimeZoneInfo.Utc);
            reader.SetBlock(CreateSingleFixedBlock(type, payload));
            return reader;
        }

        private static byte[] CreateSingleFixedBlock(TDengineDataType type, byte[] payload)
        {
            const int headerLength = 37;
            var blockLength = checked(headerLength + 1 + payload.Length);
            var block = new byte[blockLength];
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(0), 1);
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(4), blockLength);
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(12), 1);
            block[28] = (byte)type;
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(33), payload.Length);
            payload.AsSpan().CopyTo(block.AsSpan(38));
            return block;
        }

        private static byte[] CreateSingleVariableBlock(TDengineDataType type, byte[] payload,
            int lengthHeaderSize = sizeof(ushort))
        {
            const int headerLength = 37;
            const int offsetTableLength = sizeof(int);
            var columnDataLength = checked(lengthHeaderSize + payload.Length);
            var blockLength = checked(headerLength + offsetTableLength + columnDataLength);
            var block = new byte[blockLength];
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(0), 1);
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(4), blockLength);
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(12), 1);
            block[28] = (byte)type;
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(33), columnDataLength);
            BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(headerLength), 0);
            if (lengthHeaderSize == sizeof(ushort))
            {
                BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(headerLength + offsetTableLength),
                    checked((ushort)payload.Length));
            }
            else if (lengthHeaderSize == sizeof(uint))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(headerLength + offsetTableLength),
                    checked((uint)payload.Length));
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(lengthHeaderSize));
            }

            payload.AsSpan().CopyTo(block.AsSpan(headerLength + offsetTableLength + lengthHeaderSize));
            return block;
        }

        private static void AssertRootCauseIsUnexpectedMessage(TDengineWebSocketRequestException exception)
        {
            Assert.True(exception.RequestMayHaveBeenSent);
            var error = Assert.IsType<TDengineError>(exception.InnerException);
            Assert.Equal((int)TDengineError.InternalErrorCode.WS_UNEXPECTED_MESSAGE, error.Code);
        }

        private static async Task AssertCompletesAsync(Task task, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            Assert.Same(task, completed);
            await task.ConfigureAwait(false);
        }

        private static TaskCompletionSource<T> NewCompletionSource<T>()
        {
            return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class BindPayloadTestStmt : AbstractStmt
        {
            internal BindPayloadTestStmt() : base(30)
            {
            }

            internal byte[]? LastPayload { get; private set; }

            internal void Initialize()
            {
                ApplyPrepareResult("insert into t values(?)", true, 1, new[]
                {
                    new TaosFieldAll
                    {
                        name = "value",
                        type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_INT,
                        bytes = sizeof(int),
                        field_type = (byte)TaosFieldType.TAOS_FIELD_COL
                    }
                });
            }

            internal byte[] CapturePooledPayload()
            {
                var buffer = RentBindBinaryForExecution(out var bufferLength);
                try
                {
                    var snapshot = new byte[bufferLength];
                    Buffer.BlockCopy(buffer, 0, snapshot, 0, bufferLength);
                    return snapshot;
                }
                finally
                {
                    ReturnPooledBindBinary(buffer, bufferLength);
                }
            }

            protected override void PrepareInternal(string query, out bool isInsert, out int count,
                out TaosFieldAll[] fields)
            {
                throw new NotSupportedException();
            }

            protected override void BindBinaryInternal(byte[] data, out int affectedRows)
            {
                LastPayload = new byte[data.Length];
                Buffer.BlockCopy(data, 0, LastPayload, 0, data.Length);
                affectedRows = 1;
            }

            protected override bool IsConnectionAvailable(Exception exception)
            {
                return true;
            }

            protected override void ReconnectInternal()
            {
                throw new NotSupportedException();
            }

            protected override bool AutoReconnectInternal()
            {
                return false;
            }

            protected override IRows QueryResultInternal()
            {
                throw new NotSupportedException();
            }

            protected override IRows InsertResultInternal(int affectedRows)
            {
                throw new NotSupportedException();
            }

            public override void Dispose()
            {
            }
        }

        private sealed class CacheRetentionTestStmt : AbstractStmt
        {
            internal void Initialize(int columnCount)
            {
                var fields = new TaosFieldAll[columnCount];
                for (var i = 0; i < fields.Length; i++)
                {
                    fields[i] = new TaosFieldAll
                    {
                        name = "value_" + i,
                        type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_INT,
                        bytes = sizeof(int),
                        field_type = (byte)TaosFieldType.TAOS_FIELD_COL
                    };
                }

                ApplyPrepareResult("insert into t values(?)", true, columnCount, fields);
            }

            protected override void PrepareInternal(string query, out bool isInsert, out int count,
                out TaosFieldAll[] fields)
            {
                throw new NotSupportedException();
            }

            protected override void BindBinaryInternal(byte[] data, out int affectedRows)
            {
                affectedRows = 1;
            }

            protected override bool IsConnectionAvailable(Exception exception)
            {
                return true;
            }

            protected override void ReconnectInternal()
            {
                throw new NotSupportedException();
            }

            protected override bool AutoReconnectInternal()
            {
                return false;
            }

            protected override IRows QueryResultInternal()
            {
                throw new NotSupportedException();
            }

            protected override IRows InsertResultInternal(int affectedRows)
            {
                throw new NotSupportedException();
            }

            public override void Dispose()
            {
            }
        }
    }
}
