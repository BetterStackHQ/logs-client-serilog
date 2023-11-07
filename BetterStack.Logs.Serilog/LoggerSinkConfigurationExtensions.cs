using BetterStack.Logs.Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Sinks.Http.BatchFormatters;
using Serilog.Sinks.Http.Private.NonDurable;
using System;

namespace Serilog
{
    /// <summary>
    /// Class containing extension method to <see cref="LoggerConfiguration"/> for Better Stack sink config
    /// </summary>
    public static class LoggerSinkConfigurationExtensions
    {
        /// <summary>
        /// Adds a sink that sends log events to Better Stack using preconfigured Serilog.Sinks.Http
        /// The log events are stored in memory in the case that the log server cannot be reached.
        /// </summary>
        /// <param name="sinkConfiguration">The logger configuration.</param>
        /// <param name="sourceToken">
        /// Your source token (taken from https://logs.betterstack.com/dashboard -> Sources -> Edit)
        /// </param>
        /// <param name="betterStackEndpoint">
        /// The URI of the Better Stack endpoint your logs are sent to. Default value is https://in.logs.betterstack.com.
        /// </param>
        /// <param name="queueLimitBytes">
        /// The maximum size of events stored in memory, waiting to be sent. Default value is null (no limit).
        /// </param>
        /// <param name="batchSize">
        /// The maximum number of log events sent as a single batch. Default value is 1000.
        /// </param>
        /// <param name="batchInterval">
        /// The maximum time before sending logs to Better Stack. Default value is 1 second.
        /// </param>
        /// <param name="restrictedToMinimumLevel">
        /// The minimum level for events passed through the sink. Ignored if <paramref name="levelSwitch"/> specified.
        /// Default value is <see cref="LevelAlias.Minimum"/>.
        /// </param>
        /// <param name="levelSwitch">
        /// A switch allowing the pass-through level to be changed at runtime.
        /// </param>
        public static LoggerConfiguration BetterStack(
            this LoggerSinkConfiguration sinkConfiguration,
            string sourceToken,
            string betterStackEndpoint = "https://in.logs.betterstack.com",
            long? queueLimitBytes = null,
            int? batchSize = null,
            TimeSpan? batchInterval = null,
            LogEventLevel restrictedToMinimumLevel = LevelAlias.Minimum,
            LoggingLevelSwitch? levelSwitch = null)
        {
            if (sinkConfiguration == null) throw new ArgumentNullException(nameof(sinkConfiguration));

            batchSize ??= 1000;
            batchInterval ??= TimeSpan.FromSeconds(1);

            var sink = new HttpSink(
                requestUri: betterStackEndpoint,
                queueLimitBytes: queueLimitBytes,
                logEventLimitBytes: null,
                logEventsInBatchLimit: batchSize,
                batchSizeLimitBytes: null,
                period: batchInterval.Value,
                textFormatter: new BetterStackTextFormatter(),
                batchFormatter: new ArrayBatchFormatter(),
                httpClient: new BetterStackHttpClient(sourceToken));

            return sinkConfiguration.Sink(sink, restrictedToMinimumLevel, levelSwitch);
        }
    }
}
