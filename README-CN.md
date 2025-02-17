# C# 连接器

[English edition](./README.md)

`TDengine.Connector` 是 TDengine 提供的 C# 语言连接器。C# 开发人员可以通过它开发存取 TDengine 集群数据的 C# 应用软件。

`TDengine.Connector` 连接器支持通过 TDengine 客户端驱动（taosc）建立与 TDengine 运行实例的连接，提供数据写入、查询、数据订阅、schemaless 数据写入、参数绑定接口数据写入等功能。 `TDengine.Connector` 自 v3.0.1 起还支持 WebSocket 连接，提供数据写入、查询、参数绑定接口数据写入等功能。

本文介绍如何在 Linux 或 Windows 环境中安装 `TDengine.Connector`，并通过 `TDengine.Connector` 连接 TDengine 集群，进行数据写入、查询等基本操作。

**注意**：

* `TDengine.Connector` 3.x 不兼容 TDengine 2.x，如果在运行 TDengine 2.x 版本的环境下需要使用 C# 连接器请使用 TDengine.Connector 的 1.x 版本 。
* `TDengine.Connector` 3.1.0 版本进行了完整的重构，不再兼容 3.0.2 及以前版本。3.0.2 文档请参考 [nuget](https://www.nuget.org/packages/TDengine.Connector/3.0.2)

`TDengine.Connector` 的源码托管在 [GitHub](https://github.com/taosdata/taos-connector-dotnet/tree/3.0)。

## 支持的平台

支持的平台和 TDengine 客户端驱动支持的平台一致。

**注意** TDengine 不再支持 32 位 Windows 平台。

## 版本支持

| **Connector 版本** | **TDengine 版本**  | **主要功能**                   |
|------------------|------------------|----------------------------|
| 3.1.4            | 3.3.2.0/3.1.2.0  | 提升 websocket 查询和写入性能       |
| 3.1.3            | 3.2.1.0/3.1.1.18 | 支持 WebSocket 自动重连          |
| 3.1.2            | 3.2.1.0/3.1.1.18 | 修复 schemaless 资源释放         |
| 3.1.1            | 3.2.1.0/3.1.1.18 | 支持 varbinary 和 geometry 类型 |
| 3.1.0            | 3.2.1.0/3.1.1.18 | WebSocket 使用原生实现           |

## 处理异常

`TDengine.Connector` 会抛出异常，应用程序需要处理异常。taosc 异常类型 `TDengineError`，包含错误码和错误信息，应用程序可以根据错误码和错误信息进行处理。

## TDengine DataType 和 C# DataType

| TDengine DataType | C# Type          |
|-------------------|------------------|
| TIMESTAMP         | DateTime         |
| TINYINT           | sbyte            |
| SMALLINT          | short            |
| INT               | int              |
| BIGINT            | long             |
| TINYINT UNSIGNED  | byte             |
| SMALLINT UNSIGNED | ushort           |
| INT UNSIGNED      | uint             |
| BIGINT UNSIGNED   | ulong            |
| FLOAT             | float            |
| DOUBLE            | double           |
| BOOL              | bool             |
| BINARY            | byte[]           |
| NCHAR             | string (utf-8编码) |
| JSON              | byte[]           |
| VARBINARY         | byte[]           |
| GEOMETRY          | byte[]           |

**注意**：JSON 类型仅在 tag 中支持。

## 安装步骤

### 安装前准备

* 安装 [.NET SDK](https://dotnet.microsoft.com/download)
* [Nuget 客户端](https://docs.microsoft.com/en-us/nuget/install-nuget-client-tools) （可选安装）
* 安装 TDengine 客户端驱动，具体步骤请参考[安装客户端驱动](https://docs.taosdata.com/develop/connect/#%E5%AE%89%E8%A3%85%E5%AE%A2%E6%88%B7%E7%AB%AF%E9%A9%B1%E5%8A%A8-taosc)

### 安装连接器

可以在当前 .NET 项目的路径下，通过 dotnet CLI 添加 Nuget package `TDengine.Connector` 到当前项目。

``` bash
dotnet add package TDengine.Connector
```

也可以修改当前项目的 `.csproj` 文件，添加如下 ItemGroup。

``` XML
  <ItemGroup>
    <PackageReference Include="TDengine.Connector" Version="3.1.*" />
  </ItemGroup>
```

## 建立连接

Native 连接

``` csharp
var builder = new ConnectionStringBuilder("host=localhost;port=6030;username=root;password=taosdata");
using (var client = DbDriver.Open(builder))
{
    Console.WriteLine("connected");
}
```

WebSocket 连接

```csharp
var builder = new ConnectionStringBuilder("protocol=WebSocket;host=localhost;port=6041;useSSL=false;username=root;password=taosdata");
using (var client = DbDriver.Open(builder))
{
    Console.WriteLine("connected");
}
```

ConnectionStringBuilder 支持的参数如下：
* protocol: 连接协议，可选值为 Native 或 WebSocket，默认为 Native
* host: TDengine 或 taosadapter 运行实例的地址
* port: TDengine 或 taosadapter 运行实例的端口
  * 当 protocol 为 WebSocket 时 useSSL 为 false 时，port 默认为 6041
  * 当 protocol 为 WebSocket 时 useSSL 为 true 时，port 默认为 443
* useSSL: 是否使用 SSL 连接，仅当 protocol 为 WebSocket 时有效，默认为 false
* token: 连接 TDengine cloud 的 token，仅当 protocol 为 WebSocket 时有效
* username: 连接 TDengine 的用户名
* password: 连接 TDengine 的密码
* db: 连接 TDengine 的数据库
* timezone: 解析时间结果的时区，默认为 `TimeZoneInfo.Local`，使用 `TimeZoneInfo.FindSystemTimeZoneById` 方法解析字符串为 `TimeZoneInfo` 对象。
* connTimeout: WebSocket 连接超时时间，仅当 protocol 为 WebSocket 时有效，默认为 1 分钟，使用 `TimeSpan.Parse` 方法解析字符串为 `TimeSpan` 对象。
* readTimeout: WebSocket 读超时时间，仅当 protocol 为 WebSocket 时有效，默认为 5 分钟，使用 `TimeSpan.Parse` 方法解析字符串为 `TimeSpan` 对象。
* writeTimeout: WebSocket 写超时时间，仅当 protocol 为 WebSocket 时有效，默认为 10 秒，使用 `TimeSpan.Parse` 方法解析字符串为 `TimeSpan` 对象。
* enableCompression: 是否启用 WebSocket 压缩（dotnet 版本 6 及以上，连接器版本 3.1.1 及以上生效），默认为 false
* autoReconnect: 是否自动重连（连接器版本 3.1.3 及以上生效），默认为 false
* reconnectRetryCount: 重连次数（连接器版本 3.1.3 及以上生效），默认为 3
* reconnectIntervalMs: 重连间隔时间（连接器版本 3.1.3 及以上生效），默认为 2000

### 指定 URL 和 Properties 获取连接

C# 连接器不支持此功能

### 配置参数的优先级

C# 连接器不支持此功能

## 使用示例

### 创建数据库和表

Native 样例

```csharp
using System;
using System.Text;
using TDengine.Driver;
using TDengine.Driver.Client;

namespace NativeQuery
{
    internal class Query
    {
        public static void Main(string[] args)
        {
            var builder = new ConnectionStringBuilder("host=localhost;port=6030;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                try
                {
                    client.Exec("create database power");
                    client.Exec("CREATE STABLE power.meters (ts TIMESTAMP, current FLOAT, voltage INT, phase FLOAT) TAGS (groupId INT, location BINARY(24))");
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    throw;
                }
            }
        }
    }
}
```

WebSocket 样例

```csharp
using System;
using System.Text;
using TDengine.Driver;
using TDengine.Driver.Client;

namespace WSQuery
{
    internal class Query
    {
        public static void Main(string[] args)
        {
            var builder = new ConnectionStringBuilder("protocol=WebSocket;host=localhost;port=6041;useSSL=false;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                try
                {
                    client.Exec("create database power");
                    client.Exec("CREATE STABLE power.meters (ts TIMESTAMP, current FLOAT, voltage INT, phase FLOAT) TAGS (groupId INT, location BINARY(24))");
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    throw;
                }
            }
        }
    }
}
```

### 插入数据

Native 样例

```csharp
using System;
using System.Text;
using TDengine.Driver;
using TDengine.Driver.Client;

namespace NativeQuery
{
    internal class Query
    {
        public static void Main(string[] args)
        {
            var builder = new ConnectionStringBuilder("host=localhost;port=6030;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                try
                {
                    string insertQuery =
                        "INSERT INTO " +
                        "power.d1001 USING power.meters TAGS(2,'California.SanFrancisco') " +
                        "VALUES " +
                        "('2023-10-03 14:38:05.000', 10.30000, 219, 0.31000) " +
                        "('2023-10-03 14:38:15.000', 12.60000, 218, 0.33000) " +
                        "('2023-10-03 14:38:16.800', 12.30000, 221, 0.31000) " +
                        "power.d1002 USING power.meters TAGS(3, 'California.SanFrancisco') " +
                        "VALUES " +
                        "('2023-10-03 14:38:16.650', 10.30000, 218, 0.25000) " +
                        "power.d1003 USING power.meters TAGS(2,'California.LosAngeles') " +
                        "VALUES " +
                        "('2023-10-03 14:38:05.500', 11.80000, 221, 0.28000) " +
                        "('2023-10-03 14:38:16.600', 13.40000, 223, 0.29000) " +
                        "power.d1004 USING power.meters TAGS(3,'California.LosAngeles') " +
                        "VALUES " +
                        "('2023-10-03 14:38:05.000', 10.80000, 223, 0.29000) " +
                        "('2023-10-03 14:38:06.500', 11.50000, 221, 0.35000)";
                    client.Exec(insertQuery);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    throw;
                }
            }
        }
    }
}
```

WebSocket 样例

```csharp
using System;
using System.Text;
using TDengine.Driver;
using TDengine.Driver.Client;

namespace WSQuery
{
    internal class Query
    {
        public static void Main(string[] args)
        {
            var builder = new ConnectionStringBuilder("protocol=WebSocket;host=localhost;port=6041;useSSL=false;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                try
                {
                    string insertQuery =
                        "INSERT INTO " +
                        "power.d1001 USING power.meters TAGS(2,'California.SanFrancisco') " +
                        "VALUES " +
                        "('2023-10-03 14:38:05.000', 10.30000, 219, 0.31000) " +
                        "('2023-10-03 14:38:15.000', 12.60000, 218, 0.33000) " +
                        "('2023-10-03 14:38:16.800', 12.30000, 221, 0.31000) " +
                        "power.d1002 USING power.meters TAGS(3, 'California.SanFrancisco') " +
                        "VALUES " +
                        "('2023-10-03 14:38:16.650', 10.30000, 218, 0.25000) " +
                        "power.d1003 USING power.meters TAGS(2,'California.LosAngeles') " +
                        "VALUES " +
                        "('2023-10-03 14:38:05.500', 11.80000, 221, 0.28000) " +
                        "('2023-10-03 14:38:16.600', 13.40000, 223, 0.29000) " +
                        "power.d1004 USING power.meters TAGS(3,'California.LosAngeles') " +
                        "VALUES " +
                        "('2023-10-03 14:38:05.000', 10.80000, 223, 0.29000) " +
                        "('2023-10-03 14:38:06.500', 11.50000, 221, 0.35000)";
                    client.Exec(insertQuery);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    throw;
                }
            }
        }
    }
}
```

### 查询数据

Native 样例

```csharp
using System;
using System.Text;
using TDengine.Driver;
using TDengine.Driver.Client;

namespace NativeQuery
{
    internal class Query
    {
        public static void Main(string[] args)
        {
            var builder = new ConnectionStringBuilder("host=localhost;port=6030;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                try
                {
                    client.Exec("use power");
                    string query = "SELECT * FROM meters";
                    using (var rows = client.Query(query))
                    {
                       while (rows.Read())
                       {
                           Console.WriteLine($"{((DateTime)rows.GetValue(0)):yyyy-MM-dd HH:mm:ss.fff}, {rows.GetValue(1)}, {rows.GetValue(2)}, {rows.GetValue(3)}, {rows.GetValue(4)}, {Encoding.UTF8.GetString((byte[])rows.GetValue(5))}");
                       }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    throw;
                }
            }
        }
    }
}
```

WebSocket 样例

```csharp
using System;
using System.Text;
using TDengine.Driver;
using TDengine.Driver.Client;

namespace WSQuery
{
    internal class Query
    {
        public static void Main(string[] args)
        {
            var builder = new ConnectionStringBuilder("protocol=WebSocket;host=localhost;port=6041;useSSL=false;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                try
                {
                    client.Exec("use power");
                    string query = "SELECT * FROM meters";
                    using (var rows = client.Query(query))
                    {
                        while (rows.Read())
                        {
                            Console.WriteLine($"{((DateTime)rows.GetValue(0)):yyyy-MM-dd HH:mm:ss.fff}, {rows.GetValue(1)}, {rows.GetValue(2)}, {rows.GetValue(3)}, {rows.GetValue(4)}, {Encoding.UTF8.GetString((byte[])rows.GetValue(5))}");
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    throw;
                }
            }
        }
    }
}
```

### 执行带有 reqId 的 SQL

Native 样例

```csharp
using System;
using System.Text;
using TDengine.Driver;
using TDengine.Driver.Client;

namespace NativeQueryWithReqID
{
    internal abstract class QueryWithReqID
    {
        public static void Main(string[] args)
        {
            var builder = new ConnectionStringBuilder("host=localhost;port=6030;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                try
                {
                    client.Exec($"create database if not exists test_db",ReqId.GetReqId());
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    throw;
                }
            }
        }
    }
}
```

WebSocket 样例

```csharp
using System;
using System.Text;
using TDengine.Driver;
using TDengine.Driver.Client;

namespace WSQueryWithReqID
{
    internal abstract class QueryWithReqID
    {
        public static void Main(string[] args)
        {
            var builder = new ConnectionStringBuilder("protocol=WebSocket;host=localhost;port=6041;useSSL=false;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                try
                {
                    client.Exec($"create database if not exists test_db",ReqId.GetReqId());
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    throw;
                }
            }
        }
    }
}
```

### 通过参数绑定写入数据

Native 样例

```csharp
using System;
using TDengine.Driver;
using TDengine.Driver.Client;

namespace NativeStmt
{
    internal abstract class NativeStmt
    {
        public static void Main(string[] args)
        {
            var builder = new ConnectionStringBuilder("host=localhost;port=6030;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                try
                {
                    client.Exec("create database power");
                    client.Exec(
                        "CREATE STABLE power.meters (ts TIMESTAMP, current FLOAT, voltage INT, phase FLOAT) TAGS (groupId INT, location BINARY(24))");
                    using (var stmt = client.StmtInit())
                    {
                        stmt.Prepare(
                            "Insert into power.d1001 using power.meters tags(2,'California.SanFrancisco') values(?,?,?,?)");
                        var ts = new DateTime(2023, 10, 03, 14, 38, 05, 000);
                        stmt.BindRow(new object[] { ts, (float)10.30000, (int)219, (float)0.31000 });
                        stmt.AddBatch();
                        stmt.Exec();
                        var affected = stmt.Affected();
                        Console.WriteLine($"affected rows: {affected}");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
            }
        }
    }
}
```

WebSocket 样例

```csharp
using System;
using TDengine.Driver;
using TDengine.Driver.Client;

namespace WSStmt
{
    internal abstract class WSStmt
    {
        public static void Main(string[] args)
        {
            var builder = new ConnectionStringBuilder("protocol=WebSocket;host=localhost;port=6041;useSSL=false;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                try
                {
                    client.Exec("create database power");
                    client.Exec(
                        "CREATE STABLE power.meters (ts TIMESTAMP, current FLOAT, voltage INT, phase FLOAT) TAGS (groupId INT, location BINARY(24))");
                    using (var stmt = client.StmtInit())
                    {
                        stmt.Prepare(
                            "Insert into power.d1001 using power.meters tags(2,'California.SanFrancisco') values(?,?,?,?)");
                        var ts = new DateTime(2023, 10, 03, 14, 38, 05, 000);
                        stmt.BindRow(new object[] { ts, (float)10.30000, (int)219, (float)0.31000 });
                        stmt.AddBatch();
                        stmt.Exec();
                        var affected = stmt.Affected();
                        Console.WriteLine($"affected rows: {affected}");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
            }
        }
    }
}
```

注意：使用 BindRow 需要注意原始 C# 列类型与 TDengine 列类型的需要一一对应，具体对应关系请参考 [TDengine DataType 和 C# DataType](#tdengine-datatype-和-c-datatype)。

### 无模式写入

Native 样例

```csharp
using TDengine.Driver;
using TDengine.Driver.Client;

namespace NativeSchemaless
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            var builder =
                new ConnectionStringBuilder("host=localhost;port=6030;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                client.Exec("create database sml");
                client.Exec("use sml");
                var influxDBData =
                    "st,t1=3i64,t2=4f64,t3=\"t3\" c1=3i64,c3=L\"passit\",c2=false,c4=4f64 1626006833639000000";
                client.SchemalessInsert(new string[] { influxDBData },
                    TDengineSchemalessProtocol.TSDB_SML_LINE_PROTOCOL,
                    TDengineSchemalessPrecision.TSDB_SML_TIMESTAMP_NANO_SECONDS, 0, ReqId.GetReqId());
                var telnetData = "stb0_0 1626006833 4 host=host0 interface=eth0";
                client.SchemalessInsert(new string[] { telnetData },
                    TDengineSchemalessProtocol.TSDB_SML_TELNET_PROTOCOL,
                    TDengineSchemalessPrecision.TSDB_SML_TIMESTAMP_MILLI_SECONDS, 0, ReqId.GetReqId());
                var jsonData =
                    "{\"metric\": \"meter_current\",\"timestamp\": 1626846400,\"value\": 10.3, \"tags\": {\"groupid\": 2, \"location\": \"California.SanFrancisco\", \"id\": \"d1001\"}}";
                client.SchemalessInsert(new string[] { jsonData }, TDengineSchemalessProtocol.TSDB_SML_JSON_PROTOCOL,
                    TDengineSchemalessPrecision.TSDB_SML_TIMESTAMP_MILLI_SECONDS, 0, ReqId.GetReqId());
            }
        }
    }
}
```

WebSocket 样例

```csharp
using TDengine.Driver;
using TDengine.Driver.Client;

namespace WSSchemaless
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            var builder =
                new ConnectionStringBuilder("protocol=WebSocket;host=localhost;port=6041;useSSL=false;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                client.Exec("create database sml");
                client.Exec("use sml");
                var influxDBData =
                    "st,t1=3i64,t2=4f64,t3=\"t3\" c1=3i64,c3=L\"passit\",c2=false,c4=4f64 1626006833639000000";
                client.SchemalessInsert(new string[] { influxDBData },
                    TDengineSchemalessProtocol.TSDB_SML_LINE_PROTOCOL,
                    TDengineSchemalessPrecision.TSDB_SML_TIMESTAMP_NANO_SECONDS, 0, ReqId.GetReqId());
                var telnetData = "stb0_0 1626006833 4 host=host0 interface=eth0";
                client.SchemalessInsert(new string[] { telnetData },
                    TDengineSchemalessProtocol.TSDB_SML_TELNET_PROTOCOL,
                    TDengineSchemalessPrecision.TSDB_SML_TIMESTAMP_MILLI_SECONDS, 0, ReqId.GetReqId());
                var jsonData =
                    "{\"metric\": \"meter_current\",\"timestamp\": 1626846400,\"value\": 10.3, \"tags\": {\"groupid\": 2, \"location\": \"California.SanFrancisco\", \"id\": \"d1001\"}}";
                client.SchemalessInsert(new string[] { jsonData }, TDengineSchemalessProtocol.TSDB_SML_JSON_PROTOCOL,
                    TDengineSchemalessPrecision.TSDB_SML_TIMESTAMP_MILLI_SECONDS, 0, ReqId.GetReqId());
            }
        }
    }
}
```

### 执行带有 reqId 的无模式写入

```csharp
public void SchemalessInsert(string[] lines, TDengineSchemalessProtocol protocol,
    TDengineSchemalessPrecision precision,
    int ttl, long reqId)
```

### 数据订阅

#### 创建 Topic

Native 样例

```csharp
using System;
using System.Text;
using TDengine.Driver;
using TDengine.Driver.Client;

namespace NativeSubscription
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            var builder = new ConnectionStringBuilder("host=localhost;port=6030;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                try
                {
                    client.Exec("create database power");
                    client.Exec("CREATE STABLE power.meters (ts TIMESTAMP, current FLOAT, voltage INT, phase FLOAT) TAGS (groupId INT, location BINARY(24))");
                    client.Exec("CREATE TOPIC topic_meters as SELECT * from power.meters");
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    throw;
                }
            }
        }
    }
}
```

WebSocket 样例

```csharp
using System;
using System.Text;
using TDengine.Driver;
using TDengine.Driver.Client;

namespace WSSubscription
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            var builder = new ConnectionStringBuilder("protocol=WebSocket;host=localhost;port=6041;useSSL=false;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                try
                {
                    client.Exec("create database power");
                    client.Exec("CREATE STABLE power.meters (ts TIMESTAMP, current FLOAT, voltage INT, phase FLOAT) TAGS (groupId INT, location BINARY(24))");
                    client.Exec("CREATE TOPIC topic_meters as SELECT * from power.meters");
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    throw;
                }
            }
        }
    }
}
```

#### 创建 Consumer

Native 样例

```csharp
var cfg = new Dictionary<string, string>()
{
    { "group.id", "group1" },
    { "auto.offset.reset", "latest" },
    { "td.connect.ip", "127.0.0.1" },
    { "td.connect.user", "root" },
    { "td.connect.pass", "taosdata" },
    { "td.connect.port", "6030" },
    { "client.id", "tmq_example" },
    { "enable.auto.commit", "true" },
    { "msg.with.table.name", "false" },
};
var consumer = new ConsumerBuilder<Dictionary<string, object>>(cfg).Build();
```

WebSocket 样例

```csharp
var cfg = new Dictionary<string, string>()
{
    { "td.connect.type", "WebSocket" },
    { "group.id", "group1" },
    { "auto.offset.reset", "latest" },
    { "td.connect.ip", "localhost" },
    { "td.connect.port", "6041" },
    { "useSSL", "false" },
    { "td.connect.user", "root" },
    { "td.connect.pass", "taosdata" },
    { "client.id", "tmq_example" },
    { "enable.auto.commit", "true" },
    { "msg.with.table.name", "false" },
};
var consumer = new ConsumerBuilder<Dictionary<string, object>>(cfg).Build();
```

consumer 支持的配置参数如下：
* td.connect.type: 连接类型，可选值为 Native 或 WebSocket，默认为 Native
* td.connect.ip: TDengine 或 taosadapter 运行实例的地址
* td.connect.port: TDengine 或 taosadapter 运行实例的端口
  * 当 td.connect.type 为 WebSocket 且 useSSL 为 false 时，td.connect.port 默认为 6041
  * 当 td.connect.type 为 WebSocket 且 useSSL 为 true 时，td.connect.port 默认为 443
* useSSL: 是否使用 SSL 连接，仅当 td.connect.type 为 WebSocket 时有效，默认为 false
* token: 连接 TDengine cloud 的 token，仅当 td.connect.type 为 WebSocket 时有效
* td.connect.user: 连接 TDengine 的用户名
* td.connect.pass: 连接 TDengine 的密码
* group.id: 消费者组 ID
* client.id: 消费者 ID
* enable.auto.commit: 是否自动提交 offset，默认为 true
* auto.commit.interval.ms: 自动提交 offset 的间隔时间，默认为 5000 毫秒
* auto.offset.reset: 当 offset 不存在时，从哪里开始消费，可选值为 earliest 或 latest，默认为 latest
* msg.with.table.name: 消息是否包含表名
* ws.message.enableCompression: 是否启用 WebSocket 压缩（dotnet 版本 6 及以上，连接器版本 3.1.1 及以上生效），默认为 false
* ws.autoReconnect: 是否自动重连（连接器版本 3.1.3 及以上生效），默认为 false
* ws.reconnect.retry.count: 重连次数（连接器版本 3.1.3 及以上生效），默认为 3
* ws.reconnect.interval.ms: 重连间隔时间（连接器版本 3.1.3 及以上生效），默认为 2000

支持订阅结果集 `Dictionary<string, object>` key 为列名，value 为列值。

如果使用 object 接收列值，需要注意：
* 原始 C# 列类型与 TDengine 列类型的需要一一对应，具体对应关系请参考 [TDengine DataType 和 C# DataType](#tdengine-datatype-和-c-datatype)。
* 列名与 class 属性名一致，并可读写。
* 明确设置 value 解析器`ConsumerBuilder.SetValueDeserializer(new ReferenceDeserializer<T>());`

样例如下

结果 class

```csharp
    class Result
    {
        public DateTime ts { get; set; }
        public float current { get; set; }
        public int voltage { get; set; }
        public float phase { get; set; }
    }
```

设置解析器

```csharp
var tmqBuilder = new ConsumerBuilder<Result>(cfg);
tmqBuilder.SetValueDeserializer(new ReferenceDeserializer<Result>());
var consumer = tmqBuilder.Build();
```

也可实现自定义解析器，实现 `IDeserializer<T>` 接口并通过`ConsumerBuilder.SetValueDeserializer`方法传入。

```csharp
    public interface IDeserializer<T>
    {
        T Deserialize(ITMQRows data, bool isNull, SerializationContext context);
    }
```

#### 订阅消费数据

```csharp
consumer.Subscribe(new List<string>() { "topic_meters" });
while (true)
{
    using (var cr = consumer.Consume(500))
    {
        if (cr == null) continue;
        foreach (var message in cr.Message)
        {
            Console.WriteLine(
                $"message {{{((DateTime)message.Value["ts"]).ToString("yyyy-MM-dd HH:mm:ss.fff")}, " +
                $"{message.Value["current"]}, {message.Value["voltage"]}, {message.Value["phase"]}}}");
        }
    }
}
```

#### 指定订阅 Offset

```csharp
consumer.Assignment.ForEach(a =>
{
    Console.WriteLine($"{a}, seek to 0");
    consumer.Seek(new TopicPartitionOffset(a.Topic, a.Partition, 0));
    Thread.Sleep(TimeSpan.FromSeconds(1));
});
```

#### 提交 Offset

```csharp
public void Commit(ConsumeResult<TValue> consumerResult)
public List<TopicPartitionOffset> Commit()
public void Commit(IEnumerable<TopicPartitionOffset> offsets)
```

#### 关闭订阅

```csharp
consumer.Unsubscribe();
consumer.Close();
```

#### 完整示例

Native 样例

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TDengine.Driver;
using TDengine.Driver.Client;
using TDengine.TMQ;

namespace NativeSubscription
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            var builder = new ConnectionStringBuilder("host=localhost;port=6030;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                try
                {
                    client.Exec("CREATE DATABASE power");
                    client.Exec("USE power");
                    client.Exec(
                        "CREATE STABLE power.meters (ts TIMESTAMP, current FLOAT, voltage INT, phase FLOAT) TAGS (groupId INT, location BINARY(24))");
                    client.Exec("CREATE TOPIC topic_meters as SELECT * from power.meters");
                    var cfg = new Dictionary<string, string>()
                    {
                        { "group.id", "group1" },
                        { "auto.offset.reset", "latest" },
                        { "td.connect.ip", "127.0.0.1" },
                        { "td.connect.user", "root" },
                        { "td.connect.pass", "taosdata" },
                        { "td.connect.port", "6030" },
                        { "client.id", "tmq_example" },
                        { "enable.auto.commit", "true" },
                        { "msg.with.table.name", "false" },
                    };
                    var consumer = new ConsumerBuilder<Dictionary<string, object>>(cfg).Build();
                    consumer.Subscribe(new List<string>() { "topic_meters" });
                    Task.Run(InsertData);
                    while (true)
                    {
                        using (var cr = consumer.Consume(500))
                        {
                            if (cr == null) continue;
                            foreach (var message in cr.Message)
                            {
                                Console.WriteLine(
                                    $"message {{{((DateTime)message.Value["ts"]).ToString("yyyy-MM-dd HH:mm:ss.fff")}, " +
                                    $"{message.Value["current"]}, {message.Value["voltage"]}, {message.Value["phase"]}}}");
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    throw;
                }
            }
        }
        
        static void InsertData()
        {
            var builder = new ConnectionStringBuilder("host=localhost;port=6030;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                while (true)
                {
                    client.Exec("INSERT into power.d1001 using power.meters tags(2,'California.SanFrancisco') values(now,11.5,219,0.30)");
                    Task.Delay(1000).Wait();
                }
            }
        }
    }
}
```

WebSocket 样例

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TDengine.Driver;
using TDengine.Driver.Client;
using TDengine.TMQ;

namespace WSSubscription
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            var builder = new ConnectionStringBuilder("protocol=WebSocket;host=localhost;port=6041;useSSL=false;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                try
                {
                    client.Exec("CREATE DATABASE power");
                    client.Exec("USE power");
                    client.Exec(
                        "CREATE STABLE power.meters (ts TIMESTAMP, current FLOAT, voltage INT, phase FLOAT) TAGS (groupId INT, location BINARY(24))");
                    client.Exec("CREATE TOPIC topic_meters as SELECT * from power.meters");
                    var cfg = new Dictionary<string, string>()
                    {
                        { "td.connect.type", "WebSocket" },
                        { "group.id", "group1" },
                        { "auto.offset.reset", "latest" },
                        { "td.connect.ip", "localhost" },
                        { "td.connect.port", "6041" },
                        { "useSSL", "false" },
                        { "td.connect.user", "root" },
                        { "td.connect.pass", "taosdata" },
                        { "client.id", "tmq_example" },
                        { "enable.auto.commit", "true" },
                        { "msg.with.table.name", "false" },
                    };
                    var consumer = new ConsumerBuilder<Dictionary<string, object>>(cfg).Build();
                    consumer.Subscribe(new List<string>() { "topic_meters" });
                    Task.Run(InsertData);
                    while (true)
                    {
                        using (var cr = consumer.Consume(500))
                        {
                            if (cr == null) continue;
                            foreach (var message in cr.Message)
                            {
                                Console.WriteLine(
                                    $"message {{{((DateTime)message.Value["ts"]).ToString("yyyy-MM-dd HH:mm:ss.fff")}, " +
                                    $"{message.Value["current"]}, {message.Value["voltage"]}, {message.Value["phase"]}}}");
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    throw;
                }
            }
        }
        
        static void InsertData()
        {
            var builder = new ConnectionStringBuilder("protocol=WebSocket;host=localhost;port=6041;useSSL=false;username=root;password=taosdata");
            using (var client = DbDriver.Open(builder))
            {
                while (true)
                {
                    client.Exec("INSERT into power.d1001 using power.meters tags(2,'California.SanFrancisco') values(now,11.5,219,0.30)");
                    Task.Delay(1000).Wait();
                }
            }
        }
    }
}
```

### ADO.NET

C# 连接器支持 ADO.NET 接口，可以通过 ADO.NET 接口连接 TDengine 运行实例，进行数据写入、查询等操作。

Native 样例

```csharp
using System;
using TDengine.Data.Client;

namespace NativeADO
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            const string connectionString = "host=localhost;port=6030;username=root;password=taosdata";
            using (var connection = new TDengineConnection(connectionString))
            {
                try
                {
                    connection.Open();
                    using (var command = new TDengineCommand(connection))
                    {
                        command.CommandText = "create database power";
                        command.ExecuteNonQuery();
                        connection.ChangeDatabase("power");
                        command.CommandText =
                            "CREATE STABLE power.meters (ts TIMESTAMP, current FLOAT, voltage INT, phase FLOAT) TAGS (groupId INT, location BINARY(24))";
                        command.ExecuteNonQuery();
                        command.CommandText = "INSERT INTO " +
                                              "power.d1001 USING power.meters TAGS(2,'California.SanFrancisco') " +
                                              "VALUES " +
                                              "(?,?,?,?)";
                        var parameters = command.Parameters;
                        parameters.Add(new TDengineParameter("@0", new DateTime(2023,10,03,14,38,05,000)));
                        parameters.Add(new TDengineParameter("@1", (float)10.30000));
                        parameters.Add(new TDengineParameter("@2", (int)219));
                        parameters.Add(new TDengineParameter("@3", (float)0.31000));
                        command.ExecuteNonQuery();
                        command.Parameters.Clear();
                        command.CommandText = "SELECT * FROM meters";
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                Console.WriteLine(
                                    $"{((DateTime) reader.GetValue(0)):yyyy-MM-dd HH:mm:ss.fff}, {reader.GetValue(1)}, {reader.GetValue(2)}, {reader.GetValue(3)}, {reader.GetValue(4)}, {System.Text.Encoding.UTF8.GetString((byte[]) reader.GetValue(5))}");
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
            }
        }
    }
}
```

WebSocket 样例

```csharp
using System;
using TDengine.Data.Client;

namespace WSADO
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            const string connectionString = "protocol=WebSocket;host=localhost;port=6041;useSSL=false;username=root;password=taosdata";
            using (var connection = new TDengineConnection(connectionString))
            {
                try
                {
                    connection.Open();
                    using (var command = new TDengineCommand(connection))
                    {
                        command.CommandText = "create database power";
                        command.ExecuteNonQuery();
                        connection.ChangeDatabase("power");
                        command.CommandText =
                            "CREATE STABLE power.meters (ts TIMESTAMP, current FLOAT, voltage INT, phase FLOAT) TAGS (groupId INT, location BINARY(24))";
                        command.ExecuteNonQuery();
                        command.CommandText = "INSERT INTO " +
                                              "power.d1001 USING power.meters TAGS(2,'California.SanFrancisco') " +
                                              "VALUES " +
                                              "(?,?,?,?)";
                        var parameters = command.Parameters;
                        parameters.Add(new TDengineParameter("@0", new DateTime(2023,10,03,14,38,05,000)));
                        parameters.Add(new TDengineParameter("@1", (float)10.30000));
                        parameters.Add(new TDengineParameter("@2", (int)219));
                        parameters.Add(new TDengineParameter("@3", (float)0.31000));
                        command.ExecuteNonQuery();
                        command.Parameters.Clear();
                        command.CommandText = "SELECT * FROM meters";
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                Console.WriteLine(
                                    $"{((DateTime) reader.GetValue(0)):yyyy-MM-dd HH:mm:ss.fff}, {reader.GetValue(1)}, {reader.GetValue(2)}, {reader.GetValue(3)}, {reader.GetValue(4)}, {System.Text.Encoding.UTF8.GetString((byte[]) reader.GetValue(5))}");
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
            }
        }
    }
}
```

* 连接参数与[建立连接](#建立连接)中的连接参数一致。
* TDengineParameter 的 name 需要以 @ 开头，如 @0、@1、@2 等，value 需要 C# 列类型与 TDengine
  列类型一一对应，具体对应关系请参考 [TDengine DataType 和 C# DataType](#tdengine-datatype-和-c-datatype)。

### 更多示例程序

[示例程序](https://github.com/taosdata/taos-connector-dotnet/tree/3.0/examples)