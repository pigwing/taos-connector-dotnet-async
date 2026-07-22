using System;

namespace TDengine.Driver.Client.Websocket
{
    public sealed class WSClientAsyncPoolOptions
    {
        public int MinIdle { get; set; }

        public int MaximumPoolSize { get; set; } = 10;

        public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

        public TimeSpan KeepaliveTime { get; set; } = TimeSpan.FromMinutes(2);

        public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromMinutes(30);

        public TimeSpan HousekeepingInterval { get; set; } = TimeSpan.FromSeconds(30);

        public TimeSpan CreationRetryBackoff { get; set; } = TimeSpan.FromMilliseconds(100);

        public TimeSpan MaxCreationRetryBackoff { get; set; } = TimeSpan.FromSeconds(2);

        public TimeSpan LeakDetectionThreshold { get; set; } = TimeSpan.Zero;

        public Action<WSClientAsyncPoolLeakEventArgs> LeakDetected { get; set; }

        internal WSClientAsyncPoolOptions CloneAndValidate()
        {
            if (MaximumPoolSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaximumPoolSize),
                    "MaximumPoolSize must be greater than zero.");
            }

            if (MinIdle < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MinIdle), "MinIdle cannot be negative.");
            }

            if (MinIdle > MaximumPoolSize)
            {
                throw new ArgumentOutOfRangeException(nameof(MinIdle),
                    "MinIdle cannot be greater than MaximumPoolSize.");
            }

            if (!TimeoutHelper.IsSupportedTimerTimeout(ConnectionTimeout, false))
            {
                throw new ArgumentOutOfRangeException(nameof(ConnectionTimeout),
                    "ConnectionTimeout must be greater than zero and within the supported timer range.");
            }

            if (!TimeoutHelper.IsSupportedTimerTimeout(KeepaliveTime, true))
            {
                throw new ArgumentOutOfRangeException(nameof(KeepaliveTime),
                    "KeepaliveTime must be within the supported timer range.");
            }

            if (!TimeoutHelper.IsSupportedTimerTimeout(MaxLifetime, true))
            {
                throw new ArgumentOutOfRangeException(nameof(MaxLifetime),
                    "MaxLifetime must be within the supported timer range.");
            }

            if (!TimeoutHelper.IsSupportedTimerTimeout(HousekeepingInterval, false))
            {
                throw new ArgumentOutOfRangeException(nameof(HousekeepingInterval),
                    "HousekeepingInterval must be greater than zero and within the supported timer range.");
            }

            if (!TimeoutHelper.IsSupportedTimerTimeout(CreationRetryBackoff, true))
            {
                throw new ArgumentOutOfRangeException(nameof(CreationRetryBackoff),
                    "CreationRetryBackoff must be within the supported timer range.");
            }

            if (!TimeoutHelper.IsSupportedTimerTimeout(MaxCreationRetryBackoff, true))
            {
                throw new ArgumentOutOfRangeException(nameof(MaxCreationRetryBackoff),
                    "MaxCreationRetryBackoff must be within the supported timer range.");
            }

            if (MaxCreationRetryBackoff != TimeSpan.Zero && CreationRetryBackoff > MaxCreationRetryBackoff)
            {
                throw new ArgumentOutOfRangeException(nameof(CreationRetryBackoff),
                    "CreationRetryBackoff cannot be greater than MaxCreationRetryBackoff.");
            }

            if (!TimeoutHelper.IsSupportedTimerTimeout(LeakDetectionThreshold, true))
            {
                throw new ArgumentOutOfRangeException(nameof(LeakDetectionThreshold),
                    "LeakDetectionThreshold must be within the supported timer range.");
            }

            return new WSClientAsyncPoolOptions
            {
                MinIdle = MinIdle,
                MaximumPoolSize = MaximumPoolSize,
                ConnectionTimeout = ConnectionTimeout,
                KeepaliveTime = KeepaliveTime,
                MaxLifetime = MaxLifetime,
                HousekeepingInterval = HousekeepingInterval,
                CreationRetryBackoff = CreationRetryBackoff,
                MaxCreationRetryBackoff = MaxCreationRetryBackoff,
                LeakDetectionThreshold = LeakDetectionThreshold,
                LeakDetected = LeakDetected
            };
        }
    }
}
