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
        /// <param name="maxFlushTime">
        /// The maximum time to wait for them, <see cref="TimeSpan.Zero"/> or any value above about 24 days for no limit.
        /// </param>
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

            // Task.Wait takes at most int.MaxValue milliseconds (about 24.8 days) and throws for more, a longer maxFlushTime
            // like TimeSpan.MaxValue is a way to say "no limit" too
            var noLimit = maxFlushTime == TimeSpan.Zero || maxFlushTime > TimeSpan.FromMilliseconds(int.MaxValue);

            if (!flush.Wait(noLimit ? Timeout.InfiniteTimeSpan : maxFlushTime))
            {
                SelfLog.WriteLine("Gave up waiting for queued logs to be sent to Better Stack after {0} ms", maxFlushTime.TotalMilliseconds);
            }
        }
    }
}
