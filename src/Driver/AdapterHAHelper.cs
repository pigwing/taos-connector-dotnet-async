using System;
using System.Collections.Generic;

namespace TDengine.Driver
{
    internal static class AdapterHAHelper
    {
        internal const int MaximumExaminedInstances = 4096;

        internal static List<FailoverAddress> MergeDiscoveredAddresses(
            IReadOnlyList<FailoverAddress> existingAddresses,
            string[] discoveredInstances,
            string protocol,
            bool useSSL)
        {
            if (discoveredInstances == null || discoveredInstances.Length == 0)
            {
                return null;
            }

            var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existingAddresses != null)
            {
                for (var i = 0; i < existingAddresses.Count; i++)
                {
                    var address = existingAddresses[i];
                    if (address != null && !string.IsNullOrWhiteSpace(address.CacheKey))
                    {
                        existingKeys.Add(address.CacheKey);
                    }
                }
            }

            List<FailoverAddress> newAddresses = null;
            var examinedCount = Math.Min(discoveredInstances.Length, MaximumExaminedInstances);
            for (var i = 0; i < examinedCount; i++)
            {
                if (newAddresses != null && newAddresses.Count >= AdapterClusterRegistry.MaximumClusterAddresses)
                {
                    break;
                }

                if (!TryParseInstance(discoveredInstances[i], protocol, useSSL, out var address) ||
                    !existingKeys.Add(address.CacheKey))
                {
                    continue;
                }

                if (newAddresses == null)
                {
                    newAddresses = new List<FailoverAddress>();
                }

                newAddresses.Add(address);
            }

            return newAddresses;
        }

        internal static List<FailoverAddress> ParseInstances(string[] discoveredInstances, string protocol,
            bool useSSL)
        {
            if (discoveredInstances == null || discoveredInstances.Length == 0)
            {
                return null;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<FailoverAddress> result = null;
            var examinedCount = Math.Min(discoveredInstances.Length, MaximumExaminedInstances);
            for (var i = 0; i < examinedCount; i++)
            {
                if (result != null && result.Count >= AdapterClusterRegistry.MaximumClusterAddresses)
                {
                    break;
                }

                if (!TryParseInstance(discoveredInstances[i], protocol, useSSL, out var address) ||
                    !seen.Add(address.CacheKey))
                {
                    continue;
                }

                if (result == null)
                {
                    result = new List<FailoverAddress>();
                }

                result.Add(address);
            }

            return result;
        }

        private static bool TryParseInstance(string instance, string protocol, bool useSSL,
            out FailoverAddress address)
        {
            address = null;
            if (string.IsNullOrWhiteSpace(instance))
            {
                return false;
            }

            try
            {
                HostEndpointParser.ParseHostEndpoint(instance, "list_instances", out var host, out var port,
                    "adapter instance", allowBareIpv6: true);
                if (port <= 0)
                {
                    return false;
                }

                var cacheKey = HostEndpointParser.BuildFailoverCacheKey(protocol, useSSL, host, port);
                address = new FailoverAddress(host, port, cacheKey);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
