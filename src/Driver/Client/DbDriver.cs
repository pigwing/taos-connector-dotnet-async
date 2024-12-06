using System;
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

        public static async Task<ITDengineClientAsync> OpenAsync(ConnectionStringBuilder builder)
        {
            if (builder.Protocol == "WebSocket")
            {
                WSClientAsync client = new WSClientAsync(builder);
                await client.ConnectAsync();
                return client;
            }
            throw new NotImplementedException("Native Not Implemented");
        }
    }
}