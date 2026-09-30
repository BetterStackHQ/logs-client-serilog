using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Debugging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BetterStack.Logs.Serilog.Tests
{
    /// <summary>
    /// The sink keeps events in memory while Better Stack cannot be reached, and drops the events logged while its queue is full.
    /// </summary>
    [Collection("SelfLog")]
    public class QueueLimitTests
    {
        // Long enough for nothing to be sent before the logger is disposed
        private static readonly TimeSpan OnlyOnClose = TimeSpan.FromHours(1);

        private const string QueueFull = " Queue has reached its limit and the log event will be dropped; data: {\"dt\":";

        // Makes an event about 1150 bytes long, three of them fit in 4096 bytes
        private static readonly string Padding = new string('x', 1000);

        [Fact]
        public void DropsEventsLoggedWhileTheQueueIsFullAndSendsTheQueuedOnesAfterTheOutage()
        {
            var selfLog = new ConcurrentQueue<string>();

            var delivered = LogSixEventsThroughOutage(queueLimitBytes: 4096, selfLog);

            Assert.Equal(new[] { 1, 2, 3, 4 }, delivered.Select(logEvent => (int)logEvent["properties"]!["Number"]!));
            var dropped = selfLog.Where(line => line.Contains(QueueFull)).ToArray();
            Assert.Equal(2, dropped.Length);
            Assert.Contains("\"message\":\"Event 5\"", dropped[0]);
            Assert.Contains("\"message\":\"Event 6\"", dropped[1]);
        }

        [Fact]
        public void KeepsAllEventsThroughTheOutageWithoutLimit()
        {
            var selfLog = new ConcurrentQueue<string>();

            var delivered = LogSixEventsThroughOutage(queueLimitBytes: long.MaxValue, selfLog);

            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, delivered.Select(logEvent => (int)logEvent["properties"]!["Number"]!));
            Assert.DoesNotContain(selfLog, line => line.Contains(QueueFull));
        }

        [Fact]
        public void DropsEventsOverThreeHundredMegabytesByDefault()
        {
            var selfLog = new ConcurrentQueue<string>();

            LogOneHundredFiveEventsOfThreeMegabytes(queueLimitBytes: null, selfLog);

            // 104 events of 3 MB fit in 300 MB (314572800 bytes), the 105th does not
            var dropped = Assert.Single(selfLog, line => line.Contains(QueueFull));
            Assert.Contains("\"message\":\"Event 105\"", dropped);
        }

        [Fact]
        public void KeepsEventsOverThreeHundredMegabytesWithoutLimit()
        {
            var selfLog = new ConcurrentQueue<string>();

            LogOneHundredFiveEventsOfThreeMegabytes(queueLimitBytes: long.MaxValue, selfLog);

            Assert.DoesNotContain(selfLog, line => line.Contains(QueueFull));
        }

        [Fact]
        public void ReadsQueueLimitFromJsonConfiguration()
        {
            using var receiver = new Receiver();
            var appsettings = @"{
                ""Serilog"": {
                    ""WriteTo"": [
                        {
                            ""Name"": ""BetterStack"",
                            ""Args"": {
                                ""sourceToken"": ""my-source-token"",
                                ""betterStackEndpoint"": """ + receiver.Url + @""",
                                ""queueLimitBytes"": 4096,
                                ""batchInterval"": ""01:00:00""
                            }
                        }
                    ]
                }
            }";
            var configuration = new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(appsettings)))
                .Build();
            var selfLog = new ConcurrentQueue<string>();
            SelfLog.Enable(selfLog.Enqueue);

            try
            {
                using (var logger = new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger())
                {
                    for (var i = 1; i <= 5; i++)
                    {
                        logger.ForContext("Padding", Padding).Information("Event {Number}", i);
                    }
                }
            }
            finally
            {
                SelfLog.Disable();
            }

            Assert.Equal(new[] { 1, 2, 3 }, receiver.Events.Select(logEvent => (int)logEvent["properties"]!["Number"]!));
            var dropped = selfLog.Where(line => line.Contains(QueueFull)).ToArray();
            Assert.Equal(2, dropped.Length);
            Assert.Contains("\"message\":\"Event 4\"", dropped[0]);
            Assert.Contains("\"message\":\"Event 5\"", dropped[1]);
        }

        private static IReadOnlyList<JObject> LogSixEventsThroughOutage(long queueLimitBytes, ConcurrentQueue<string> selfLog)
        {
            using var receiver = new Receiver();
            var handler = new OutageHandler();
            SelfLog.Enable(selfLog.Enqueue);

            try
            {
                using (var logger = new LoggerConfiguration()
                    .WriteTo.BetterStack(
                        sourceToken: "my-source-token",
                        betterStackEndpoint: receiver.Url,
                        queueLimitBytes: queueLimitBytes,
                        batchInterval: TimeSpan.FromMilliseconds(100),
                        httpClientHandler: handler)
                    .CreateLogger())
                {
                    logger.ForContext("Padding", Padding).Information("Event {Number}", 1);
                    Assert.True(SpinWait.SpinUntil(() => handler.FailedRequests > 0, TimeSpan.FromSeconds(30)));

                    // The sink retries the failed batch without reading the queue, so the queue fills up with these
                    for (var i = 2; i <= 6; i++)
                    {
                        logger.ForContext("Padding", Padding).Information("Event {Number}", i);
                    }

                    handler.EndOutage();
                    // The retried batch, then the queue
                    receiver.WaitForRequests(2);
                }
            }
            finally
            {
                SelfLog.Disable();
            }

            return receiver.Events;
        }

        private static void LogOneHundredFiveEventsOfThreeMegabytes(long? queueLimitBytes, ConcurrentQueue<string> selfLog)
        {
            // Better Stack stays unreachable, closing the logger sends a single request instead of 300 MB
            using var receiver = new Receiver(HttpStatusCode.ServiceUnavailable);
            // The queue counts UTF-8 bytes. The euro sign takes 3 of them and 2 bytes in a .NET string, which keeps the
            // test at 200 MB of memory for 300 MB in the queue.
            var padding = new string('€', 1000 * 1000);
            SelfLog.Enable(selfLog.Enqueue);

            try
            {
                using (var logger = new LoggerConfiguration()
                    .WriteTo.BetterStack(
                        sourceToken: "my-source-token",
                        betterStackEndpoint: receiver.Url,
                        queueLimitBytes: queueLimitBytes,
                        batchInterval: OnlyOnClose)
                    .CreateLogger())
                {
                    for (var i = 1; i <= 105; i++)
                    {
                        logger.ForContext("Padding", padding).Information("Event {Number}", i);
                    }
                }
            }
            finally
            {
                SelfLog.Disable();
            }
        }

        /// <summary>
        /// Fails every request like a network that cannot reach Better Stack, until the outage ends.
        /// </summary>
        private sealed class OutageHandler : HttpClientHandler
        {
            private int failedRequests;
            private volatile bool down = true;

            public int FailedRequests => failedRequests;

            public void EndOutage()
            {
                down = false;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (down)
                {
                    Interlocked.Increment(ref failedRequests);
                    throw new HttpRequestException("Better Stack cannot be reached");
                }

                return base.SendAsync(request, cancellationToken);
            }
        }
    }
}
