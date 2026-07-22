using System;

namespace TDengine.Driver
{
    internal static class TimeoutHelper
    {
        // CancellationTokenSource and Task.Delay use an Int32 millisecond timer.
        internal static readonly TimeSpan MaximumTimerTimeout =
            TimeSpan.FromMilliseconds(int.MaxValue);

        internal static bool IsSupportedTimerTimeout(TimeSpan value, bool allowZero)
        {
            return value >= (allowZero ? TimeSpan.Zero : TimeSpan.FromTicks(1)) &&
                   value <= MaximumTimerTimeout;
        }

        internal static void ValidateTimerTimeout(TimeSpan value, string paramName, bool allowZero)
        {
            if (!IsSupportedTimerTimeout(value, allowZero))
            {
                var minimum = allowZero ? TimeSpan.Zero : TimeSpan.FromTicks(1);
                throw new ArgumentOutOfRangeException(paramName, value,
                    $"Timeout must be between {minimum} and {MaximumTimerTimeout}.");
            }
        }
    }
}
