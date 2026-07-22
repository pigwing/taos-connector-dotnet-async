using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;

namespace TDengine.Driver
{
    public static class ReqId
    {
        private static readonly long UuidHashId;
        private static readonly long Pid;
        private static long _timeAndSerial;
        private const long TimeAndSerialMask = (1L << 46) - 1;
        private const long TimeMask = (1L << 26) - 1;

        static ReqId()
        {
            var randomBytes = new byte[2];
            using (var randomNumberGenerator = RandomNumberGenerator.Create())
            {
                randomNumberGenerator.GetBytes(randomBytes);
            }

            UuidHashId = ((long)BitConverter.ToUInt16(randomBytes, 0) & 0x07ff) << 52;

            Pid = ((long)(Process.GetCurrentProcess().Id & 0x0f)) << 48;
            _timeAndSerial = GetCurrentTimePart();
        }

        public static long GetReqId()
        {
            while (true)
            {
                var current = Volatile.Read(ref _timeAndSerial);
                var currentTimePart = GetCurrentTimePart();
                var next = current >= currentTimePart
                    ? (current + 1) & TimeAndSerialMask
                    : currentTimePart;
                if (Interlocked.CompareExchange(ref _timeAndSerial, next, current) == current)
                {
                    return UuidHashId | Pid | next;
                }
            }
        }

        private static long GetCurrentTimePart()
        {
            var timeWindow = ((DateTime.UtcNow.Ticks - TDengineConstant.TimeZero.Ticks) / 10000) >> 8;
            return (timeWindow & TimeMask) << 20;
        }

        internal static long Normalize(long reqId, string parameterName)
        {
            if (reqId < 0)
            {
                throw new ArgumentOutOfRangeException(parameterName, reqId,
                    "Request id cannot be negative.");
            }

            return reqId == 0 ? GetReqId() : reqId;
        }

        private const uint C1 = 0xcc9e2d51;
        private const uint C2 = 0x1b873593;

        public static uint MurmurHash32(byte[] data, uint seed)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            uint h1 = seed;

            int nBlocks = data.Length / 4;
            int p = 0;
            uint k1;
            for (int i = 0; i < nBlocks; i++)
            {
                k1 = (uint)(data[p]
                            | (data[p + 1] << 8)
                            | (data[p + 2] << 16)
                            | (data[p + 3] << 24));

                k1 *= C1;
                k1 = (k1 << 15) | (k1 >> 17);
                k1 *= C2;

                h1 ^= k1;
                h1 = (h1 << 13) | (h1 >> 19);
                h1 = h1 * 5 + 0xe6546b64;

                p += 4;
            }

            var tailOffset = nBlocks * 4;
            k1 = 0;
            switch (data.Length & 3)
            {
                case 3:
                    k1 ^= (uint)data[tailOffset + 2] << 16;
                    goto case 2;
                case 2:
                    k1 ^= (uint)data[tailOffset + 1] << 8;
                    goto case 1;
                case 1:
                    k1 ^= data[tailOffset];
                    k1 *= C1;
                    k1 = (k1 << 15) | (k1 >> 17);
                    k1 *= C2;
                    h1 ^= k1;
                    break;
            }

            h1 ^= (uint)data.Length;

            h1 ^= h1 >> 16;
            h1 *= 0x85ebca6b;
            h1 ^= h1 >> 13;
            h1 *= 0xc2b2ae35;
            h1 ^= h1 >> 16;

            return h1;
        }
        
    }
}
