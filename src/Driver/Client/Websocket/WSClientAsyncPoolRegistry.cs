using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TDengine.Driver.Client.Websocket
{
    internal static class WSClientAsyncPoolRegistry
    {
        private static readonly ConcurrentDictionary<string, Lazy<WSClientAsyncPool>> Pools =
            new ConcurrentDictionary<string, Lazy<WSClientAsyncPool>>(StringComparer.Ordinal);

        internal static async Task<ITDengineClientAsync> AcquireAsync(ConnectionStringBuilder builder,
            CancellationToken cancellationToken)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (builder.Protocol != TDengineConstant.ProtocolWebSocket)
            {
                throw new ArgumentException("WebSocket async pooling requires WebSocket protocol.", nameof(builder));
            }

            var key = BuildPoolKey(builder);
            var lazy = Pools.GetOrAdd(key, _ => new Lazy<WSClientAsyncPool>(() =>
                new WSClientAsyncPool(CloneBuilderForPool(builder), builder.CreateWebSocketAsyncPoolOptions()),
                LazyThreadSafetyMode.ExecutionAndPublication));

            WSClientAsyncPool pool;
            try
            {
                pool = lazy.Value;
            }
            catch
            {
                Lazy<WSClientAsyncPool> removed;
                if (TryRemovePool(key, lazy, out removed) && removed.IsValueCreated)
                {
                    removed.Value.Dispose();
                }

                throw;
            }

            try
            {
                return await pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (pool.GetMetrics().TotalConnections == 0)
                {
                    Lazy<WSClientAsyncPool> removed;
                    if (TryRemovePool(key, lazy, out removed) && removed.IsValueCreated)
                    {
                        removed.Value.Dispose();
                    }
                }

                throw;
            }
        }

        private static bool TryRemovePool(string key, Lazy<WSClientAsyncPool> lazy,
            out Lazy<WSClientAsyncPool> removed)
        {
            var pools = (ICollection<KeyValuePair<string, Lazy<WSClientAsyncPool>>>)Pools;
            if (pools.Remove(new KeyValuePair<string, Lazy<WSClientAsyncPool>>(key, lazy)))
            {
                removed = lazy;
                return true;
            }

            removed = null;
            return false;
        }

        internal static WSClientAsyncPoolMetrics GetMetrics(ConnectionStringBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            Lazy<WSClientAsyncPool> lazy;
            if (!Pools.TryGetValue(BuildPoolKey(builder), out lazy) || !lazy.IsValueCreated)
            {
                return null;
            }

            return lazy.Value.GetMetrics();
        }

        internal static void Clear()
        {
            foreach (var item in Pools)
            {
                Lazy<WSClientAsyncPool> removed;
                if (!Pools.TryRemove(item.Key, out removed) || !removed.IsValueCreated)
                {
                    continue;
                }

                removed.Value.Dispose();
            }
        }

        private static ConnectionStringBuilder CloneBuilderForPool(ConnectionStringBuilder builder)
        {
            var clone = new ConnectionStringBuilder(builder.ConnectionString);
            clone.Remove("pooling");
            return clone;
        }

        private static string BuildPoolKey(ConnectionStringBuilder builder)
        {
            var normalized = new StringBuilder(512);
            Append(normalized, "protocol", builder.Protocol);
            Append(normalized, "host", NormalizeHost(builder));
            Append(normalized, "port", builder.Port.ToString(CultureInfo.InvariantCulture));
            Append(normalized, "db", builder.Database);
            Append(normalized, "username", builder.Username);
            Append(normalized, "password", builder.Password);
            Append(normalized, "token", builder.Token);
            Append(normalized, "bearerToken", builder.BearerToken);
            Append(normalized, "useSSL", builder.UseSSL);
            Append(normalized, "enableCompression", builder.EnableCompression);
            Append(normalized, "autoReconnect", builder.AutoReconnect);
            Append(normalized, "reconnectRetryCount", builder.ReconnectRetryCount);
            Append(normalized, "reconnectIntervalMs", builder.ReconnectIntervalMs);
            Append(normalized, "connTimeout", builder.ConnTimeout);
            Append(normalized, "readTimeout", builder.ReadTimeout);
            Append(normalized, "writeTimeout", builder.WriteTimeout);
            Append(normalized, "timezone", builder.GetTimeZone().Id);
            Append(normalized, "minPoolSize", builder.MinPoolSize);
            Append(normalized, "maxPoolSize", builder.MaxPoolSize);
            Append(normalized, "poolConnectionTimeout", builder.PoolConnectionTimeout);
            Append(normalized, "poolKeepaliveTime", builder.PoolKeepaliveTime);
            Append(normalized, "poolMaxLifetime", builder.PoolMaxLifetime);
            Append(normalized, "poolHousekeepingInterval", builder.PoolHousekeepingInterval);
            Append(normalized, "poolCreationRetryBackoff", builder.PoolCreationRetryBackoff);
            Append(normalized, "poolMaxCreationRetryBackoff", builder.PoolMaxCreationRetryBackoff);
            Append(normalized, "poolLeakDetectionThreshold", builder.PoolLeakDetectionThreshold);
            return ComputeKeyHash(normalized.ToString());
        }

        private static string NormalizeHost(ConnectionStringBuilder builder)
        {
            var addresses = builder.GetFailoverAddresses();
            var text = new StringBuilder(addresses.Count * 32);
            for (var i = 0; i < addresses.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }

                text.Append(addresses[i].CacheKey);
            }

            return text.ToString();
        }

        private static void Append(StringBuilder builder, string key, string value)
        {
            builder.Append(key).Append('=').Append(value ?? string.Empty).Append(';');
        }

        private static void Append(StringBuilder builder, string key, bool value)
        {
            builder.Append(key).Append('=').Append(value ? "true" : "false").Append(';');
        }

        private static void Append(StringBuilder builder, string key, int value)
        {
            builder.Append(key).Append('=').Append(value.ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        private static void Append(StringBuilder builder, string key, TimeSpan value)
        {
            builder.Append(key).Append('=').Append(value.Ticks.ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        private static string ComputeKeyHash(string value)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                var hash = sha256.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }
    }
}
