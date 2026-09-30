using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Debugging;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BetterStack.Logs.Serilog.Tests
{
    /// <summary>
    /// Disposing the logger sends the queued events, and an endpoint that never answers holds it for the HttpClient timeout
    /// of 100 seconds, or twice that when a request is already in flight.
    /// </summary>
    [Collection("SelfLog")]
    public class MaxFlushTimeTests
    {
        // Well under the HttpClient timeout, so a test fails quickly when disposing is not limited
        private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);

        [Fact]
        public void GivesUpWaitingForEndpointThatNeverAnswers()
        {
            using var endpoint = new SilentEndpoint();
            var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(
                    sourceToken: "my-source-token",
                    betterStackEndpoint: endpoint.Url,
                    batchInterval: TimeSpan.FromHours(1),
                    maxFlushTime: TimeSpan.FromMilliseconds(500))
                .CreateLogger();
            logger.Information("Hello");

            var selfLog = WaitForDispose(logger.Dispose);

            Assert.Contains(selfLog, line => line.EndsWith(" Gave up waiting for queued logs to be sent to Better Stack after 500 ms"));
        }

        [Fact]
        public void GivesUpWaitingForRequestInFlight()
        {
            using var endpoint = new SilentEndpoint();
            var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(
                    sourceToken: "my-source-token",
                    betterStackEndpoint: endpoint.Url,
                    batchInterval: TimeSpan.FromMilliseconds(100),
                    maxFlushTime: TimeSpan.FromMilliseconds(500))
                .CreateLogger();
            logger.Information("Hello");
            Assert.True(SpinWait.SpinUntil(endpoint.IsConnected, DisposeTimeout));

            var selfLog = WaitForDispose(logger.Dispose);

            Assert.Contains(selfLog, line => line.EndsWith(" Gave up waiting for queued logs to be sent to Better Stack after 500 ms"));
        }

        [Fact]
        public void DeliversQueuedEventsBeforeDisposeReturnsWithoutLimit()
        {
            using var receiver = new Receiver();

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(
                    sourceToken: "my-source-token",
                    betterStackEndpoint: receiver.Url,
                    batchSize: 10,
                    batchInterval: TimeSpan.FromHours(1),
                    maxFlushTime: TimeSpan.Zero)
                .CreateLogger())
            {
                for (var i = 1; i <= 25; i++)
                {
                    logger.Information("Event {Number}", i);
                }
            }

            Assert.Equal(new[] { 10, 10, 5 }, receiver.Requests.Select(request => request.Events.Count()));
            Assert.Equal(Enumerable.Range(1, 25), receiver.Events.Select(logEvent => (int)logEvent["properties"]!["Number"]!));
        }

        [Fact]
        public void RejectsNegativeMaxFlushTime()
        {
            var e = Assert.Throws<ArgumentOutOfRangeException>(() => new LoggerConfiguration()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: "http://127.0.0.1", maxFlushTime: TimeSpan.FromSeconds(-1)));

            Assert.Equal("maxFlushTime", e.ParamName);
        }

        [Theory]
        [InlineData("")]
        [InlineData(@", ""restrictedToMinimumLevel"": ""Information""")]
        [InlineData(@", ""levelSwitch"": ""$switch""")]
        public void GivesUpWaitingForEndpointThatNeverAnswersFromJsonConfiguration(string levelArgument)
        {
            using var endpoint = new SilentEndpoint();
            var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(Configuration(endpoint.Url, @", ""maxFlushTime"": ""00:00:00.5""" + levelArgument))
                .CreateLogger();
            logger.Information("Hello");

            var selfLog = WaitForDispose(logger.Dispose);

            Assert.Contains(selfLog, line => line.EndsWith(" Gave up waiting for queued logs to be sent to Better Stack after 500 ms"));
        }

#if NETCOREAPP
        [Fact]
        public void GivesUpWaitingOnDisposeAsync()
        {
            using var endpoint = new SilentEndpoint();
            var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(Configuration(endpoint.Url, @", ""maxFlushTime"": ""00:00:00.5"""))
                .CreateLogger();
            logger.Information("Hello");

            // Serilog disposes a sink that is not IAsyncDisposable synchronously
            var selfLog = WaitForDispose(() => logger.DisposeAsync().AsTask().GetAwaiter().GetResult());

            Assert.Contains(selfLog, line => line.EndsWith(" Gave up waiting for queued logs to be sent to Better Stack after 500 ms"));
        }
#endif

        [Fact]
        public void DeliversQueuedEventsBeforeDisposeReturns()
        {
            using var receiver = new Receiver();

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: receiver.Url, batchSize: 10, batchInterval: TimeSpan.FromHours(1))
                .CreateLogger())
            {
                for (var i = 1; i <= 25; i++)
                {
                    logger.Information("Event {Number}", i);
                }
            }

            Assert.Equal(new[] { 10, 10, 5 }, receiver.Requests.Select(request => request.Events.Count()));
            Assert.Equal(Enumerable.Range(1, 25), receiver.Events.Select(logEvent => (int)logEvent["properties"]!["Number"]!));
        }

        [Fact]
        public void DeliversQueuedEventsBeforeDisposeReturnsWithoutLimitFromJsonConfiguration()
        {
            using var receiver = new Receiver();

            using (var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(Configuration(receiver.Url, @", ""maxFlushTime"": ""00:00:00"""))
                .CreateLogger())
            {
                logger.Information("Hello");
            }

            Assert.Equal("Hello", (string)Assert.Single(receiver.Events)["message"]!);
        }

        /// <summary>
        /// Runs the dispose on another thread and waits for it at most <see cref="DisposeTimeout"/>.
        /// </summary>
        private static ConcurrentQueue<string> WaitForDispose(Action dispose)
        {
            var selfLog = new ConcurrentQueue<string>();
            SelfLog.Enable(selfLog.Enqueue);

            try
            {
                Assert.True(Task.Run(dispose).Wait(DisposeTimeout), $"Disposing the logger took longer than {DisposeTimeout}.");
            }
            finally
            {
                SelfLog.Disable();
            }

            return selfLog;
        }

        private static IConfiguration Configuration(string endpoint, string moreArguments)
        {
            var appsettings = @"{
                ""Serilog"": {
                    ""LevelSwitches"": { ""$switch"": ""Information"" },
                    ""WriteTo"": [
                        {
                            ""Name"": ""BetterStack"",
                            ""Args"": {
                                ""sourceToken"": ""my-source-token"",
                                ""betterStackEndpoint"": """ + endpoint + @""",
                                ""batchInterval"": ""01:00:00""" + moreArguments + @"
                            }
                        }
                    ]
                }
            }";
            return new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(appsettings)))
                .Build();
        }

        /// <summary>
        /// Accepts connections and never answers, like a stuck proxy.
        /// </summary>
        private sealed class SilentEndpoint : IDisposable
        {
            // The operating system accepts the connections into the backlog, nothing ever reads the requests
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);

            public SilentEndpoint()
            {
                listener.Start();
                Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            }

            public string Url { get; }

            public bool IsConnected()
            {
                return listener.Pending();
            }

            public void Dispose()
            {
                listener.Stop();
            }
        }
    }
}
