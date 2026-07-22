using System;

namespace TDengine.Driver.Client.Websocket
{
    public sealed class WSClientAsyncPoolMetrics
    {
        internal WSClientAsyncPoolMetrics(
            int activeConnections,
            int idleConnections,
            int totalConnections,
            int threadsAwaitingConnection,
            int maintenanceConnections,
            long acquireCount,
            long acquireTimeoutCount,
            long creationCount,
            long creationFailureCount,
            long disposedConnectionCount,
            long recycledConnectionCount,
            long keepaliveCount,
            long keepaliveFailureCount,
            TimeSpan averageAcquireDuration,
            TimeSpan maxAcquireDuration)
        {
            ActiveConnections = activeConnections;
            IdleConnections = idleConnections;
            TotalConnections = totalConnections;
            ThreadsAwaitingConnection = threadsAwaitingConnection;
            MaintenanceConnections = maintenanceConnections;
            AcquireCount = acquireCount;
            AcquireTimeoutCount = acquireTimeoutCount;
            CreationCount = creationCount;
            CreationFailureCount = creationFailureCount;
            DisposedConnectionCount = disposedConnectionCount;
            RecycledConnectionCount = recycledConnectionCount;
            KeepaliveCount = keepaliveCount;
            KeepaliveFailureCount = keepaliveFailureCount;
            AverageAcquireDuration = averageAcquireDuration;
            MaxAcquireDuration = maxAcquireDuration;
        }

        public int ActiveConnections { get; }

        public int IdleConnections { get; }

        public int TotalConnections { get; }

        public int ThreadsAwaitingConnection { get; }

        public int MaintenanceConnections { get; }

        public long AcquireCount { get; }

        public long AcquireTimeoutCount { get; }

        public long CreationCount { get; }

        public long CreationFailureCount { get; }

        public long DisposedConnectionCount { get; }

        public long RecycledConnectionCount { get; }

        public long KeepaliveCount { get; }

        public long KeepaliveFailureCount { get; }

        public TimeSpan AverageAcquireDuration { get; }

        public TimeSpan MaxAcquireDuration { get; }
    }
}
