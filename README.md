# C# Connector

[中文版](./README-CN.md)

`TDengine.Connector` is the C# language connector provided by TDengine. C# developers can use it to develop C# application software that accesses TDengine cluster data.

The `TDengine.Connector` connector supports establishing a connection with the TDengine running instance through the TDengine client driver (taosc), and provides functions such as data writing, query, data subscription, schemaless data writing, and parameter binding interface data writing. `TDengine.Connector` also supports WebSocket since v3.0.1, establishes WebSocket connection, and provides functions such as data writing, query, and parameter binding interface data writing.

This article introduces how to install `TDengine.Connector` in a Linux or Windows environment, and connect to the TDengine cluster through `TDengine.Connector` to perform basic operations such as data writing and querying.

**Notice**:

* `TDengine.Connector` 3.x is not compatible with TDengine 2.x. If you need to use the C# connector in an environment running TDengine 2.x version, please use the 1.x version of TDengine.Connector.
* `TDengine.Connector` version 3.1.0 has been completely refactored and is no longer compatible with 3.0.2 and previous versions. For 3.0.2 documents, please refer to [nuget](https://www.nuget.org/packages/TDengine.Connector/3.0.2)

The source code of `TDengine.Connector` is hosted on [GitHub](https://github.com/taosdata/taos-connector-dotnet/tree/3.0).

## Supported platforms

The supported platforms are the same as those supported by the TDengine client driver.

Note TDengine no longer supports 32-bit Windows platforms.

## Version support

| **Connector version** | **TDengine version** | **major features**                   |
|-----------------------|----------------------|--------------------------------------|
| 3.1.4                 | 3.3.2.0/3.1.2.0      | WebSocket performance improvements   |
| 3.1.3                 | 3.2.1.0/3.1.1.18     | support Websocket reconnect          |
| 3.1.2                 | 3.2.1.0/3.1.1.18     | fix schemaless result release        |
| 3.1.1                 | 3.2.1.0/3.1.1.18     | support varbinary and geometry       |
| 3.1.0                 | 3.2.1.0/3.1.1.18     | WebSocket uses native implementation |

## Handling exceptions

`TDengine.Connector` will throw an exception and the application needs to handle the exception. The taosc exception type `TDengineError` contains error code and error information, and the application can handle it based on the error code and error information.

## TDengine DataType vs. C# DataType

| TDengine DataType | C# Type                 |
|-------------------|-------------------------|
| TIMESTAMP         | DateTime                |
| TINYINT           | sbyte                   |
| SMALLINT          | short                   |
| INT               | int                     |
| BIGINT            | long                    |
| TINYINT UNSIGNED  | byte                    |
| SMALLINT UNSIGNED | ushort                  |
| INT UNSIGNED      | uint                    |
| BIGINT UNSIGNED   | ulong                   |
| FLOAT             | float                   |
| DOUBLE            | double                  |
| BOOL              | bool                    |
| BINARY            | byte[]                  |
| NCHAR             | string (utf-8 encoding) |
| JSON              | byte[]                  |
| VARBINARY         | byte[]                  |
| GEOMETRY          | byte[]                  |

**Note**: JSON type is only supported in tag.

## Installation Steps

### Pre-installation preparation

* Install [.NET SDK](https://dotnet.microsoft.com/download)
* [Nuget Client](https://docs.microsoft.com/en-us/nuget/install-nuget-client-tools) (optional installation)
* Install the TDengine client driver. For specific steps, please refer to [Installing the client driver](https://docs.tdengine.com/develop/connect/#install-client-driver-taosc)

### Install the connectors

Nuget package `TDengine.Connector` can be added to the current project through dotnet CLI under the path of the current .NET project.

```bash
dotnet add package TDengine.Connector
```

You can also modify the `.csproj` file of the current project and add the following ItemGroup.

``` XML
   <ItemGroup>
     <PackageReference Include="TDengine.Connector" Version="3.1.*" />
   </ItemGroup>
```

## Establishing a connection

Native connection

``` csharp
var builder = new ConnectionStringBuilder("host=localhost;port=6030;username=root;password=taosdata");
using (var client = DbDriver.Open(builder))
{
     Console.WriteLine("connected");
}
```

WebSocket connection

```csharp
var builder = new ConnectionStringBuilder("protocol=WebSocket;host=localhost;port=6041;useSSL=false;username=root;password=taosdata");
using (var client = DbDriver.Open(builder))
{
     Console.WriteLine("connected");
}
```

The parameters supported by `ConnectionStringBuilder` are as follows:
* protocol: connection protocol, optional value is Native or WebSocket, default is Native
* host: the address of the running instance of TDengine or taosadapter,
* port: The port of the running instance of TDengine or taosadapter.
  * When using WebSocket without SSL, the default is 6041.
  * When using WebSocket with SSL, the default is 443.
* useSSL: Whether to use SSL, the default is false, only valid when the protocol is WebSocket
* token: The token used to connect to TDengine Cloud, only valid when the protocol is WebSocket
* username: username to connect to TDengine
* password: password to connect to TDengine
* db: database connected to TDengine
* timezone: The time zone for parsing time results, the default is `TimeZoneInfo.Local`, use the `TimeZoneInfo.FindSystemTimeZoneById` method to parse the string into a `TimeZoneInfo` object.
* connTimeout: WebSocket connection timeout, only valid when the protocol is WebSocket, the default is 1 minute, use the `TimeSpan.Parse` method to parse the string into a `TimeSpan` object.
* readTimeout: WebSocket read timeout, only valid when the protocol is WebSocket, the default is 5 minutes, use the `TimeSpan.Parse` method to parse the string into a `TimeSpan` object.
* writeTimeout: WebSocket write timeout, only valid when the protocol is WebSocket, the default is 10 seconds, use the `TimeSpan.Parse` method to parse the string into a `TimeSpan` object.
* enableCompression: Whether to enable WebSocket compression (effective for dotnet version 6 and above, connector version 3.1.1 and above). The default is false.
* autoReconnect: Whether to enable WebSocket reconnect (connector version 3.1.3 and above). The default is false.
* reconnectRetryCount: The number of reconnection retries (connector version 3.1.3 and above). The default is 3.
* reconnectIntervalMs: The interval between reconnection retries (connector version 3.1.3 and above). The default is
  2000.

### Specify the URL and Properties to get the connection

The C# connector does not support this feature

### Priority of configuration parameters

The C# connector does not support this feature

## Usage examples

### Create database and tables

Native Example

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

WebSocket Example

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

### Insert data

Native Example

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

WebSocket Example

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

### Querying data

Native Example

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

WebSocket Example

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

### execute SQL with reqId

Native Example

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

WebSocket Example

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

### Writing data via parameter binding

Native Example

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

WebSocket Example

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

Note: When using BindRow, you need to pay attention to the one-to-one correspondence between the original C# column type and the TDengine column type. For the specific correspondence, please refer to [TDengine DataType and C# DataType](#tdengine-datatype-vs-c-datatype).

### Schemaless Writing

Native Example

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

WebSocket Example

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

### Schemaless with reqId

```csharp
public void SchemalessInsert(string[] lines, TDengineSchemalessProtocol protocol,
    TDengineSchemalessPrecision precision,
    int ttl, long reqId)
```

### Data Subscription

#### Create a Topic

Native Example

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

WebSocket Example

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

#### Create a Consumer

Native Example

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

WebSocket Example

```csharp
var cfg = new Dictionary<string, string>()
{
    { "td.connect.type", "WebSocket" },
    { "group.id", "group1" },
    { "auto.offset.reset", "latest" },
    { "td.connect.ip", "localhost" },
    { "td.connect.port","6041"},
    { "useSSL", "false" },
    { "td.connect.user", "root" },
    { "td.connect.pass", "taosdata" },
    { "client.id", "tmq_example" },
    { "enable.auto.commit", "true" },
    { "msg.with.table.name", "false" },
};
var consumer = new ConsumerBuilder<Dictionary<string, object>>(cfg).Build();
```

The configuration parameters supported by consumer are as follows:
* td.connect.type: connection type, optional value is Native or WebSocket, default is Native
* td.connect.ip: The address of the TDengine running instance or taosadapter
* td.connect.port: The port of the running instance of TDengine or taosadapter.
  * When using WebSocket without SSL, the default is 6041.
  * When using WebSocket with SSL, the default is 443.
* useSSL: Whether to use SSL, the default is false, only valid when the protocol is WebSocket
* token: The token used to connect to TDengine Cloud, only valid when the protocol is WebSocket
* td.connect.user: username to connect to TDengine
* td.connect.pass: Password for connecting to TDengine
* group.id: consumer group ID
* client.id: consumer ID
* enable.auto.commit: Whether to automatically commit offset, the default is true
* auto.commit.interval.ms: The interval for automatically submitting offsets, the default is 5000 milliseconds
* auto.offset.reset: When offset does not exist, where to start consumption, the optional value is earliest or latest, the default is latest
* msg.with.table.name: Whether the message contains the table name
* ws.message.enableCompression: Whether to enable WebSocket compression (effective for dotnet version 6 and above, connector version 3.1.1 and above). The default is false.
* ws.autoReconnect: Whether to enable WebSocket reconnect (connector version 3.1.3 and above). The default is false.
* ws.reconnect.retry.count: The number of reconnection retries (connector version 3.1.3 and above). The default is 3.
* ws.reconnect.interval.ms: The interval between reconnection retries (connector version 3.1.3 and above). The default
  is 2000.

Supports subscribing to the result set `Dictionary<string, object>` where the key is the column name and the value is the column value.

If you use object to receive column values, you need to pay attention to:
* There needs to be a one-to-one correspondence between the original C# column type and the TDengine column type. For specific correspondence, please refer to [TDengine DataType and C# DataType] (#tdengine-datatype-and-c-datatype).
* The column name is consistent with the class attribute name and can be get and set.
* Explicitly set the value parser `ConsumerBuilder.SetValueDeserializer(new ReferenceDeserializer<T>());`

An example is as follows

Result class

```csharp
     class Result
     {
         public DateTime ts { get; set; }
         public float current { get; set; }
         public int voltage { get; set; }
         public float phase { get; set; }
     }
```

Set up parser

```csharp
var tmqBuilder = new ConsumerBuilder<Result>(cfg);
tmqBuilder.SetValueDeserializer(new ReferenceDeserializer<Result>());
var consumer = tmqBuilder.Build();
```

You can also implement a custom deserializer, implement the `IDeserializer<T>` interface and pass it in through the `ConsumerBuilder.SetValueDeserializer` method.

```csharp
     public interface IDeserializer<T>
     {
         T Deserialize(ITMQRows data, bool isNull, SerializationContext context);
     }
```

#### Subscribe to consume data

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

#### Assignment subscription Offset

```csharp
consumer.Assignment.ForEach(a =>
{
    Console.WriteLine($"{a}, seek to 0");
    consumer.Seek(new TopicPartitionOffset(a.Topic, a.Partition, 0));
    Thread.Sleep(TimeSpan.FromSeconds(1));
});
```

#### Commit offset

```csharp
public void Commit(ConsumeResult<TValue> consumerResult)
public List<TopicPartitionOffset> Commit()
public void Commit(IEnumerable<TopicPartitionOffset> offsets)
```

#### Close subscriptions

```csharp
consumer.Unsubscribe();
consumer.Close();
```

#### Full Sample Code

Native Example

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

WebSocket Example

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
                        { "td.connect.port","6041"},
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

The C# connector supports the ADO.NET interface, and you can connect to the TDengine running instance through the ADO.NET interface to perform operations such as data writing and querying.

Native Example

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

WebSocket Example

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

* The connection parameters are consistent with those in [Establishing a connection](#establishing-a-connection).
* The name of TDengineParameter needs to start with @, such as @0, @1, @2, etc. The value needs to have a one-to-one correspondence between the C# column type and the TDengine column type. For the specific correspondence, please refer to [TDengine DataType and C# DataType](#tdengine-datatype-vs-c-datatype).

### More sample programs

[sample program](https://github.com/taosdata/taos-connector-dotnet/tree/3.0/examples)