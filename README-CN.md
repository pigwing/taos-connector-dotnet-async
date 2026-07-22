# TDengine.Connector.Async

[English](https://github.com/pigwing/taos-connector-dotnet-async/blob/3.0/README.md)

`TDengine.Connector.Async` 是一个由社区维护的 TDengine .NET 驱动，核心目标是提供真正异步的 WebSocket I/O。项目以官方 [`taosdata/taos-connector-dotnet` v3.2.1](https://github.com/taosdata/taos-connector-dotnet/tree/v3.2.1) 的公共 API 和协议能力为基线，同时恢复不依赖同步阻塞的异步调用链。

WebSocket 异步路径不需要安装本地 `taosc` 动态库，只要应用能够访问 `taosAdapter`，即可在 Windows、Linux、容器及其他 .NET 环境中使用。

> 本项目只维护 WebSocket 异步能力。`DbDriver.OpenAsync` 会拒绝 `protocol=Native`；Native async 不属于本项目范围。

## 主要特性

- 连接、查询、执行、结果拉取、stmt2、Schemaless 和 TMQ 全链路真异步。
- 所有公共异步 API 均支持 `CancellationToken`。
- 单条物理 WebSocket 使用后台接收循环，并按 `req_id` 分发响应，支持连接内并发请求。
- WebSocket 发送串行化，并限制协议负载大小，在高负载下保持可预期行为。
- 通过连接字符串无感启用共享异步连接池。
- 连接池支持 keepalive、最大生命周期、获取超时、退避重试、空闲维护、泄漏检测和运行指标。
- 支持多地址 failover、自动重连、taosAdapter HA 发现、TLS、Bearer Token 和 WebSocket 压缩。
- 补齐 typed getters，包括 `decimal` 和 `DateTimeOffset`。
- 支持 `DECIMAL`、`DECIMAL64`、`VARBINARY`、`GEOMETRY`、`BLOB` 的读写。
- 支持 `net45`、`net451`、`netstandard2.0`、`netstandard2.1`，以及 .NET 5 到 .NET 10。

## 安装

```bash
dotnet add package TDengine.Connector.Async --version 3.2.1.5
```

WebSocket 服务由 `taosAdapter` 提供，非 TLS 默认端口为 `6041`，实际部署也可以映射为其他端口。

## 快速开始

```csharp
using System;
using System.Threading;
using TDengine.Driver;
using TDengine.Driver.Client;

var builder = new ConnectionStringBuilder(
    "protocol=WebSocket;host=localhost;port=6041;" +
    "useSSL=false;username=root;password=taosdata;" +
    "enableCompression=true;autoReconnect=true");

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var cancellationToken = cts.Token;

await using var client = await DbDriver.OpenAsync(builder, cancellationToken);

await client.ExecAsync("create database if not exists async_demo", cancellationToken);
await client.ExecAsync("use async_demo", cancellationToken);
await client.ExecAsync(
    "create table if not exists meters(ts timestamp, current float, voltage int)",
    cancellationToken);
await client.ExecAsync("insert into meters values(now, 10.2, 220)", cancellationToken);

await using var rows = await client.QueryAsync(
    "select ts, current, voltage from meters order by ts desc limit 10",
    cancellationToken);

while (await rows.ReadAsync(cancellationToken))
{
    DateTime timestamp = rows.GetDateTime(0);
    float current = rows.GetFloat(1);
    int voltage = rows.GetInt32(2);
    Console.WriteLine($"{timestamp:O} current={current} voltage={voltage}");
}
```

应当先释放每个 `IRowsAsync` 和 `IStmtAsync`，再释放其所属 client。在支持 `IAsyncDisposable` 的目标框架中，优先使用 `await using`，确保服务端结果集和 statement 资源被异步释放。

## 查询与结果读取

`IRowsAsync` 提供字段元数据、空值判断、通用取值和强类型 getter：

```csharp
await using var rows = await client.QueryAsync(
    "select ts, value, amount, payload from measurements",
    cancellationToken);

while (await rows.ReadAsync(cancellationToken))
{
    if (rows.IsDBNull(1))
    {
        continue;
    }

    DateTimeOffset ts = rows.GetDateTimeOffset(0);
    double value = rows.GetDouble(1);
    decimal amount = rows.GetDecimal(2);
    byte[] payload = (byte[])rows.GetValue(3);
}
```

可用 API 包括 `GetByte`、`GetInt16`、`GetInt32`、`GetInt64`、`GetBoolean`、`GetDateTime`、`GetDateTimeOffset`、`GetDecimal`、`GetDouble`、`GetFloat`、`GetString`、`GetBytes`、`GetChars`、`GetValue` 和 `GetValues`。字段元数据包括名称、类型、长度、精度和小数位数。

## 连接池

连接池默认关闭。只需在连接字符串增加 `pooling=true`，不需要改变业务 API：每次 `DbDriver.OpenAsync` 获取一个逻辑租约，释放 client 时会把物理 WebSocket 连接归还共享池。

```csharp
var pooledBuilder = new ConnectionStringBuilder(
    "protocol=WebSocket;host=localhost;port=6041;db=async_demo;" +
    "useSSL=false;username=root;password=taosdata;" +
    "enableCompression=true;autoReconnect=true;" +
    "pooling=true;minPoolSize=2;maxPoolSize=10;" +
    "poolConnectionTimeout=00:00:10;" +
    "poolKeepaliveTime=00:02:00;" +
    "poolMaxLifetime=00:30:00;" +
    "poolHousekeepingInterval=00:00:30;" +
    "poolLeakDetectionThreshold=00:00:30");

await using var client = await DbDriver.OpenAsync(pooledBuilder, cancellationToken);
await client.ExecAsync("insert into meters values(now, 11.4, 221)", cancellationToken);
```

建议只配置一次 `ConnectionStringBuilder`，并发使用期间不要修改它。完整的标准化连接配置会标识一个共享池，其中包括凭据、数据库、地址和连接池参数。

### 连接池参数

时间参数支持固定格式 `TimeSpan`，例如 `00:00:10`；也支持 `500ms`、`10s`、`2m`、`1h` 等后缀。为兼容旧调用方，纯数字会按毫秒解析。生产配置推荐使用明确的 `TimeSpan` 格式。

| 连接字符串参数 | 默认值 | 说明 |
| --- | ---: | --- |
| `minPoolSize` | `0` | 后台维持的最小空闲物理连接数。 |
| `maxPoolSize` | `10` | 池内最大物理连接数。 |
| `poolConnectionTimeout` | `00:00:30` | 获取或创建连接的最长等待时间。 |
| `poolKeepaliveTime` | `00:02:00` | 空闲多久后执行轻量健康检查；`0` 表示关闭。 |
| `poolMaxLifetime` | `00:30:00` | 物理连接最长寿命，到期后在安全时机替换；`0` 表示关闭。 |
| `poolHousekeepingInterval` | `00:00:30` | 后台维护周期。 |
| `poolCreationRetryBackoff` | `00:00:00.100` | 创建连接失败后的初始等待时间。 |
| `poolMaxCreationRetryBackoff` | `00:00:02` | 指数退避的最大等待时间。 |
| `poolLeakDetectionThreshold` | `0` | 租约超过该时长后报告潜在泄漏；`0` 表示关闭。 |

`minPoolSize` 应按实际吞吐保守设置。不同进程、不同数据库或不同标准化连接配置都会建立独立连接池，并分别占用物理连接。

### 连接池指标

获取指标不需要借出连接：

```csharp
var metrics = DbDriver.GetWebSocketAsyncPoolMetrics(pooledBuilder);
if (metrics != null)
{
    Console.WriteLine(
        $"active={metrics.ActiveConnections}, " +
        $"idle={metrics.IdleConnections}, " +
        $"total={metrics.TotalConnections}, " +
        $"waiting={metrics.ThreadsAwaitingConnection}, " +
        $"avgAcquire={metrics.AverageAcquireDuration.TotalMilliseconds:F2}ms");
}
```

指标还包括 `MaintenanceConnections`、`AcquireCount`、`AcquireTimeoutCount`、`CreationCount`、`CreationFailureCount`、`DisposedConnectionCount`、`RecycledConnectionCount`、`KeepaliveCount`、`KeepaliveFailureCount` 和 `MaxAcquireDuration`。

匹配的共享池尚未创建时，`GetWebSocketAsyncPoolMetrics` 返回 `null`。`DbDriver.ClearWebSocketAsyncPools()` 只适合受控的应用退出或测试清理，不要在普通请求中调用。

需要明确管理池生命周期、启动预热或自定义泄漏回调时，可以使用 `DbDriver.CreateWebSocketAsyncPool`：

```csharp
using TDengine.Driver.Client.Websocket;

var options = new WSClientAsyncPoolOptions
{
    MinIdle = 2,
    MaximumPoolSize = 10,
    ConnectionTimeout = TimeSpan.FromSeconds(10),
    LeakDetectionThreshold = TimeSpan.FromSeconds(30),
    LeakDetected = leak => Console.Error.WriteLine(
        $"连接租约已持有 {leak.Elapsed}:{Environment.NewLine}{leak.StackTrace}")
};

await using var pool = DbDriver.CreateWebSocketAsyncPool(builder, options);
await pool.WarmupAsync(cancellationToken);
await using var leasedClient = await pool.AcquireAsync(cancellationToken);
```

## Failover 与自动重连

`host` 可以配置多个 taosAdapter 地址，每个地址可以单独携带端口：

```text
protocol=WebSocket;host=adapter-1:6041,adapter-2:6041;username=root;password=taosdata;autoReconnect=true
```

IPv6 地址需要方括号，例如 `[2001:db8::10]:6041`。标准化后重复的 endpoint 会被自动去除。

- `autoReconnect=true`：物理连接失败后执行重连和 failover。
- `reconnectRetryCount`：默认 `3` 次。
- `reconnectIntervalMs`：每轮重连间隔，默认 `2000` 毫秒。
- `adapterHA=true`：当 taosAdapter 支持 HA `list_instances` 响应时，启用实例发现和选择。

网络故障后的写入不能无条件重试，具体语义参见[异常与不确定写入](#异常与不确定写入)。

## Prepared Statement（stmt2）

异步 statement 使用 TDengine WebSocket stmt2 协议，支持按行和按列绑定：

```csharp
await using var stmt = await client.StmtInitAsync(cancellationToken);
await stmt.PrepareAsync(
    "insert into meters(ts, current, voltage) values(?, ?, ?)",
    cancellationToken);

await stmt.BindRowAsync(
    new object[] { DateTime.UtcNow, 12.5f, 223 },
    cancellationToken);
await stmt.AddBatchAsync(cancellationToken);
await stmt.ExecAsync(cancellationToken);

Console.WriteLine($"affected rows: {stmt.Affected()}");
```

超级表写入还可以使用 `SetTableNameAsync`、`SetTagsAsync`、`GetTagFieldsAsync`、`GetColFieldsAsync` 和 `BindColumnAsync`。Statement 是有状态对象，不要在同一个 `IStmtAsync` 实例上并发执行操作。

## Schemaless 写入

```csharp
var lines = new[]
{
    "meters,location=beijing current=10.3,voltage=220i 1721620800000000000"
};

await client.SchemalessInsertAsync(
    lines,
    TDengineSchemalessProtocol.TSDB_SML_LINE_PROTOCOL,
    TDengineSchemalessPrecision.TSDB_SML_TIMESTAMP_NANO_SECONDS,
    0,
    ReqId.GetReqId(),
    cancellationToken);
```

支持 Line、Telnet 和 JSON 协议。`ttl` 参数会传递给 `taosAdapter`，使用 `0` 表示采用服务端默认行为。

## WebSocket 异步 TMQ

`TMQConnectionAsync` 是底层真异步 WebSocket TMQ API：

```csharp
using System.Collections.Generic;
using TDengine.Driver.Impl.WebSocketMethods;

var config = new Dictionary<string, string>
{
    ["td.connect.type"] = "WebSocket",
    ["td.connect.ip"] = "localhost",
    ["td.connect.port"] = "6041",
    ["td.connect.user"] = "root",
    ["td.connect.pass"] = "taosdata",
    ["td.connect.db"] = "async_demo",
    ["group.id"] = "async-consumer-group",
    ["client.id"] = "async-consumer-1",
    ["auto.offset.reset"] = "earliest",
    ["enable.auto.commit"] = "false",
    ["msg.with.table.name"] = "true",
    ["useSSL"] = "false",
    ["ws.message.enableCompression"] = "true"
};

var tmqOptions = new TMQOptions(config);
var consumer = new TMQConnectionAsync(
    tmqOptions,
    TimeSpan.FromSeconds(10),
    TimeSpan.FromSeconds(30),
    TimeSpan.FromSeconds(10));

try
{
    await consumer.ConnectAsync(cancellationToken);
    await consumer.SubscribeAsync(
        new List<string> { "meters_topic" },
        tmqOptions,
        cancellationToken);

    var message = await consumer.PollAsync(5000, cancellationToken);
    if (message.HaveMessage)
    {
        byte[] rawBlock = await consumer.FetchRawBlockAsync(
            message.MessageId,
            cancellationToken);
        await consumer.CommitAsync(cancellationToken);
    }

    await consumer.UnsubscribeAsync(cancellationToken);
}
finally
{
    await consumer.CloseAsync();
}
```

TMQ 还支持 assignment、seek、position、committed offset、显式 offset commit 和订阅状态查询。`td.connect.ip` 同样支持逗号分隔的多地址 failover。

## 连接字符串参数

| 参数 | 说明 |
| --- | --- |
| `protocol` | 使用 `DbDriver.OpenAsync` 时必须为 `WebSocket`。 |
| `host` | taosAdapter 地址或逗号分隔的 failover 地址，每项可以包含端口。 |
| `port` | 未携带端口的 host 使用该端口；非 TLS 默认 `6041`，TLS 默认 `443`。 |
| `db` | 建立连接时选择的数据库，可选。 |
| `username`、`password` | TDengine 用户名和密码。 |
| `useSSL` | 是否使用 `wss`，默认 `false`。 |
| `token` | 添加到 WebSocket URL 的 Token，也可用于 TDengine Cloud。 |
| `bearerToken` | 在连接请求中发送的 Bearer Token。 |
| `timezone` | 使用 `TimeZoneInfo` 解析的客户端结果时区。 |
| `connectionTimezone` | 发送到服务端并用于结果转换的 IANA 时区，仅支持 .NET 6+，不能与 `timezone` 同时配置。 |
| `connTimeout` | 连接超时，默认 `00:01:00`。 |
| `readTimeout` | 单个响应读取超时，默认 `00:05:00`。 |
| `writeTimeout` | WebSocket 写入超时，默认 `00:00:10`。 |
| `enableCompression` | 在 .NET 6+ 启用 WebSocket per-message deflate。 |
| `autoReconnect` | 连接失败后启用重连和 failover。 |
| `reconnectRetryCount` | 重连轮数，默认 `3`。 |
| `reconnectIntervalMs` | 每轮重连间隔，默认 `2000` 毫秒。 |
| `adapterHA` | 请求并使用 taosAdapter HA 实例信息。 |
| `pooling` | 启用共享 WebSocket 异步连接池，默认 `false`。 |

连接字符串通常包含密码或 Token，不要把完整连接字符串写入日志。

## 异常与不确定写入

TDengine 服务端和协议错误会抛出 `TDengineError`。请求可能已经开始发送后发生网络错误时，会抛出 `TDengineWebSocketRequestException`：

```csharp
try
{
    await client.ExecAsync(sql, cancellationToken);
}
catch (TDengineWebSocketRequestException ex) when (ex.RequestMayHaveBeenSent)
{
    // 服务端可能已经执行该写入。应先核对结果，或者依赖业务幂等键，
    // 再决定是否重试。
    throw;
}
catch (TDengineError ex)
{
    Console.Error.WriteLine($"TDengine error 0x{ex.Code:x}: {ex.Message}");
    throw;
}
```

取消会传递到连接、发送、等待响应、结果拉取、statement、连接池获取和 TMQ 操作。如果取消与已经开始发送的写请求发生竞争，连接会被标记失效，并向调用方暴露“不确定结果”，不会静默重试写入。

如果调试器勾选了对所有 `WebSocketException` 的“抛出时中断”，远端关闭或重连过程中，可能停在 `ManagedWebSocket` 内部抛出的 first-chance exception 上。只有异常最终逃逸出驱动调用时才代表应用操作失败；接收循环会观察并处理预期的关闭异常。

## 数据类型映射

| TDengine 类型 | .NET 类型 |
| --- | --- |
| `BOOL` | `bool` |
| `TINYINT`、`SMALLINT`、`INT`、`BIGINT` | `sbyte`、`short`、`int`、`long` |
| 无符号整数类型 | `byte`、`ushort`、`uint`、`ulong` |
| `FLOAT`、`DOUBLE` | `float`、`double` |
| `DECIMAL`、`DECIMAL64` | `decimal` |
| `TIMESTAMP` | `DateTime`，也可以调用 `GetDateTimeOffset` |
| `NCHAR` | `string` |
| `BINARY`、`JSON`、`VARBINARY`、`GEOMETRY`、`BLOB` | `byte[]` |

## 并发使用建议

- 非池化的 `ITDengineClientAsync` 持有一条物理 WebSocket，可按 `req_id` 并发分发相互独立的 query/exec 请求。
- 池化的每次 `OpenAsync` 持有一个逻辑租约，直到 client 及其所有 rows/statement 子资源释放后才归还。
- 不要并发操作同一个 `IRowsAsync` 或 `IStmtAsync`；它们分别持有游标和 statement 状态。
- 应限制应用层并发。连接池获取超时和 `ThreadsAwaitingConnection` 是最重要的背压指标。
- 始终 await 操作和释放流程，不要使用 `.Result`、`.Wait()` 或 fire-and-forget 数据库调用。

## 兼容目标

NuGet 包包含以下目标框架：

```text
net45; net451; netstandard2.0; netstandard2.1;
net5; net6; net7; net8; net9; net10.0
```

WebSocket 压缩要求 .NET 6 或更高版本。`connectionTimezone` 同样要求 .NET 6+，并且必须使用 `Asia/Shanghai` 这类 IANA 时区 ID。

## 项目范围与上游

本仓库基于 TDengine 官方 .NET 驱动，并以官方 `v3.2.1` 为基线同步公共协议和数据类型行为。本 fork 持续维护的产品范围是 WebSocket 异步路径，包括连接池和异步 TMQ；不会提供 Native-driver async。

- 官方上游：[taosdata/taos-connector-dotnet](https://github.com/taosdata/taos-connector-dotnet)
- 本项目：[pigwing/taos-connector-dotnet-async](https://github.com/pigwing/taos-connector-dotnet-async)
- TDengine 文档：[docs.taosdata.com](https://docs.taosdata.com/)

## 许可证

[MIT](https://github.com/pigwing/taos-connector-dotnet-async/blob/3.0/LICENSE)
