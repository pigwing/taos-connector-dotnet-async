using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Test.Fixture
{
    internal sealed class LoopbackWebSocketServer : IAsyncDisposable
    {
        private const int MaximumHttpHeaderSize = 32 * 1024;
        private readonly TcpListener _listener;
        private readonly Func<WebSocket, int, CancellationToken, Task> _handler;
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<Task, byte> _clientTasks = new ConcurrentDictionary<Task, byte>();
        private readonly ConcurrentQueue<Exception> _exceptions = new ConcurrentQueue<Exception>();
        private readonly SemaphoreSlim _acceptedSignal = new SemaphoreSlim(0);
        private readonly Task _acceptLoopTask;
        private int _acceptedConnections;

        internal LoopbackWebSocketServer(Func<WebSocket, int, CancellationToken, Task> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoopTask = AcceptLoopAsync();
        }

        internal int Port { get; }

        internal int AcceptedConnections => Volatile.Read(ref _acceptedConnections);

        internal async Task WaitForAcceptedConnectionsAsync(int count, TimeSpan timeout)
        {
            if (count <= 0)
            {
                return;
            }

            using (var timeoutCts = new CancellationTokenSource(timeout))
            {
                while (AcceptedConnections < count)
                {
                    await _acceptedSignal.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
            }
        }

        internal void ThrowIfFaulted()
        {
            if (_exceptions.IsEmpty)
            {
                return;
            }

            throw new AggregateException("The loopback WebSocket server failed.", _exceptions);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_disposeCts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (_disposeCts.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (_disposeCts.IsCancellationRequested)
                {
                    break;
                }

                var connectionNumber = Interlocked.Increment(ref _acceptedConnections);
                _acceptedSignal.Release();
                var task = HandleClientAsync(client, connectionNumber);
                _clientTasks.TryAdd(task, 0);
                _ = task.ContinueWith(completed => _clientTasks.TryRemove(completed, out _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        private async Task HandleClientAsync(TcpClient client, int connectionNumber)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    await UpgradeAsync(stream, _disposeCts.Token).ConfigureAwait(false);
                    using (var webSocket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30)))
                    {
                        await _handler(webSocket, connectionNumber, _disposeCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
                {
                }
                catch (WebSocketException) when (_disposeCts.IsCancellationRequested)
                {
                }
                catch (IOException) when (_disposeCts.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    _exceptions.Enqueue(e);
                }
            }
        }

        private static async Task UpgradeAsync(Stream stream, CancellationToken cancellationToken)
        {
            var headerBytes = new byte[MaximumHttpHeaderSize];
            var headerLength = 0;
            var terminatorLength = 0;
            while (headerLength < headerBytes.Length && terminatorLength < 4)
            {
                var read = await stream.ReadAsync(headerBytes, headerLength, 1, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The WebSocket client closed before completing the HTTP upgrade.");
                }

                var current = headerBytes[headerLength++];
                switch (terminatorLength)
                {
                    case 0:
                    case 2:
                        terminatorLength = current == (byte)'\r' ? terminatorLength + 1 : 0;
                        break;
                    case 1:
                    case 3:
                        terminatorLength = current == (byte)'\n' ? terminatorLength + 1 : 0;
                        break;
                }
            }

            if (terminatorLength != 4)
            {
                throw new InvalidDataException("The WebSocket HTTP upgrade header exceeded the test limit.");
            }

            var headers = Encoding.ASCII.GetString(headerBytes, 0, headerLength);
            var webSocketKey = GetHeaderValue(headers, "Sec-WebSocket-Key");
            if (string.IsNullOrWhiteSpace(webSocketKey))
            {
                throw new InvalidDataException("The WebSocket HTTP upgrade did not include Sec-WebSocket-Key.");
            }

            string accept;
            using (var sha1 = SHA1.Create())
            {
                var source = Encoding.ASCII.GetBytes(webSocketKey.Trim() +
                                                     "258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
                accept = Convert.ToBase64String(sha1.ComputeHash(source));
            }

            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
            await stream.WriteAsync(response, 0, response.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static string? GetHeaderValue(string headers, string name)
        {
            var lines = headers.Split(new[] { "\r\n" }, StringSplitOptions.None);
            for (var i = 0; i < lines.Length; i++)
            {
                var separator = lines[i].IndexOf(':');
                if (separator <= 0 ||
                    !string.Equals(lines[i].Substring(0, separator).Trim(), name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return lines[i].Substring(separator + 1).Trim();
            }

            return null;
        }

        public async ValueTask DisposeAsync()
        {
            _disposeCts.Cancel();
            _listener.Stop();
            try
            {
                await _acceptLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            var tasks = _clientTasks.Keys;
            if (tasks.Count != 0)
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            _acceptedSignal.Dispose();
            _disposeCts.Dispose();
            ThrowIfFaulted();
        }
    }
}
