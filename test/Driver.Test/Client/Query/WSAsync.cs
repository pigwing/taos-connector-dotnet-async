using System.Threading.Tasks;
using TDengine.Driver;
using Xunit;

namespace Driver.Test.Client.Query
{
    [Collection("WebSocket async collection")]
    public partial class ClientAsync
    {
        [Fact]
        public async Task WebSocketQueryMSTest()
        {
            var db = "ws_query_test_ms";
            await this.QueryAsyncTest(this._wsConnectString, db, TDenginePrecision.TSDB_TIME_PRECISION_MILLI);
        }

        [Fact]
        public async Task WebSocketQueryUSTest()
        {
            var db = "ws_query_test_us";
            await this.QueryAsyncTest(this._wsConnectString, db, TDenginePrecision.TSDB_TIME_PRECISION_MICRO);
        }

        [Fact]
        public async Task WebSocketQueryNSTest()
        {
            var db = "ws_query_test_ns";
            await this.QueryAsyncTest(this._wsConnectString, db, TDenginePrecision.TSDB_TIME_PRECISION_NANO);
        }

        [Fact]
        public async Task WebSocketQueryWithReqIDMSTest()
        {
            var db = "ws_query_test_reqid_ms";
            await this.QueryWithReqIDAsyncTest(this._wsConnectString, db, TDenginePrecision.TSDB_TIME_PRECISION_MILLI);
        }

        [Fact]
        public async Task WebSocketQueryWithReqIDUSTest()
        {
            var db = "ws_query_test_reqid_us";
             await this.QueryWithReqIDAsyncTest(this._wsConnectString, db, TDenginePrecision.TSDB_TIME_PRECISION_MICRO);
        }

        [Fact]
        public async Task WebSocketQueryWithReqIDNSTest()
        {
            var db = "ws_query_test_reqid_ns";
            await this.QueryWithReqIDAsyncTest(this._wsConnectString, db, TDenginePrecision.TSDB_TIME_PRECISION_NANO);
        }

        [Fact]
        public async Task WebSocketStmtMSTest()
        {
            var db = "ws_stmt_test_ms";
            await this.StmtAsyncTest(this._wsConnectString, db, TDenginePrecision.TSDB_TIME_PRECISION_MILLI);
        }

        [Fact]
        public async Task WebSocketStmtUSTest()
        {
            var db = "ws_stmt_test_us";
            await this.StmtAsyncTest(this._wsConnectString, db, TDenginePrecision.TSDB_TIME_PRECISION_MICRO);
        }

        [Fact]
        public async Task WebSocketStmtNSTest()
        {
            var db = "ws_stmt_test_ns";
            await this.StmtAsyncTest(this._wsConnectString, db, TDenginePrecision.TSDB_TIME_PRECISION_NANO);
        }

        [Fact]
        public async Task WebSocketStmtWithReqIDMSTest()
        {
            var db = "ws_stmt_test_req_ms";
            await this.StmtWithReqIDAsyncTest(this._wsConnectString, db, TDenginePrecision.TSDB_TIME_PRECISION_MILLI);
        }

        [Fact]
        public async Task WebSocketStmtWithReqIDUSTest()
        {
            var db = "ws_stmt_test_req_us";
            await this.StmtWithReqIDAsyncTest(this._wsConnectString, db, TDenginePrecision.TSDB_TIME_PRECISION_MICRO);
        }

        [Fact]
        public async Task WebSocketStmtWithReqIDNSTest()
        {
            var db = "ws_stmt_test_req_ns";
            await this.StmtWithReqIDAsyncTest(this._wsConnectString, db, TDenginePrecision.TSDB_TIME_PRECISION_NANO);
        }

        [Fact]
        public async Task WebSocketStmtColumnsMSTest()
        {
            var db = "ws_stmt_columns_test_ms";
            await this.StmtBindColumnsAsyncTest(this._wsConnectString, db, TDenginePrecision.TSDB_TIME_PRECISION_MILLI);
        }

        [Fact]
        public async Task WebSocketStmtColumnsUSTest()
        {
            var db = "ws_stmt_columns_test_us";
            await this.StmtBindColumnsAsyncTest(this._wsConnectString, db, TDenginePrecision.TSDB_TIME_PRECISION_MICRO);
        }

        [Fact]
        public async Task WebSocketStmtColumnsNSTest()
        {
            var db = "ws_stmt_columns_test_ns";
            await this.StmtBindColumnsAsyncTest(this._wsConnectString, db, TDenginePrecision.TSDB_TIME_PRECISION_NANO);
        }

        [Fact]
        public async Task WebSocketVarbinaryTest()
        {
            var db = "ws_varbinary_test";
            await this.VarbinaryAsyncTest(this._wsConnectString, db);
        }

        [Fact]
        public async Task WebSocketInfluxDBTest()
        {
            var db = "ws_influxdb_test";
            await this.InfluxDBAsyncTest(this._wsConnectString, db);
        }

        [Fact]
        public async Task WebSocketTelnetTest()
        {
            var db = "ws_telnet_test";
            await this.TelnetAsyncTest(this._wsConnectString, db);
        }

