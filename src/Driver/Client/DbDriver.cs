using System;
using System.Threading;
using System.Threading.Tasks;
using TDengine.Driver.Client.Native;
using TDengine.Driver.Client.Websocket;

namespace TDengine.Driver.Client
{
    public static class DbDriver
    {
        public static ITDengineClient Open(ConnectionStringBuilder builder)
        {
            if (builder.Protocol == TDengineConstant.ProtocolWebSocket)
            {
                return new WSClient(builder);
            }

            return new NativeClient(builder);
        }

        public static Task<ITDengineClientAsync> OpenAsync(ConnectionStringBuilder builder)
        {
            return OpenAsync(builder, CancellationToken.None);
        }

        public static async Task<ITDengineClientAsync> OpenAsync(ConnectionStringBuilder builder,
            CancellationToken cancellationToken)
        {
            if (builder.Protocol == TDengineConstant.ProtocolWebSocket)
            {
                var client = new WSClientAsync(builder);
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return client;
            }

            throw new NotImplementedException("Native async is not implemented");
        }
    }
}
