using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TDengine.Driver.Client.Websocket
{
    internal static class WSClientAsyncPoolRegistry
    {
        internal const int MaximumPoolEntries = 256;
        private static readonly TimeSpan IdleEntryLifetime = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan TrimInterval = TimeSpan.FromMinutes(1);

        private static readonly ConcurrentDictionary<string, PoolEntry> Pools =
            new ConcurrentDictionary<string, PoolEntry>(StringComparer.Ordinal);

        private static readonly object MutationLock = new object();
        private static readonly WaitCallback DisposeEntriesCallback = DisposeEntriesInBackground;
        private static long _lastTrimTimestamp;
        private static readonly Timer TrimTimer = new Timer(ScheduledTrim, null, TrimInterval, TrimInterval);

        internal static async Task<ITDengineClientAsync> AcquireAsync(ConnectionStringBuilder builder,
            CancellationToken cancellationToken)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (builder.Protocol != TDengineConstant.ProtocolWebSocket)
            {
                throw new ArgumentException("WebSocket async pooling requires WebSocket protocol.", nameof(builder));
            }

            var snapshot = builder.CreateSnapshot();
            var key = BuildPoolKey(snapshot);
            TrimIfDue();
            while (true)
            {
                var entry = GetOrCreateEntry(key, snapshot);
                if (!entry.TryEnter())
                {
                    RemoveRetiredEntry(key, entry);
                    continue;
                }

                try
                {
                    WSClientAsyncPool pool;
                    try
                    {
                        pool = entry.GetPool();
                    }
                    catch
                    {
                        RemoveAndDispose(key, entry, true);
                        throw;
                    }

                    return await pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    entry.Exit();
                }
            }
        }

        internal static WSClientAsyncPoolMetrics GetMetrics(ConnectionStringBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            var key = BuildPoolKey(builder.CreateSnapshot());
            if (!Pools.TryGetValue(key, out var entry) || !entry.TryEnter())
            {
                return null;
            }

            try
            {
                return entry.TryGetMetrics(out var metrics) ? metrics : null;
            }
            finally
            {
                entry.Exit();
            }
        }

        internal static void Clear()
        {
            List<PoolEntry> retiredEntries;
            lock (MutationLock)
            {
                retiredEntries = RetireAllNoLock(true);
                Volatile.Write(ref _lastTrimTimestamp, 0);
            }

            DisposeEntries(retiredEntries);
        }

        internal static int Count => Pools.Count;

        internal static PoolEntry GetOrCreateEntry(string key, ConnectionStringBuilder builder)
        {
            while (true)
            {
                if (Pools.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                List<PoolEntry> retiredEntries = null;
                PoolEntry result = null;
                Exception capacityException = null;
                lock (MutationLock)
                {
                    if (Pools.TryGetValue(key, out existing))
                    {
                        result = existing;
                    }
                    else
                    {
                        retiredEntries = TrimNoLock(true);
                        if (Pools.Count >= MaximumPoolEntries)
                        {
                            capacityException = new TDengineError(
                                (int)TDengineError.InternalErrorCode.WS_POOL_REGISTRY_LIMIT,
                                $"WebSocket async pool registry limit of {MaximumPoolEntries} has been reached");
                        }
                        else
                        {
                            var created = new PoolEntry(builder);
                            if (Pools.TryAdd(key, created))
                            {
                                result = created;
                            }
                            else if (Pools.TryGetValue(key, out existing))
                            {
                                result = existing;
                            }
                        }
                    }
                }

                QueueEntriesForDisposal(retiredEntries);
                if (capacityException != null)
                {
                    throw capacityException;
                }

                if (result != null)
                {
                    return result;
                }
            }
        }

        private static void TrimIfDue(bool reserveCapacity = false)
        {
            if (!reserveCapacity)
            {
                var now = Stopwatch.GetTimestamp();
                var last = Volatile.Read(ref _lastTrimTimestamp);
                if (last != 0 && StopwatchTicksToTimeSpan(now - last) < TrimInterval)
                {
                    return;
                }
            }

            List<PoolEntry> retiredEntries;
            lock (MutationLock)
            {
                retiredEntries = TrimNoLock(reserveCapacity);
            }

            QueueEntriesForDisposal(retiredEntries);
        }

        private static void ScheduledTrim(object state)
        {
            try
            {
                TrimIfDue();
            }
            catch (Exception e)
            {
                Trace.TraceWarning("WSClientAsyncPoolRegistry scheduled trim failed: " +
                                   DescribeException(e));
            }

            GC.KeepAlive(TrimTimer);
        }

        private static List<PoolEntry> TrimNoLock(bool reserveCapacity)
        {
            var now = Stopwatch.GetTimestamp();
            var last = Volatile.Read(ref _lastTrimTimestamp);
            if (!reserveCapacity && last != 0 && StopwatchTicksToTimeSpan(now - last) < TrimInterval)
            {
                return null;
            }

            if (!reserveCapacity && Interlocked.CompareExchange(ref _lastTrimTimestamp, now, last) != last)
            {
                return null;
            }

            if (reserveCapacity)
            {
                Volatile.Write(ref _lastTrimTimestamp, now);
            }

            var entries = new List<KeyValuePair<string, PoolEntry>>(Pools.Count);
            var retiredEntries = new List<PoolEntry>();
            foreach (var item in Pools)
            {
                entries.Add(item);
                if (StopwatchTicksToTimeSpan(now - item.Value.LastAccessTimestamp) >= IdleEntryLifetime)
                {
                    if (TryRetireAndRemoveNoLock(item.Key, item.Value, false))
                    {
                        retiredEntries.Add(item.Value);
                    }
                }
            }

            var targetCount = reserveCapacity ? MaximumPoolEntries - 1 : MaximumPoolEntries;
            if (Pools.Count <= targetCount)
            {
                return retiredEntries.Count == 0 ? null : retiredEntries;
            }

            entries.Sort((left, right) =>
                left.Value.LastAccessTimestamp.CompareTo(right.Value.LastAccessTimestamp));
            for (var i = 0; i < entries.Count && Pools.Count > targetCount; i++)
            {
                if (TryRetireAndRemoveNoLock(entries[i].Key, entries[i].Value, false))
                {
                    retiredEntries.Add(entries[i].Value);
                }
            }

            return retiredEntries.Count == 0 ? null : retiredEntries;
        }

        private static void RemoveAndDispose(string key, PoolEntry entry, bool force)
        {
            bool retired;
            lock (MutationLock)
            {
                retired = TryRetireAndRemoveNoLock(key, entry, force);
            }

            if (retired)
            {
                entry.DisposePool();
            }
        }

        private static void RemoveRetiredEntry(string key, PoolEntry entry)
        {
            var shouldDispose = false;
            lock (MutationLock)
            {
                if (entry != null && entry.IsRetired)
                {
                    TryRemoveEntry(key, entry);
                    shouldDispose = true;
                }
            }

            if (shouldDispose)
            {
                entry.DisposePool();
            }
        }

        private static bool TryRetireAndRemoveNoLock(string key, PoolEntry entry, bool force)
        {
            if (entry == null || (!entry.IsRetired && !entry.TryRetire(force)))
            {
                return false;
            }

            TryRemoveEntry(key, entry);
            return true;
        }

        private static List<PoolEntry> RetireAllNoLock(bool force)
        {
            var retiredEntries = new List<PoolEntry>(Pools.Count);
            foreach (var item in Pools)
            {
                if (TryRetireAndRemoveNoLock(item.Key, item.Value, force))
                {
                    retiredEntries.Add(item.Value);
                }
            }

            return retiredEntries;
        }

        private static void DisposeEntries(List<PoolEntry> entries)
        {
            if (entries == null)
            {
                return;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                entries[i].DisposePool();
            }
        }

        private static void QueueEntriesForDisposal(List<PoolEntry> entries)
        {
            if (entries == null)
            {
                return;
            }

            try
            {
                if (ThreadPool.UnsafeQueueUserWorkItem(DisposeEntriesCallback, entries))
                {
                    return;
                }
            }
            catch (Exception e)
            {
                Trace.TraceWarning("WSClientAsyncPoolRegistry failed to queue retired pool disposal: " +
                                   DescribeException(e));
            }

            DisposeEntries(entries);
        }

        private static void DisposeEntriesInBackground(object state)
        {
            try
            {
                DisposeEntries((List<PoolEntry>)state);
            }
            catch (Exception e)
            {
                Trace.TraceWarning("WSClientAsyncPoolRegistry background pool disposal failed: " +
                                   DescribeException(e));
            }
        }

        private static bool TryRemoveEntry(string key, PoolEntry entry)
        {
            var entries = (ICollection<KeyValuePair<string, PoolEntry>>)Pools;
            return entries.Remove(new KeyValuePair<string, PoolEntry>(key, entry));
        }

        private static TimeSpan StopwatchTicksToTimeSpan(long stopwatchTicks)
        {
            if (stopwatchTicks <= 0)
            {
                return TimeSpan.Zero;
            }

            var ticks = stopwatchTicks * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;
            return TimeSpan.FromTicks((long)ticks);
        }

        private static string DescribeException(Exception exception)
        {
            var error = exception as TDengineError;
            return error == null
                ? exception.GetType().Name
                : $"{exception.GetType().Name}/0x{error.Code:x}";
        }

        private static ConnectionStringBuilder CloneBuilderForPool(ConnectionStringBuilder builder)
        {
            var clone = builder.CreateSnapshot();
            clone.Remove("pooling");
            return clone;
        }

        internal static string BuildPoolKey(ConnectionStringBuilder builder)
        {
            using (var hasher = new PoolKeyHasher())
            {
                hasher.Append("protocol", builder.Protocol);
                var addresses = builder.GetFailoverAddresses();
                hasher.Append("hostCount", addresses.Count);
                for (var i = 0; i < addresses.Count; i++)
                {
                    hasher.Append("host", addresses[i].CacheKey);
                }

                hasher.Append("port", builder.Port);
                hasher.Append("db", builder.Database);
                hasher.Append("username", builder.Username);
                hasher.Append("password", builder.Password);
                hasher.Append("token", builder.Token);
                hasher.Append("bearerToken", builder.BearerToken);
                hasher.Append("adapterHA", builder.AdapterHA);
                hasher.Append("useSSL", builder.UseSSL);
                hasher.Append("enableCompression", builder.EnableCompression);
                hasher.Append("autoReconnect", builder.AutoReconnect);
                hasher.Append("reconnectRetryCount", builder.ReconnectRetryCount);
                hasher.Append("reconnectIntervalMs", builder.ReconnectIntervalMs);
                hasher.Append("connTimeout", builder.ConnTimeout);
                hasher.Append("readTimeout", builder.ReadTimeout);
                hasher.Append("writeTimeout", builder.WriteTimeout);
                hasher.Append("timezone", builder.GetTimeZone().ToSerializedString());
                hasher.Append("connectionTimezone", builder.ConnectionTimezone == null
                    ? string.Empty
                    : builder.ConnectionTimezone.ToSerializedString());
                hasher.Append("minPoolSize", builder.MinPoolSize);
                hasher.Append("maxPoolSize", builder.MaxPoolSize);
                hasher.Append("poolConnectionTimeout", builder.PoolConnectionTimeout);
                hasher.Append("poolKeepaliveTime", builder.PoolKeepaliveTime);
                hasher.Append("poolMaxLifetime", builder.PoolMaxLifetime);
                hasher.Append("poolHousekeepingInterval", builder.PoolHousekeepingInterval);
                hasher.Append("poolCreationRetryBackoff", builder.PoolCreationRetryBackoff);
                hasher.Append("poolMaxCreationRetryBackoff", builder.PoolMaxCreationRetryBackoff);
                hasher.Append("poolLeakDetectionThreshold", builder.PoolLeakDetectionThreshold);
                return hasher.Complete();
            }
        }

        internal sealed class PoolEntry
        {
            private readonly Lazy<WSClientAsyncPool> _pool;
            private int _users;
            private int _retired;
            private int _disposeRequested;
            private int _poolInstanceDisposed;
            private long _lastAccessTimestamp;

            internal PoolEntry(ConnectionStringBuilder builder)
                : this(CreatePoolFactory(builder))
            {
            }

            private static Func<WSClientAsyncPool> CreatePoolFactory(ConnectionStringBuilder builder)
            {
                if (builder == null) throw new ArgumentNullException(nameof(builder));
                var snapshot = builder.CreateSnapshot();
                return () =>
                {
                    var poolBuilder = CloneBuilderForPool(snapshot);
                    var options = snapshot.CreateWebSocketAsyncPoolOptions();
                    return new WSClientAsyncPool(poolBuilder, options);
                };
            }

            internal PoolEntry(Func<WSClientAsyncPool> poolFactory)
            {
                if (poolFactory == null) throw new ArgumentNullException(nameof(poolFactory));
                _pool = new Lazy<WSClientAsyncPool>(poolFactory,
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _lastAccessTimestamp = Stopwatch.GetTimestamp();
            }

            internal long LastAccessTimestamp => Volatile.Read(ref _lastAccessTimestamp);

            internal bool IsRetired => Volatile.Read(ref _retired) != 0;

            internal bool TryEnter()
            {
                if (Volatile.Read(ref _retired) != 0)
                {
                    return false;
                }

                Interlocked.Increment(ref _users);
                if (Volatile.Read(ref _retired) == 0)
                {
                    Volatile.Write(ref _lastAccessTimestamp, Stopwatch.GetTimestamp());
                    return true;
                }

                Interlocked.Decrement(ref _users);
                return false;
            }

            internal void Exit()
            {
                Interlocked.Decrement(ref _users);
            }

            internal WSClientAsyncPool GetPool()
            {
                if (Volatile.Read(ref _disposeRequested) != 0)
                {
                    throw new ObjectDisposedException(nameof(PoolEntry));
                }

                var pool = _pool.Value;
                if (pool == null)
                {
                    Interlocked.Exchange(ref _disposeRequested, 1);
                    throw new InvalidOperationException("The WebSocket async pool factory returned null.");
                }

                if (Volatile.Read(ref _disposeRequested) == 0)
                {
                    return pool;
                }

                DisposePoolInstance(pool);
                throw new ObjectDisposedException(nameof(PoolEntry));
            }

            internal bool TryGetMetrics(out WSClientAsyncPoolMetrics metrics)
            {
                try
                {
                    if (Volatile.Read(ref _disposeRequested) != 0 || !_pool.IsValueCreated)
                    {
                        metrics = null;
                        return false;
                    }

                    var pool = _pool.Value;
                    if (pool == null)
                    {
                        metrics = null;
                        return false;
                    }

                    metrics = pool.GetMetrics();
                    return true;
                }
                catch
                {
                    metrics = null;
                    return false;
                }
            }

            internal bool TryRetire(bool force)
            {
                if (Volatile.Read(ref _retired) != 0)
                {
                    return false;
                }

                if (!force)
                {
                    if (Volatile.Read(ref _users) != 0)
                    {
                        return false;
                    }

                    if (_pool.IsValueCreated)
                    {
                        WSClientAsyncPoolMetrics metrics = null;
                        var poolIsUsable = true;
                        try
                        {
                            var pool = _pool.Value;
                            if (pool == null)
                            {
                                poolIsUsable = false;
                            }
                            else
                            {
                                metrics = pool.GetMetrics();
                                if (metrics == null)
                                {
                                    poolIsUsable = false;
                                }
                            }
                        }
                        catch
                        {
                            poolIsUsable = false;
                        }

                        if (!poolIsUsable)
                        {
                            Interlocked.Exchange(ref _disposeRequested, 1);
                        }
                        else if (metrics.ActiveConnections != 0 || metrics.ThreadsAwaitingConnection != 0 ||
                                 metrics.MaintenanceConnections != 0)
                        {
                            return false;
                        }
                    }
                }

                if (Interlocked.CompareExchange(ref _retired, 1, 0) != 0)
                {
                    return false;
                }

                if (force || Volatile.Read(ref _users) == 0)
                {
                    return true;
                }

                Volatile.Write(ref _retired, 0);
                return false;
            }

            internal void DisposePool()
            {
                Interlocked.Exchange(ref _disposeRequested, 1);

                if (!_pool.IsValueCreated)
                {
                    return;
                }

                try
                {
                    DisposePoolInstance(_pool.Value);
                }
                catch
                {
                    // A failed Lazy factory did not publish a pool instance to dispose.
                }
            }

            private void DisposePoolInstance(WSClientAsyncPool pool)
            {
                if (pool == null || Interlocked.Exchange(ref _poolInstanceDisposed, 1) != 0)
                {
                    return;
                }

                try
                {
                    pool.Dispose();
                }
                catch (Exception e)
                {
                    Trace.TraceWarning("WSClientAsyncPoolRegistry failed to dispose a retired pool: " +
                                       DescribeException(e));
                }
            }
        }

        private sealed class PoolKeyHasher : IDisposable
        {
            private static readonly byte[] EmptyBytes = new byte[0];
            private readonly HashAlgorithm _hash = SHA256.Create();
            private byte[] _buffer = ArrayPool<byte>.Shared.Rent(256);
            private bool _completed;

            internal void Append(string key, string value)
            {
                AppendString(key);
                AppendString(value ?? string.Empty);
            }

            internal void Append(string key, bool value)
            {
                Append(key, value ? "true" : "false");
            }

            internal void Append(string key, int value)
            {
                Append(key, value.ToString(CultureInfo.InvariantCulture));
            }

            internal void Append(string key, TimeSpan value)
            {
                Append(key, value.Ticks.ToString(CultureInfo.InvariantCulture));
            }

            internal string Complete()
            {
                if (_completed)
                {
                    throw new InvalidOperationException("The pool key hash has already been completed.");
                }

                _completed = true;
                _hash.TransformFinalBlock(EmptyBytes, 0, 0);
                return Convert.ToBase64String(_hash.Hash);
            }

            private void AppendString(string value)
            {
                // Hash UTF-16 code units directly. UTF-8's default replacement fallback
                // would make distinct invalid-surrogate credentials share a pool key.
                AppendLength(value.Length);
                var offset = 0;
                while (offset < value.Length)
                {
                    var charCount = Math.Min(value.Length - offset, _buffer.Length / sizeof(char));
                    var byteCount = charCount * sizeof(char);
                    for (var i = 0; i < charCount; i++)
                    {
                        var codeUnit = value[offset + i];
                        var byteOffset = i * sizeof(char);
                        _buffer[byteOffset] = (byte)codeUnit;
                        _buffer[byteOffset + 1] = (byte)(codeUnit >> 8);
                    }

                    try
                    {
                        _hash.TransformBlock(_buffer, 0, byteCount, _buffer, 0);
                    }
                    finally
                    {
                        Array.Clear(_buffer, 0, byteCount);
                    }

                    offset += charCount;
                }
            }

            private void AppendLength(int length)
            {
                _buffer[0] = (byte)length;
                _buffer[1] = (byte)(length >> 8);
                _buffer[2] = (byte)(length >> 16);
                _buffer[3] = (byte)(length >> 24);
                _hash.TransformBlock(_buffer, 0, sizeof(int), _buffer, 0);
                Array.Clear(_buffer, 0, sizeof(int));
            }

            public void Dispose()
            {
                if (_buffer != null)
                {
                    Array.Clear(_buffer, 0, _buffer.Length);
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = null;
                }

                _hash.Dispose();
            }
        }
    }
}
