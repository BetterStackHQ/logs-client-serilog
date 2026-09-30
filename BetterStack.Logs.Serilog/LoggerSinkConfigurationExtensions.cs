#nullable enable

using BetterStack.Logs.Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Sinks.Http.BatchFormatters;
using Serilog.Sinks.Http.Private.NonDurable;
using System;
using System.ComponentModel;
using System.Net.Http;

namespace Serilog
{
    /// <summary>
    /// Class containing extension method to <see cref="LoggerConfiguration"/> for Better Stack sink config
    /// </summary>
    public static class LoggerSinkConfigurationExtensions
    {
        // Better Stack rejects a request over 10 MB, and the sink would retry that batch for ever. The sink sizes a batch
        // by its formatted events alone, so the limit leaves room for the JSON array around them. An event over the limit
        // would not fit in any batch and is dropped when it is logged.
        private const long SizeLimitBytes = 5 * 1024 * 1024;

        // Events wait in memory while Better Stack cannot be reached, and a busy application would grow without bound
        // during an outage. The Java client keeps at most 100000 events, which is about 300 MB at 3 KB per event.
        private const long DefaultQueueLimitBytes = 300L * 1024 * 1024;

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
        /// The maximum size of events stored in memory, waiting to be sent. Default value is 300 MB.
        /// Events logged while the queue is full are dropped. Use <see cref="long.MaxValue"/> for no limit.
        /// </param>
        /// <param name="batchSize">
        /// The maximum number of log events sent as a single batch. Default value is 1000.
        /// A batch is also kept under 5 MB, and a single log event over 5 MB is dropped.
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
        /// <param name="httpClientHandler">
        /// Optional HttpClientHandler to configure the HttpClient.
        /// </param>
        /// <param name="outputTemplate">
        /// A message template describing the format of the message sent to Better Stack,
        /// e.g. "[{Level:u3}] {SourceContext} - {Message:lj}". Default value is null (the log message on its own).
        /// </param>
        /// <param name="formatProvider">
        /// Supplies culture-specific formatting information for the message. Default value is null.
        /// </param>
        public static LoggerConfiguration BetterStack(
            this LoggerSinkConfiguration sinkConfiguration,
            string sourceToken,
            string betterStackEndpoint = "https://in.logs.betterstack.com",
            long? queueLimitBytes = null,
            int? batchSize = null,
            TimeSpan? batchInterval = null,
            LogEventLevel restrictedToMinimumLevel = LevelAlias.Minimum,
            LoggingLevelSwitch? levelSwitch = null,
            HttpClientHandler? httpClientHandler = null,
            string? outputTemplate = null,
            IFormatProvider? formatProvider = null)
        {
            if (sinkConfiguration == null) throw new ArgumentNullException(nameof(sinkConfiguration));

            batchSize ??= 1000;
            batchInterval ??= TimeSpan.FromSeconds(1);

            var sink = new HttpSink(
                requestUri: betterStackEndpoint,
                queueLimitBytes: queueLimitBytes ?? DefaultQueueLimitBytes,
                logEventLimitBytes: SizeLimitBytes,
                logEventsInBatchLimit: batchSize,
                batchSizeLimitBytes: SizeLimitBytes,
                period: batchInterval.Value,
                flushOnClose: true,
                textFormatter: new BetterStackTextFormatter(outputTemplate, formatProvider),
                batchFormatter: new ArrayBatchFormatter(),
                httpClient: new BetterStackHttpClient(sourceToken, httpClientHandler));

            return sinkConfiguration.Sink(sink, restrictedToMinimumLevel, levelSwitch);
        }

        /// <summary>
        /// Keeps assemblies compiled against version 1.2.0 working.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static LoggerConfiguration BetterStack(
            this LoggerSinkConfiguration sinkConfiguration,
            string sourceToken,
            string betterStackEndpoint,
            long? queueLimitBytes,
            int? batchSize,
            TimeSpan? batchInterval,
            LogEventLevel restrictedToMinimumLevel,
            LoggingLevelSwitch? levelSwitch,
            HttpClientHandler? httpClientHandler)
        {
            // Naming outputTemplate selects the overload above, this one would call itself otherwise
            return sinkConfiguration.BetterStack(
                sourceToken,
                betterStackEndpoint,
                queueLimitBytes,
                batchSize,
                batchInterval,
                restrictedToMinimumLevel,
                levelSwitch,
                httpClientHandler,
                outputTemplate: null);
        }

        /// <summary>
        /// Keeps assemblies compiled against versions older than 1.2.0 working.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static LoggerConfiguration BetterStack(
            this LoggerSinkConfiguration sinkConfiguration,
            string sourceToken,
            string betterStackEndpoint,
            long? queueLimitBytes,
            int? batchSize,
            TimeSpan? batchInterval,
            LogEventLevel restrictedToMinimumLevel,
            LoggingLevelSwitch? levelSwitch)
        {
            return sinkConfiguration.BetterStack(
                sourceToken,
                betterStackEndpoint,
                queueLimitBytes,
                batchSize,
                batchInterval,
                restrictedToMinimumLevel,
                levelSwitch,
                httpClientHandler: null,
                outputTemplate: null);
        }
    }
}