        [Fact]
        public async Task WebSocketSMLJsonTest()
        {
            var db = "ws_sml_json_test";
            await this.SMLJsonAsyncTest(this._wsConnectString, db);
        }

        [Fact]
        public async Task WebSocketAsyncConnectionAvailableTest()
        {
            await this.ConnectionAvailableAsyncTest(this._wsConnectString);
        }

        [Fact]
        public async Task WebSocketAsyncOpenWithCancelledTokenTest()
        {
            await this.OpenWithCancelledTokenAsyncTest(this._wsConnectString);
        }

        [Fact]
        public async Task WebSocketAsyncCancelledOperationsDoNotBreakConnectionTest()
        {
            var db = "ws_cancelled_ops_recovery_test";
            await this.CancelledOperationsDoNotBreakConnectionAsyncTest(this._wsConnectString, db);
        }

        [Fact]
        public async Task WebSocketAsyncUnicodeSqlPayloadTest()
        {
            var db = "ws_unicode_sql_payload_test";
            await this.UnicodeSqlPayloadAsyncTest(this._wsConnectString, db);
        }

        [Fact]
        public async Task WebSocketAsyncConcurrentQueryAndFetchTest()
        {
            var db = "ws_concurrent_query_fetch_test";
            await this.ConcurrentQueryAndFetchAsyncTest(this._wsConnectString, db);
        }

        [Fact]
        public async Task WebSocketAsyncConcurrentInsertAndQueryStressTest()
        {
            var db = "ws_concurrent_insert_query_stress_test";
            await this.ConcurrentInsertAndQueryStressAsyncTest(this._wsConnectString, db);
        }

        [Fact]
        public async Task WebSocketAsyncRowsMetadataPrecisionScaleTest()
        {
            await this.RowsMetadataPrecisionScaleAsyncTest();
        }

        [Fact]
        public async Task WebSocketAsyncRowsCancelledReadPreservesInFlightFetchTest()
        {
            await this.RowsCancelledReadPreservesInFlightFetchAsyncTest();
        }

        [Fact]
        public async Task WebSocketAsyncRowsDisposeCancelsInFlightFetchTest()
        {
            await this.RowsDisposeCancelsInFlightFetchAsyncTest();
        }

        [Fact]
        public async Task WebSocketAsyncRowsInvalidFetchBlockThrowsProtocolErrorTest()
        {
            await this.RowsInvalidFetchBlockThrowsProtocolErrorAsyncTest();
        }

        [Fact]
        public async Task WebSocketAsyncUpdateRowsReadReturnsFalseTest()
        {
            await this.UpdateRowsReadAsyncReturnsFalseTest();
        }

        [Fact]
        public async Task WebSocketAsyncRowsDisposeIsIdempotentAndRejectsReadsTest()
        {
            var db = "ws_rows_dispose_behavior_test";
            await this.RowsDisposeIsIdempotentAndRejectsReadsAsyncTest(this._wsConnectString, db);
        }

        [Fact]
        public async Task WebSocketAsyncReadCancellationDoesNotCompleteRowsTest()
        {
            var db = "ws_read_cancel_recovery_test";
            await this.ReadCancellationDoesNotCompleteRowsAsyncTest(this._wsConnectString, db);
        }

        [Fact]
        public async Task WebSocketAsyncStmtDisposeIsIdempotentAndRejectsUseTest()
        {
            await this.StmtDisposeIsIdempotentAndRejectsUseAsyncTest(this._wsConnectString);
        }

        [Fact]
        public async Task WebSocketAsyncStmtExecFailureResetsExecutedStateTest()
        {
            var db = "ws_stmt_exec_failure_state_test";
            await this.StmtExecFailureResetsExecutedStateAsyncTest(this._wsConnectString, db);
        }

        [Fact]
        public async Task WebSocketAsyncStmtMultipleAddBatchDirectTableTest()
        {
            var db = "ws_stmt_multi_batch_direct_test";
            await this.StmtMultipleAddBatchDirectTableAsyncTest(this._wsConnectString, db);
        }

        [Fact]
        public async Task WebSocketAsyncStmtAddBatchResetsColumnStateTest()
        {
            var db = "ws_stmt_add_batch_state_test";
            await this.StmtAddBatchResetsColumnStateAsyncTest(this._wsConnectString, db);
        }

        [Fact]
        public async Task WebSocketAsyncClientDisposeDoesNotHangWithOpenRowsTest()
        {
            var db = "ws_client_dispose_open_rows_test";
            await this.ClientDisposeDoesNotHangWithOpenRowsAsyncTest(this._wsConnectString, db);
        }

        [Fact]
        public async Task WebSocketAsyncClientRepeatedDisposeAfterQueryTest()
        {
            var db = "ws_client_repeated_dispose_test";
            await this.ClientRepeatedDisposeAfterQueryAsyncTest(this._wsConnectString, db);
        }

        [Fact]
        public async Task WebSocketAsyncClientDisposeDoesNotCancelHttpConnectionReceiveTest()
        {
            var db = "ws_client_dispose_http_cancel_test";
            await this.ClientDisposeDoesNotCancelHttpConnectionReceiveAsyncTest(this._wsConnectString, db);
        }
    }
}
