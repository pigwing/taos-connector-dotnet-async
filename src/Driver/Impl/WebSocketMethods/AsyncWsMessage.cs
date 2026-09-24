using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Threading;

namespace TDengine.Driver.Impl.WebSocketMethods
{
    internal sealed class AsyncWsMessage : IDisposable
    {
        private byte[] _pooledBuffer;

        internal AsyncWsMessage(byte[] message, int messageLength, WebSocketMessageType messageType,
            bool pooled)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
            if (messageLength < 0 || messageLength > message.Length)
                throw new ArgumentOutOfRangeException(nameof(messageLength));
            MessageLength = messageLength;
            MessageType = messageType;
            _pooledBuffer = pooled ? message : null;
        }

        internal AsyncWsMessage(string text, int byteLength)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
            MessageLength = byteLength;
            MessageType = WebSocketMessageType.Text;
        }

        internal AsyncWsMessage(Exception exception)
        {
            Exception = exception;
            MessageType = WebSocketMessageType.Close;
        }

        internal byte[] Message { get; }
        internal int MessageLength { get; }
        internal string Text { get; }
        internal WebSocketMessageType MessageType { get; }
        internal Exception Exception { get; }

        internal byte[] CopyExactBytes()
        {
            if (Message == null)
                throw new InvalidOperationException("The WebSocket message does not contain binary data.");
            if (_pooledBuffer == null && MessageLength == Message.Length)
                return Message;

            var exact = new byte[MessageLength];
            Buffer.BlockCopy(Message, 0, exact, 0, MessageLength);
            return exact;
        }

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _pooledBuffer, null);
            if (buffer == null)
                return;

            Array.Clear(buffer, 0, MessageLength);
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }
}
