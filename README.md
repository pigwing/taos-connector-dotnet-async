# TDengine.Connector.Async

[简体中文](https://github.com/pigwing/taos-connector-dotnet-async/blob/3.0/README-CN.md)

`TDengine.Connector.Async` is a community-maintained TDengine .NET connector focused on true asynchronous WebSocket I/O. It follows the public API and protocol capabilities of the official [`taosdata/taos-connector-dotnet` v3.2.1](https://github.com/taosdata/taos-connector-dotnet/tree/v3.2.1), while restoring an async path that does not block on synchronous WebSocket calls.

The WebSocket async path does not require the local `taosc` native library. It is suitable for Windows, Linux, containers, and other .NET environments that can reach `taosAdapter`.

> This package intentionally supports async through WebSocket only. `DbDriver.OpenAsync` rejects `protocol=Native`; Native async is outside this project's scope.

## Features

- True async connect, query, execute, result fetch, stmt2, schemaless, and TMQ operations.
- `CancellationToken` support throughout the public async API.
- One background receive loop with `req_id` response dispatch, allowing concurrent requests on one physical WebSocket connection.
- Serialized WebSocket sends and bounded protocol payloads for predictable behavior under load.
- Optional shared async connection pool enabled directly in the connection string.
- Pool keepalive, maximum lifetime, acquire timeout, retry backoff, idle maintenance, leak detection, and runtime metrics.
- Multi-address failover, automatic reconnect, adapter HA discovery, TLS, bearer token, and WebSocket compression.
- Typed row getters, including `decimal` and `DateTimeOffset`.
- `DECIMAL`, `DECIMAL64`, `VARBINARY`, `GEOMETRY`, and `BLOB` read/write support.
- Targets `net45`, `net451`, `netstandard2.0`, `netstandard2.1`, and .NET 5 through .NET 10.

## Installation

```bash
dotnet add package TDengine.Connector.Async --version 3.2.1.5
```

The WebSocket endpoint is provided by `taosAdapter`. Its default non-TLS port is `6041`; deployments may expose a different port.

## Quick Start

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

Dispose each `IRowsAsync` and `IStmtAsync` before disposing its client. On targets that support `IAsyncDisposable`, prefer `await using` so server-side result and statement resources are released asynchronously.

## Query and Row Access

`IRowsAsync` exposes metadata, null checks, generic values, and typed getters:

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

Available row APIs include `GetByte`, `GetInt16`, `GetInt32`, `GetInt64`, `GetBoolean`, `GetDateTime`, `GetDateTimeOffset`, `GetDecimal`, `GetDouble`, `GetFloat`, `GetString`, `GetBytes`, `GetChars`, `GetValue`, and `GetValues`. Metadata includes field name, type, size, precision, and scale.

## Connection Pooling

Pooling is opt-in and is enabled without changing application APIs. Add `pooling=true` to the connection string; every `DbDriver.OpenAsync` acquires a logical lease, and disposing that client returns its physical WebSocket connection to the shared pool.

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

Configure a builder once and do not mutate it while it is being used concurrently. The complete normalized connection configuration, including credentials and pool settings, identifies a shared pool.

### Pool options

Time values accept invariant `TimeSpan` text such as `00:00:10`, suffixes such as `500ms`, `10s`, `2m`, or `1h`, and legacy bare numbers interpreted as milliseconds. `TimeSpan` text is recommended.

| Connection string option | Default | Purpose |
| --- | ---: | --- |
| `minPoolSize` | `0` | Minimum idle physical connections maintained in the background. |
| `maxPoolSize` | `10` | Maximum physical connections in the pool. |
| `poolConnectionTimeout` | `00:00:30` | Maximum wait to acquire or create a connection. |
| `poolKeepaliveTime` | `00:02:00` | Idle duration before a lightweight health check; `0` disables it. |
| `poolMaxLifetime` | `00:30:00` | Maximum physical connection age; replacement occurs when safe; `0` disables it. |
| `poolHousekeepingInterval` | `00:00:30` | Background maintenance interval. |
| `poolCreationRetryBackoff` | `00:00:00.100` | Initial delay after connection creation failure. |
| `poolMaxCreationRetryBackoff` | `00:00:02` | Maximum exponential creation retry delay. |
| `poolLeakDetectionThreshold` | `0` | Lease duration before reporting a potential leak; `0` disables it. |

Keep `minPoolSize` conservative. A pool exists per distinct normalized connection string, so every application process and database configuration has its own physical connections.

### Pool metrics

Metrics are snapshots and can be read without taking a connection lease:

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

The snapshot also contains `MaintenanceConnections`, `AcquireCount`, `AcquireTimeoutCount`, `CreationCount`, `CreationFailureCount`, `DisposedConnectionCount`, `RecycledConnectionCount`, `KeepaliveCount`, `KeepaliveFailureCount`, and `MaxAcquireDuration`.

`GetWebSocketAsyncPoolMetrics` returns `null` until the matching shared pool has been created. `DbDriver.ClearWebSocketAsyncPools()` is intended for controlled application shutdown or tests, not routine request handling.

For explicit ownership, warmup, or a custom leak callback, use `DbDriver.CreateWebSocketAsyncPool` with `WSClientAsyncPoolOptions`:

```csharp
using TDengine.Driver.Client.Websocket;

var options = new WSClientAsyncPoolOptions
{
    MinIdle = 2,
    MaximumPoolSize = 10,
    ConnectionTimeout = TimeSpan.FromSeconds(10),
    LeakDetectionThreshold = TimeSpan.FromSeconds(30),
    LeakDetected = leak => Console.Error.WriteLine(
        $"Connection lease held for {leak.Elapsed}:{Environment.NewLine}{leak.StackTrace}")
};

await using var pool = DbDriver.CreateWebSocketAsyncPool(builder, options);
await pool.WarmupAsync(cancellationToken);
await using var leasedClient = await pool.AcquireAsync(cancellationToken);
```

## taosAdapter HA, Failover, and Reconnect

### Dynamic adapter HA discovery

Enable adapter HA directly in the normal connection string. No separate client or pool API is required:

```csharp
var haBuilder = new ConnectionStringBuilder(
    "protocol=WebSocket;" +
    "host=adapter-1.example.com:6041,adapter-2.example.com:6041;" +
    "db=async_demo;username=root;password=taosdata;useSSL=false;" +
    "adapterHA=true;autoReconnect=true;" +
    "reconnectRetryCount=3;reconnectIntervalMs=2000;" +
    "pooling=true;minPoolSize=2;maxPoolSize=10");

await using var client = await DbDriver.OpenAsync(haBuilder, cancellationToken);
```

With `adapterHA=true`, each successful connection asks taosAdapter for its `list_instances` response. The connector validates and de-duplicates those `host:port` endpoints, merges them with the configured seed addresses, and caches the cluster map in the current process for 30 minutes. New clients and pooled physical connections using the same seeds can reuse that discovered map.

`autoReconnect=true` is required for automatic recovery after an established connection fails. Reconnect attempts use both configured seeds and discovered instances. `adapterHA=true` without `autoReconnect=true` performs discovery but does not recover a failed request connection automatically.

Production requirements:

- Configure at least two independent seed endpoints in `host`. If a process starts with an empty discovery cache and its only seed is down, it cannot discover the remaining instances.
- taosAdapter must support `list_instances`. If the response is absent or empty, the connection remains usable but failover is limited to the configured seeds.
- Every advertised instance address must be reachable from the application. In Docker or Kubernetes, do not advertise container-only IPs or `127.0.0.1` unless the client shares that network namespace.
- All discovered instances use the connection's `useSSL`, credentials, token, and database settings.
- Pooling needs no special handling: `pooling=true` creates HA-aware physical WebSocket connections and replaces failed ones before returning them to borrowers.

### Static multi-address failover

Comma-separated `host` values also work without dynamic discovery:

```text
protocol=WebSocket;host=adapter-1:6041,adapter-2:6041;username=root;password=taosdata;adapterHA=false;autoReconnect=true
```

An endpoint may include its own port. Use bracketed IPv6 endpoints, for example `[2001:db8::10]:6041`. Duplicate endpoints are removed after normalization. `reconnectRetryCount` defaults to `3`, and `reconnectIntervalMs` defaults to `2000` milliseconds.

Do not blindly retry a write after an uncertain network failure. See [Failure semantics](#failure-semantics).

## Prepared Statements (stmt2)

The async statement implementation uses TDengine's stmt2 WebSocket protocol and supports row and column binding.

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

Supertable inserts can use `SetTableNameAsync`, `SetTagsAsync`, `GetTagFieldsAsync`, `GetColFieldsAsync`, and `BindColumnAsync`. A statement is stateful; do not execute concurrent operations on the same `IStmtAsync` instance.

## Schemaless Insert

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

Line, Telnet, and JSON protocols are supported. The `ttl` argument is passed to `taosAdapter`; use `0` for its default behavior.

## Async TMQ over WebSocket

`TMQConnectionAsync` is the low-level true-async WebSocket TMQ API:

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

TMQ also supports assignment, seek, position, committed offset, explicit offset commit, and subscription inspection APIs. Multi-address `td.connect.ip` values use the same comma-separated failover format.

`TMQConnectionAsync` is a low-level API. To request the adapter instance list, call the overload with `listInstances` enabled and inspect `ListInstances` in the response:

```csharp
var subscription = await consumer.SubscribeAsync(
    new List<string> { "meters_topic" },
    tmqOptions,
    true,
    cancellationToken);

string[] adapterInstances = subscription.ListInstances;
```

The higher-level WebSocket TMQ consumer enables automatic adapter switching with `ws.adapterHA=true`, `ws.autoReconnect=true`, `ws.reconnect.retry.count`, and `ws.reconnect.interval.ms`. These TMQ keys are separate from the SQL client's connection-string keys.

## Connection String Reference

| Option | Description |
| --- | --- |
| `protocol` | Must be `WebSocket` for `DbDriver.OpenAsync`. |
| `host` | Adapter host or comma-separated failover endpoints. Each endpoint may include a port. |
| `port` | Fallback port for host entries without one. Defaults to `6041` without TLS and `443` with TLS. |
| `db` | Optional database selected during connection. |
| `username`, `password` | TDengine credentials. |
| `useSSL` | Use `wss`; default `false`. |
| `token` | Token added to the WebSocket URL, including TDengine Cloud scenarios. |
| `bearerToken` | Bearer token sent in the connection request. |
| `timezone` | Client-side result timezone resolved with `TimeZoneInfo`. |
| `connectionTimezone` | IANA timezone sent to the server and used for results; .NET 6+ only. Cannot be combined with `timezone`. |
| `connTimeout` | Connect timeout; default `00:01:00`. |
| `readTimeout` | Per-response read timeout; default `00:05:00`. |
| `writeTimeout` | WebSocket write timeout; default `00:00:10`. |
| `enableCompression` | Enable WebSocket per-message deflate on .NET 6+. |
| `autoReconnect` | Enable reconnect/failover after a connection failure. |
| `reconnectRetryCount` | Number of reconnect passes; default `3`. |
| `reconnectIntervalMs` | Delay between reconnect passes; default `2000`. |
| `adapterHA` | Request `list_instances` from taosAdapter and add discovered endpoints to reconnect/failover. |
| `pooling` | Enable the shared WebSocket async pool; default `false`. |

Do not log complete connection strings because they commonly contain passwords or tokens.

## Failure Semantics

TDengine server and protocol failures are reported as `TDengineError`. Network failures that occur after a request may have started sending are reported as `TDengineWebSocketRequestException`:

```csharp
try
{
    await client.ExecAsync(sql, cancellationToken);
}
catch (TDengineWebSocketRequestException ex) when (ex.RequestMayHaveBeenSent)
{
    // The server may have applied the write. Reconcile or use an idempotent
    // application operation before deciding whether to retry.
    throw;
}
catch (TDengineError ex)
{
    Console.Error.WriteLine($"TDengine error 0x{ex.Code:x}: {ex.Message}");
    throw;
}
```

Cancellation is propagated through connect, send, response wait, fetch, statement, pool acquire, and TMQ operations. If cancellation races with a write that has already begun sending, the connection is invalidated and the uncertain outcome is surfaced rather than silently retrying the write.

When a debugger is configured to break on every thrown `WebSocketException`, it can stop on first-chance exceptions raised internally by `ManagedWebSocket` during a remote close or reconnect. An exception is an application failure only when it escapes the connector call; expected shutdown exceptions are observed and handled by the receive loop.

## Data Type Mapping

| TDengine type | .NET type |
| --- | --- |
| `BOOL` | `bool` |
| `TINYINT`, `SMALLINT`, `INT`, `BIGINT` | `sbyte`, `short`, `int`, `long` |
| Unsigned integer types | `byte`, `ushort`, `uint`, `ulong` |
| `FLOAT`, `DOUBLE` | `float`, `double` |
| `DECIMAL`, `DECIMAL64` | `decimal` |
| `TIMESTAMP` | `DateTime`; `GetDateTimeOffset` is also available |
| `NCHAR` | `string` |
| `BINARY`, `JSON`, `VARBINARY`, `GEOMETRY`, `BLOB` | `byte[]` |

## Concurrency Guidance

- A non-pooled `ITDengineClientAsync` owns one physical WebSocket and can dispatch independent query/execute requests concurrently by `req_id`.
- A pooled `OpenAsync` call owns one logical lease until the client and all child rows/statements are disposed.
- Do not concurrently mutate or read the same `IRowsAsync` or `IStmtAsync` instance; each has cursor or statement state.
- Bound application concurrency. Pool acquire timeout and `ThreadsAwaitingConnection` are the primary backpressure signals.
- Always await operations and disposal; do not use `.Result`, `.Wait()`, or fire-and-forget database calls.

## Compatibility

The connector is built for:

```text
net45; net451; netstandard2.0; netstandard2.1;
net5; net6; net7; net8; net9; net10.0
```

WebSocket compression requires .NET 6 or later. `connectionTimezone` also requires .NET 6 or later and an IANA timezone ID such as `Asia/Shanghai`.

## Project Scope and Upstream

This repository is based on the official TDengine .NET connector and keeps its shared protocol and data-type behavior aligned with official version `v3.2.1`. The maintained product surface in this fork is the WebSocket async path, including its connection pool and async TMQ implementation. Native-driver async support is intentionally not provided.

- Upstream: [taosdata/taos-connector-dotnet](https://github.com/taosdata/taos-connector-dotnet)
- This project: [pigwing/taos-connector-dotnet-async](https://github.com/pigwing/taos-connector-dotnet-async)
- TDengine documentation: [docs.tdengine.com](https://docs.tdengine.com/)

## License

[MIT](https://github.com/pigwing/taos-connector-dotnet-async/blob/3.0/LICENSE)
