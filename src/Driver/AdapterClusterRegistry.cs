using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TDengine.Driver
{
    internal static class AdapterClusterRegistry
    {
        internal const int MaximumRegistryKeys = 4096;
        internal const int MaximumClusterAddresses = 1024;
        private static readonly TimeSpan EntryLifetime = TimeSpan.FromMinutes(30);

        private static readonly Dictionary<string, ClusterEntry> KnownClusters =
            new Dictionary<string, ClusterEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly object SyncLock = new object();

        internal static void RegisterCluster(IReadOnlyList<FailoverAddress> seedAddresses,
            IReadOnlyList<FailoverAddress> fullCluster)
        {
            if (seedAddresses == null || seedAddresses.Count == 0 || fullCluster == null || fullCluster.Count == 0)
            {
                return;
            }

            lock (SyncLock)
            {
                var now = Stopwatch.GetTimestamp();
                RemoveExpiredEntriesNoLock(now);

                var cluster = CopyValidAddresses(fullCluster);
                if (cluster.Length == 0)
                {
                    return;
                }

                var seedAliases = CopyKeys(seedAddresses, MaximumRegistryKeys);
                var memberKeys = CopyKeys(cluster, MaximumClusterAddresses);
                var lookupKeys = BuildKeys(seedAliases, memberKeys);
                if (lookupKeys.Count == 0)
                {
                    return;
                }

                var replacedEntries = new HashSet<ClusterEntry>();
                for (var i = 0; i < lookupKeys.Count; i++)
                {
                    if (KnownClusters.TryGetValue(lookupKeys[i], out var replacedEntry))
                    {
                        replacedEntries.Add(replacedEntry);
                    }
                }

                if (replacedEntries.Count != 0)
                {
                    var seenSeedAliases = new HashSet<string>(seedAliases, StringComparer.OrdinalIgnoreCase);
                    foreach (var replacedEntry in replacedEntries)
                    {
                        AddKeys(replacedEntry.SeedAliases, seedAliases, seenSeedAliases, MaximumRegistryKeys);
                    }

                    var staleKeys = new List<string>();
                    foreach (var item in KnownClusters)
                    {
                        if (replacedEntries.Contains(item.Value))
                        {
                            staleKeys.Add(item.Key);
                        }
                    }

                    for (var i = 0; i < staleKeys.Count; i++)
                    {
                        KnownClusters.Remove(staleKeys[i]);
                    }
                }

                var newKeys = BuildKeys(seedAliases, memberKeys);
                EnsureCapacityNoLock(newKeys, null);

                var registeredSeedAliases = GetRegisteredSeedAliases(seedAliases, newKeys);
                var entry = new ClusterEntry(cluster, registeredSeedAliases, now);
                for (var i = 0; i < newKeys.Count; i++)
                {
                    KnownClusters[newKeys[i]] = entry;
                }
            }
        }

        internal static IReadOnlyList<FailoverAddress> ExpandIfKnown(IReadOnlyList<FailoverAddress> seeds)
        {
            if (seeds == null || seeds.Count == 0)
            {
                return seeds;
            }

            lock (SyncLock)
            {
                var now = Stopwatch.GetTimestamp();
                for (var i = 0; i < seeds.Count; i++)
                {
                    var seed = seeds[i];
                    if (seed == null || string.IsNullOrWhiteSpace(seed.CacheKey))
                    {
                        continue;
                    }

                    if (!KnownClusters.TryGetValue(seed.CacheKey, out var entry))
                    {
                        continue;
                    }

                    if (IsExpired(entry, now))
                    {
                        RemoveEntryNoLock(entry);
                        continue;
                    }

                    entry.LastAccessTimestamp = now;
                    if (HasNewMembers(entry.Addresses, seeds))
                    {
                        return entry.Addresses;
                    }
                }
            }

            return seeds;
        }

        private static List<string> BuildKeys(IReadOnlyList<string> seedAliases,
            IReadOnlyList<string> memberKeys)
        {
            var keys = new List<string>(Math.Min(MaximumRegistryKeys, seedAliases.Count + memberKeys.Count));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddKeys(memberKeys, keys, seen, MaximumRegistryKeys);
            AddKeys(seedAliases, keys, seen, MaximumRegistryKeys);
            return keys;
        }

        private static string[] GetRegisteredSeedAliases(IReadOnlyList<string> seedAliases,
            IReadOnlyList<string> registeredKeys)
        {
            var registered = new HashSet<string>(registeredKeys, StringComparer.OrdinalIgnoreCase);
            var result = new List<string>(Math.Min(seedAliases.Count, registeredKeys.Count));
            for (var i = 0; i < seedAliases.Count; i++)
            {
                var alias = seedAliases[i];
                if (!string.IsNullOrWhiteSpace(alias) && registered.Contains(alias))
                {
                    result.Add(alias);
                }
            }

            return result.ToArray();
        }

        private static List<string> CopyKeys(IReadOnlyList<FailoverAddress> addresses, int maximumCount)
        {
            var keys = new List<string>(Math.Min(addresses.Count, maximumCount));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < addresses.Count; i++)
            {
                var address = addresses[i];
                if (address != null && !string.IsNullOrWhiteSpace(address.CacheKey) &&
                    seen.Add(address.CacheKey))
                {
                    if (keys.Count >= maximumCount)
                    {
                        break;
                    }

                    keys.Add(address.CacheKey);
                }
            }

            return keys;
        }

        private static void AddKeys(IReadOnlyList<string> source, ICollection<string> destination,
            ISet<string> seen, int maximumCount)
        {
            for (var i = 0; i < source.Count && destination.Count < maximumCount; i++)
            {
                var key = source[i];
                if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                {
                    destination.Add(key);
                }
            }
        }

        private static FailoverAddress[] CopyValidAddresses(IReadOnlyList<FailoverAddress> addresses)
        {
            var result = new List<FailoverAddress>(Math.Min(addresses.Count, MaximumClusterAddresses));
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < addresses.Count; i++)
            {
                if (result.Count >= MaximumClusterAddresses)
                {
                    break;
                }

                var address = addresses[i];
                if (address != null && !string.IsNullOrWhiteSpace(address.CacheKey) && keys.Add(address.CacheKey))
                {
                    result.Add(address);
                }
            }

            return result.ToArray();
        }

        private static bool HasNewMembers(IReadOnlyList<FailoverAddress> cluster,
            IReadOnlyList<FailoverAddress> seeds)
        {
            var seedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < seeds.Count; i++)
            {
                var seed = seeds[i];
                if (seed != null && !string.IsNullOrWhiteSpace(seed.CacheKey))
                {
                    seedKeys.Add(seed.CacheKey);
                }
            }

            for (var i = 0; i < cluster.Count; i++)
            {
                var address = cluster[i];
                if (address != null && !string.IsNullOrWhiteSpace(address.CacheKey) &&
                    !seedKeys.Contains(address.CacheKey))
                {
                    return true;
                }
            }

            return false;
        }

        private static void EnsureCapacityNoLock(IReadOnlyList<string> newKeys, ClusterEntry replacedEntry)
        {
            while (GetProjectedKeyCountNoLock(newKeys) > MaximumRegistryKeys)
            {
                ClusterEntry oldest = null;
                foreach (var item in KnownClusters)
                {
                    if (ReferenceEquals(item.Value, replacedEntry))
                    {
                        continue;
                    }

                    if (oldest == null || item.Value.LastAccessTimestamp < oldest.LastAccessTimestamp)
                    {
                        oldest = item.Value;
                    }
                }

                if (oldest == null)
                {
                    break;
                }

                RemoveEntryNoLock(oldest);
            }
        }

        private static int GetProjectedKeyCountNoLock(IReadOnlyList<string> newKeys)
        {
            var projectedCount = KnownClusters.Count;
            for (var i = 0; i < newKeys.Count; i++)
            {
                if (!KnownClusters.ContainsKey(newKeys[i]))
                {
                    projectedCount++;
                }
            }

            return projectedCount;
        }

        private static void RemoveExpiredEntriesNoLock(long now)
        {
            List<ClusterEntry> expired = null;
            foreach (var item in KnownClusters)
            {
                if (!IsExpired(item.Value, now) ||
                    (expired != null && expired.Contains(item.Value)))
                {
                    continue;
                }

                if (expired == null)
                {
                    expired = new List<ClusterEntry>();
                }

                expired.Add(item.Value);
            }

            if (expired == null)
            {
                return;
            }

            for (var i = 0; i < expired.Count; i++)
            {
                RemoveEntryNoLock(expired[i]);
            }
        }

        private static bool IsExpired(ClusterEntry entry, long now)
        {
            var elapsed = now - entry.LastAccessTimestamp;
            return elapsed > 0 && elapsed * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency >=
                EntryLifetime.Ticks;
        }

        private static void RemoveEntryNoLock(ClusterEntry entry)
        {
            var keys = new List<string>();
            foreach (var item in KnownClusters)
            {
                if (ReferenceEquals(item.Value, entry))
                {
                    keys.Add(item.Key);
                }
            }

            for (var i = 0; i < keys.Count; i++)
            {
                KnownClusters.Remove(keys[i]);
            }
        }

        internal static int Count
        {
            get
            {
                lock (SyncLock)
                {
                    return KnownClusters.Count;
                }
            }
        }

        internal static void Clear()
        {
            lock (SyncLock)
            {
                KnownClusters.Clear();
            }
        }

        private sealed class ClusterEntry
        {
            internal ClusterEntry(FailoverAddress[] addresses, string[] seedAliases, long lastAccessTimestamp)
            {
                Addresses = addresses;
                SeedAliases = seedAliases;
                LastAccessTimestamp = lastAccessTimestamp;
            }

            internal FailoverAddress[] Addresses { get; }

            internal string[] SeedAliases { get; }

            internal long LastAccessTimestamp { get; set; }
        }
    }
}
