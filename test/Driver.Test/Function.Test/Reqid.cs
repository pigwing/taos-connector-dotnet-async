using System.Text;
using System;
using System.Threading.Tasks;
using TDengine.Driver;
using Xunit;

namespace Driver.Test.Function.Test
{
    public class Reqid
    {
        [Fact]
        public void MurmurHash32_ReturnsExpectedHash()
        {
            // Arrange
            byte[] data = Encoding.UTF8.GetBytes("driver-go");
            uint seed = 0;

            // Act
            uint hash = ReqId.MurmurHash32(data, seed);

            // Assert
            uint expectedHash = 3037880692;
            Assert.Equal(expectedHash, hash);
        }

        [Fact]
        public void GetReqId()
        {
            var reqId = ReqId.GetReqId();
            var reqId2 = ReqId.GetReqId();
            Assert.NotEqual(0, reqId);
            Assert.True(reqId2 > reqId);
            Assert.NotEqual(reqId, reqId2);
        }

        [Fact]
        public void GetReqIdIsUniqueUnderParallelLoad()
        {
            const int requestCount = 100_000;
            var requestIds = new long[requestCount];

            Parallel.For(0, requestCount, i => requestIds[i] = ReqId.GetReqId());
            Array.Sort(requestIds);

            Assert.True(requestIds[0] > 0);
            for (var i = 1; i < requestIds.Length; i++)
            {
                Assert.NotEqual(requestIds[i - 1], requestIds[i]);
            }
        }
    }
}
