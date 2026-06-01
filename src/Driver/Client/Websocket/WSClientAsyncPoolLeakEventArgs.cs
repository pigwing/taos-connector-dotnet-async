using System;

namespace TDengine.Driver.Client.Websocket
{
    public sealed class WSClientAsyncPoolLeakEventArgs : EventArgs
    {
        internal WSClientAsyncPoolLeakEventArgs(TimeSpan elapsed, string stackTrace)
        {
            Elapsed = elapsed;
            StackTrace = stackTrace;
        }

        public TimeSpan Elapsed { get; }

        public string StackTrace { get; }
    }
}
