using System.Threading.Tasks;
using TDengine.Driver;
using Xunit;

namespace Driver.Test.Client.Query
{
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
    }
}