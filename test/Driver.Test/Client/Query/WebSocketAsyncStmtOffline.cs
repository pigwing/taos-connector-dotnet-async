using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.WebSockets;
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
    public sealed class WebSocketAsyncStmtOffline
    {
        [Fact]
        public async Task UncertainBindOutcomeInvalidatesPhysicalConnection()
        {
            var bindReceived = NewCompletionSource<bool>();
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var init = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Init,
                    WebSocketTestProtocol.GetRequestId(init), new JObject { ["stmt_id"] = 71UL }, false, token)
                    .ConfigureAwait(false);
                var prepare = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await SendSingleIntPrepareResponseAsync(socket, prepare, 71, token).ConfigureAwait(false);

                var bind = await WebSocketTestProtocol.ReceiveAsync(socket, token).ConfigureAwait(false);
                Assert.Equal(WebSocketMessageType.Binary, bind.MessageType);
                bindReceived.TrySetResult(true);
                await WaitForDisconnectAsync(socket, token).ConfigureAwait(false);
            });

            var client = new WSClientAsync(CreateBuilder(server.Port));
            await client.ConnectAsync().ConfigureAwait(false);
            var stmt = await client.StmtInitAsync(7001).ConfigureAwait(false);
            await stmt.PrepareAsync("insert into t values(?)").ConfigureAwait(false);
            await stmt.BindRowAsync(new object[] { 1 }).ConfigureAwait(false);
            await stmt.AddBatchAsync().ConfigureAwait(false);

            using (var cancellation = new CancellationTokenSource())
            {
                var execTask = stmt.ExecAsync(cancellation.Token);
                await bindReceived.Task.ConfigureAwait(false);
                cancellation.Cancel();
                var exception = await Assert.ThrowsAsync<TDengineWebSocketRequestException>(() => execTask)
                    .ConfigureAwait(false);
                Assert.True(exception.RequestMayHaveBeenSent);
            }

            Assert.False(client.ConnectionAvailable());
            await stmt.DisposeAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task ReturnedFieldMetadataCannotMutatePreparedStatement()
        {
            await using var server = new LoopbackWebSocketServer(async (socket, _, token) =>
            {
                await CompleteConnectionHandshakeAsync(socket, token).ConfigureAwait(false);
                var init = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Init,
                    WebSocketTestProtocol.GetRequestId(init), new JObject { ["stmt_id"] = 72UL }, false, token)
                    .ConfigureAwait(false);
                var prepare = await WebSocketTestProtocol.ReceiveJsonAsync(socket, token).ConfigureAwait(false);
                await SendSingleIntPrepareResponseAsync(socket, prepare, 72, token).ConfigureAwait(false);
                await CompleteCloseHandshakeAsync(socket, token).ConfigureAwait(false);
            });

            var client = new WSClientAsync(CreateBuilder(server.Port));
            await client.ConnectAsync().ConfigureAwait(false);
            var stmt = await client.StmtInitAsync(7002).ConfigureAwait(false);
            await stmt.PrepareAsync("insert into t values(?)").ConfigureAwait(false);

            var first = await stmt.GetColFieldsAsync().ConfigureAwait(false);
            first[0].name = "mutated";
            first[0].type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_DOUBLE;
            var second = await stmt.GetColFieldsAsync().ConfigureAwait(false);

            Assert.NotSame(first, second);
            Assert.Equal("value", second[0].name);
            Assert.Equal((sbyte)TDengineDataType.TSDB_DATA_TYPE_INT, second[0].type);
            await stmt.DisposeAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public void TableNameWithNullCharacterIsRejected()
        {
            using var stmt = new StmtPayloadProbe();
            stmt.InitializeTableInsert();

            Assert.Throws<ArgumentException>(() => stmt.SetTableName("valid\0different"));
        }

        [Fact]
        public void OversizedUtf8TableNameIsRejectedBeforePayloadAllocation()
        {
            using var stmt = new StmtPayloadProbe();
            stmt.InitializeTableInsert();
            stmt.SetTableName(new string('x', ushort.MaxValue));
            stmt.SetTags(new object[] { "tag" });
            stmt.BindRow(new object[] { 1 });
            stmt.AddBatch();

            Assert.Throws<ArgumentException>(() => stmt.CapturePooledPayload());
        }

        [Fact]
        public void RepeatedTableBatchesAreMergedWithoutLosingRows()
        {
            using var stmt = new StmtPayloadProbe();
            stmt.InitializeTableInsert();
            AddTableBatch(stmt, "table_1", "tag", 11);
            AddTableBatch(stmt, "table_1", "tag", 22);

            var payload = stmt.CapturePooledPayload();
            const int websocketHeaderLength = 30;
            var tableCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(websocketHeaderLength + 4));
            var columnsOffset = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(websocketHeaderLength + 24));
            var columnBindOffset = checked(websocketHeaderLength + (int)columnsOffset + sizeof(uint));
            var rowCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(columnBindOffset + 8));
            var valuesOffset = checked(columnBindOffset + 17 + (int)rowCount);

            Assert.Equal(1U, tableCount);
            Assert.Equal(2U, rowCount);
            Assert.Equal(11, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(valuesOffset)));
            Assert.Equal(22, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(valuesOffset + sizeof(int))));
        }

        [Fact]
        public void RepeatedTableBatchRejectsChangedTags()
        {
            using var stmt = new StmtPayloadProbe();
            stmt.InitializeTableInsert();
            AddTableBatch(stmt, "table_1", "tag-a", 11);
            stmt.SetTableName("table_1");
            stmt.SetTags(new object[] { "tag-b" });
            stmt.BindRow(new object[] { 22 });

            Assert.Throws<InvalidOperationException>(() => stmt.AddBatch());
        }

        private static void AddTableBatch(StmtPayloadProbe stmt, string tableName, string tag, int value)
        {
            stmt.SetTableName(tableName);
            stmt.SetTags(new object[] { tag });
            stmt.BindRow(new object[] { value });
            stmt.AddBatch();
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
            Assert.Equal(WSAction.Conn, WebSocketTestProtocol.GetAction(connect));
            await WebSocketTestProtocol.SendResponseAsync(socket, WSAction.Conn,
                WebSocketTestProtocol.GetRequestId(connect), new JObject(), false, cancellationToken)
                .ConfigureAwait(false);
        }

        private static Task SendSingleIntPrepareResponseAsync(WebSocket socket, JObject prepare, ulong stmtId,
            CancellationToken cancellationToken)
        {
            return WebSocketTestProtocol.SendResponseAsync(socket, WSAction.STMT2Prepare,
                WebSocketTestProtocol.GetRequestId(prepare), new JObject
                {
                    ["stmt_id"] = stmtId,
                    ["is_insert"] = true,
                    ["fields_count"] = 1,
                    ["fields"] = new JArray(new JObject
                    {
                        ["name"] = "value",
                        ["field_type"] = (int)TDengineDataType.TSDB_DATA_TYPE_INT,
                        ["precision"] = 0,
                        ["scale"] = 0,
                        ["bytes"] = sizeof(int),
                        ["bind_type"] = (int)TaosFieldType.TAOS_FIELD_COL
                    })
                }, false, cancellationToken);
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

        private static async Task WaitForDisconnectAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            try
            {
                await CompleteCloseHandshakeAsync(socket, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
            }
            catch (IOException)
            {
            }
        }

        private static TaskCompletionSource<T> NewCompletionSource<T>()
        {
            return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class StmtPayloadProbe : AbstractStmt
        {
            internal StmtPayloadProbe() : base(30)
            {
            }

            internal void InitializeTableInsert()
            {
                ApplyPrepareResult("insert into ? using stable tags(?) values(?)", true, 3, new[]
                {
                    new TaosFieldAll
                    {
                        name = "table_name",
                        type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_BINARY,
                        field_type = (byte)TaosFieldType.TAOS_FIELD_TBNAME
                    },
                    new TaosFieldAll
                    {
                        name = "tag",
                        type = (sbyte)TDengineDataType.TSDB_DATA_TYPE_NCHAR,
                        bytes = 64,
                        field_type = (byte)TaosFieldType.TAOS_FIELD_TAG
                    },
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
                throw new NotSupportedException();
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
