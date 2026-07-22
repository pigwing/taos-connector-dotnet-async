using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Test.Fixture
{
    internal sealed class SingleConnectionTcpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<NetworkStream, CancellationToken, Task> _handler;
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
        private readonly TaskCompletionSource<bool> _accepted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serverTask;
        private TcpClient? _client;
        private Exception? _exception;

        internal SingleConnectionTcpServer(Func<NetworkStream, CancellationToken, Task> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serverTask = RunAsync();
        }

        internal int Port { get; }

        internal async Task WaitForAcceptedConnectionAsync(TimeSpan timeout)
        {
            using (var timeoutCts = new CancellationTokenSource(timeout))
            using (timeoutCts.Token.Register(() => _accepted.TrySetCanceled()))
            {
                await _accepted.Task.ConfigureAwait(false);
            }
        }

        private async Task RunAsync()
        {
            try
            {
                _client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                _accepted.TrySetResult(true);
                using (_client)
                using (var stream = _client.GetStream())
                {
                    await _handler(stream, _disposeCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_disposeCts.IsCancellationRequested)
            {
            }
            catch (SocketException) when (_disposeCts.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                _exception = e;
                _accepted.TrySetException(e);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _disposeCts.Cancel();
            _listener.Stop();
            _client?.Dispose();
            await _serverTask.ConfigureAwait(false);
            _disposeCts.Dispose();
            if (_exception != null)
            {
                throw new AggregateException("The loopback TCP server failed.", _exception);
            }
        }
    }
}
