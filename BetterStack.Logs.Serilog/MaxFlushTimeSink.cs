#nullable enable

using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Sinks.Http.Private.NonDurable;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterStack.Logs.Serilog
{
    /// <summary>
    /// Limits how long disposing the logger waits for the queued log events to be sent.
    /// </summary>
    internal sealed class MaxFlushTimeSink : ILogEventSink, IDisposable
    {
        private readonly HttpSink sink;
        private readonly TimeSpan maxFlushTime;

        /// <param name="sink">The sink sending the log events, which sends the queued ones when it is disposed.</param>
        /// <param name="maxFlushTime">The maximum time to wait for them, <see cref="TimeSpan.Zero"/> for no limit.</param>
        public MaxFlushTimeSink(HttpSink sink, TimeSpan maxFlushTime)
        {
            this.sink = sink;
            this.maxFlushTime = maxFlushTime;
        }

        public void Emit(LogEvent logEvent)
        {
            sink.Emit(logEvent);
        }

        public void Dispose()
        {
            // An endpoint that never answers holds the flush for the HttpClient timeout, once more for a request in flight.
            // A thread-pool thread is a background thread, so the flush left behind does not keep the process alive.
            var flush = Task.Run(sink.Dispose);

            if (!flush.Wait(maxFlushTime == TimeSpan.Zero ? Timeout.InfiniteTimeSpan : maxFlushTime))
            {
                SelfLog.WriteLine("Gave up waiting for queued logs to be sent to Better Stack after {0} ms", maxFlushTime.TotalMilliseconds);
            }
        }
    }
}
