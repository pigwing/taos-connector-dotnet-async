using System;
using TDengine.Driver;
using Xunit;

namespace Driver.Test.Client.Query
{
    [Collection("WebSocket async collection")]
    public sealed class WebSocketAsyncOfflineTimestamp
    {
        [Theory]
        [InlineData(TDenginePrecision.TSDB_TIME_PRECISION_MILLI, 1700000000123L)]
        [InlineData(TDenginePrecision.TSDB_TIME_PRECISION_MICRO, 1700000000123456L)]
        [InlineData(TDenginePrecision.TSDB_TIME_PRECISION_NANO, 1700000000123456700L)]
        public void TimestampRoundTripPreservesRepresentableValues(TDenginePrecision precision, long timestamp)
        {
            var dateTime = TDengineConstant.ConvertTimestampToDateTime(timestamp, precision, TimeZoneInfo.Utc);
            var dateTimeOffset = TDengineConstant.ConvertTimestampToDateTimeOffset(timestamp, precision,
                TimeZoneInfo.Utc);

            Assert.Equal(timestamp, TDengineConstant.ConvertDateTimeToTimestamp(dateTime, precision));
            Assert.Equal(timestamp, TDengineConstant.ConvertDateTimeOffsetToTimestamp(dateTimeOffset, precision));
        }

        [Theory]
        [InlineData(TDenginePrecision.TSDB_TIME_PRECISION_MILLI)]
        [InlineData(TDenginePrecision.TSDB_TIME_PRECISION_MICRO)]
        public void OversizedTimestampScaleThrowsInsteadOfWrapping(TDenginePrecision precision)
        {
            Assert.Throws<OverflowException>(() =>
                TDengineConstant.ConvertTimestampToDateTime(long.MaxValue, precision, TimeZoneInfo.Utc));
            Assert.Throws<OverflowException>(() =>
                TDengineConstant.ConvertTimestampToDateTimeOffset(long.MinValue, precision, TimeZoneInfo.Utc));
        }

        [Fact]
        public void NanosecondDateTimeScaleThrowsInsteadOfWrapping()
        {
            var maxUtc = new DateTime(DateTime.MaxValue.Ticks, DateTimeKind.Utc);
            var maxOffset = new DateTimeOffset(maxUtc);

            Assert.Throws<OverflowException>(() => TDengineConstant.ConvertDateTimeToTimestamp(maxUtc,
                TDenginePrecision.TSDB_TIME_PRECISION_NANO));
            Assert.Throws<OverflowException>(() => TDengineConstant.ConvertDateTimeOffsetToTimestamp(maxOffset,
                TDenginePrecision.TSDB_TIME_PRECISION_NANO));
        }

        [Fact]
        public void TimestampOutsideDateTimeRangeThrowsInsteadOfReturningCorruptValue()
        {
            var maximumMilliseconds = (DateTime.MaxValue.Ticks - TDengineConstant.TimeZero.Ticks) / 10000;

            Assert.Throws<ArgumentOutOfRangeException>(() => TDengineConstant.ConvertTimestampToDateTime(
                maximumMilliseconds + 1, TDenginePrecision.TSDB_TIME_PRECISION_MILLI, TimeZoneInfo.Utc));
            Assert.Throws<ArgumentOutOfRangeException>(() => TDengineConstant.ConvertTimestampToDateTimeOffset(
                maximumMilliseconds + 1, TDenginePrecision.TSDB_TIME_PRECISION_MILLI, TimeZoneInfo.Utc));
        }
    }
}
