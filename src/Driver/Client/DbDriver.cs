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
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (builder.Protocol == TDengineConstant.ProtocolWebSocket)
            {
                if (builder.Pooling)
                {
                    return await WSClientAsyncPoolRegistry.AcquireAsync(builder, cancellationToken)
                        .ConfigureAwait(false);
                }

                var client = new WSClientAsync(builder);
                try
                {
                    await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    return client;
                }
                catch
                {
                    try
                    {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_0_OR_GREATER || NET5_0_OR_GREATER
                        await client.DisposeAsync().ConfigureAwait(false);
#else
                        client.Dispose();
#endif
                    }
                    catch
                    {
                        // Preserve the connection failure; the client is already unusable.
                    }

                    throw;
                }
            }

            throw new NotImplementedException("Native async is not implemented");
        }

        public static WSClientAsyncPool CreateWebSocketAsyncPool(ConnectionStringBuilder builder)
        {
            return CreateWebSocketAsyncPool(builder, null);
        }

        public static WSClientAsyncPool CreateWebSocketAsyncPool(ConnectionStringBuilder builder,
            WSClientAsyncPoolOptions options)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (builder.Protocol != TDengineConstant.ProtocolWebSocket)
            {
                throw new ArgumentException("WebSocket async pool requires WebSocket protocol.", nameof(builder));
            }

            return new WSClientAsyncPool(builder, options);
        }

        public static WSClientAsyncPoolMetrics GetWebSocketAsyncPoolMetrics(ConnectionStringBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            return WSClientAsyncPoolRegistry.GetMetrics(builder);
        }

        public static void ClearWebSocketAsyncPools()
        {
            WSClientAsyncPoolRegistry.Clear();
        }
    }
}
