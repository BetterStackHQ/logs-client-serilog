using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Parsing;
using System;
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
    public class BetterStackSinkTests
    {
        // Long enough for nothing to be sent before the logger is disposed
        private static readonly TimeSpan OnlyOnClose = TimeSpan.FromHours(1);

        [Fact]
        public void SendsEventsAsAuthorizedJsonArray()
        {
            using var receiver = new Receiver();

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: receiver.Url, batchInterval: OnlyOnClose)
                .CreateLogger())
            {
                var timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.FromHours(2));
                var parser = new MessageTemplateParser();
                logger.Write(new LogEvent(
                    timestamp,
                    LogEventLevel.Information,
                    null,
                    parser.Parse("User {User} signed in"),
                    new[] { new LogEventProperty("User", new ScalarValue("Josh")) }));
                logger.Write(new LogEvent(
                    timestamp.AddSeconds(1),
                    LogEventLevel.Error,
                    new InvalidOperationException("Card declined"),
                    parser.Parse("Payment failed"),
                    new LogEventProperty[0]));
            }

            var request = Assert.Single(receiver.Requests);
            Assert.Equal("POST", request.Method);
            Assert.Equal("/", request.Path);
            Assert.Equal("Bearer my-source-token", request.Authorization);
            Assert.Equal("application/json", request.ContentType);
            Assert.Equal(
                "[{\"dt\":\"2026-01-02T01:04:05.0060000Z\",\"level\":\"INFO\"," +
                "\"message\":\"User \\\"Josh\\\" signed in\",\"messageTemplate\":\"User {User} signed in\"," +
                "\"properties\":{\"User\":\"Josh\"}}" + Environment.NewLine +
                ",{\"dt\":\"2026-01-02T01:04:06.0060000Z\",\"level\":\"ERROR\"," +
                "\"message\":\"Payment failed\",\"messageTemplate\":\"Payment failed\"," +
                "\"exception\":\"System.InvalidOperationException: Card declined\"}" + Environment.NewLine +
                "]",
                request.Body);
        }

        [Fact]
        public void SendsScalarsStructuresAndContextAsProperties()
        {
            using var receiver = new Receiver();
            var before = DateTime.UtcNow;

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: receiver.Url)
                .CreateLogger())
            {
                logger
                    .ForContext<BetterStackSinkTests>()
                    .ForContext("RequestId", "req-1")
                    .Information(
                        "User {User} - {UserId} paid {Total} for {@Order}, gift: {Gift}",
                        "Josh", 95845, 12.5, new Order { Id = 7, Items = new[] { "book", "pen" } }, true);
            }

            var logEvent = Assert.Single(receiver.Events);
            Assert.Equal("INFO", (string)logEvent["level"]!);
            Assert.Equal(
                "User \"Josh\" - 95845 paid 12.5 for Order { Id: 7, Items: [\"book\", \"pen\"] }, gift: True",
                (string)logEvent["message"]!);
            Assert.Equal("User {User} - {UserId} paid {Total} for {@Order}, gift: {Gift}", (string)logEvent["messageTemplate"]!);
            Assert.Null(logEvent["exception"]);
            Assert.Equal(
                "{\"User\":\"Josh\",\"UserId\":95845,\"Total\":12.5," +
                "\"Order\":{\"Id\":7,\"Items\":[\"book\",\"pen\"],\"_typeTag\":\"Order\"},\"Gift\":true," +
                "\"RequestId\":\"req-1\",\"SourceContext\":\"BetterStack.Logs.Serilog.Tests.BetterStackSinkTests\"}",
                logEvent["properties"]!.ToString(Formatting.None));

            var dt = (string)logEvent["dt"]!;
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$", dt);
            Assert.InRange(DateTime.Parse(dt).ToUniversalTime(), before, DateTime.UtcNow);
        }

        [Fact]
        public void CloseAndFlushDeliversAllQueuedEvents()
        {
            using var receiver = new Receiver();

            // BetterStack.Logs.Log would win over Serilog.Log inside the BetterStack.Logs namespace
            global::Serilog.Log.Logger = new LoggerConfiguration()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: receiver.Url, batchSize: 10, batchInterval: OnlyOnClose)
                .CreateLogger();
            for (var i = 1; i <= 25; i++)
            {
                global::Serilog.Log.Information("Event {Number}", i);
            }

            Assert.Empty(receiver.Requests);

            global::Serilog.Log.CloseAndFlush();

            Assert.Equal(new[] { 10, 10, 5 }, receiver.Requests.Select(request => request.Events.Count()));
            Assert.Equal(Enumerable.Range(1, 25), receiver.Events.Select(logEvent => (int)logEvent["properties"]!["Number"]!));
        }

        [Fact]
        public void RestrictedToMinimumLevelFiltersEvents()
        {
            using var receiver = new Receiver();

            using (var logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: receiver.Url, restrictedToMinimumLevel: LogEventLevel.Warning)
                .CreateLogger())
            {
                logger.Information("Below the minimum level");
                logger.Warning("At the minimum level");
                logger.Error("Above the minimum level");
            }

            Assert.Equal(
                new[] { "At the minimum level", "Above the minimum level" },
                receiver.Events.Select(logEvent => (string)logEvent["message"]!));
        }

        [Fact]
        public void LevelSwitchFiltersEvents()
        {
            using var receiver = new Receiver();
            var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Error);

            using (var logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: receiver.Url, levelSwitch: levelSwitch)
                .CreateLogger())
            {
                logger.Warning("Below the switch");
                logger.Error("At the switch");
                levelSwitch.MinimumLevel = LogEventLevel.Debug;
                logger.Debug("At the lowered switch");
                logger.Verbose("Below the lowered switch");
            }

            Assert.Equal(
                new[] { "At the switch", "At the lowered switch" },
                receiver.Events.Select(logEvent => (string)logEvent["message"]!));
        }

        [Fact]
        public void SendsThroughCustomHttpClientHandler()
        {
            using var receiver = new Receiver();
            var handler = new CountingHandler();

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: receiver.Url, batchInterval: OnlyOnClose, httpClientHandler: handler)
                .CreateLogger())
            {
                logger.Information("Hello");
            }

            Assert.Equal(1, handler.Requests);
            Assert.Equal("Hello", (string)Assert.Single(receiver.Events)["message"]!);
        }

        [Fact]
        public void RetriesBatchAfterServerError()
        {
            using var receiver = new Receiver(HttpStatusCode.InternalServerError);

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: receiver.Url, batchInterval: TimeSpan.FromMilliseconds(100))
                .CreateLogger())
            {
                logger.Information("Hello");
                receiver.WaitForRequests(2);
            }

            Assert.Equal(2, receiver.Requests.Count);
            Assert.Equal(receiver.Requests[0].Body, receiver.Requests[1].Body);
            Assert.Equal("Hello", (string)Assert.Single(receiver.Requests[1].Events)["message"]!);
        }

        [Theory]
        [InlineData(typeof(HttpRequestException))] // the server cannot be reached
        [InlineData(typeof(TaskCanceledException))] // the request timed out
        public void RetriesBatchAfterFailedRequest(Type failure)
        {
            using var receiver = new Receiver();
            var handler = new FailingOnceHandler((Exception)Activator.CreateInstance(failure)!);

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: receiver.Url, batchInterval: TimeSpan.FromMilliseconds(100), httpClientHandler: handler)
                .CreateLogger())
            {
                logger.Information("Hello");
                Assert.True(SpinWait.SpinUntil(() => handler.Requests > 0, TimeSpan.FromSeconds(30)));
            }

            Assert.Equal("Hello", (string)Assert.Single(receiver.Events)["message"]!);
        }

        [Fact]
        public void ReadsSinkFromJsonConfiguration()
        {
            using var receiver = new Receiver();
            var appsettings = @"{
                ""Serilog"": {
                    ""MinimumLevel"": ""Debug"",
                    ""WriteTo"": [
                        { ""Name"": ""BetterStack"", ""Args"": { ""sourceToken"": ""my-source-token"", ""betterStackEndpoint"": """ + receiver.Url + @""" } }
                    ]
                }
            }";
            var configuration = new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(appsettings)))
                .Build();

            using (var logger = new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger())
            {
                logger.Debug("Configured from JSON");
            }

            var request = Assert.Single(receiver.Requests);
            Assert.Equal("Bearer my-source-token", request.Authorization);
            Assert.Equal("DEBUG", (string)Assert.Single(request.Events)["level"]!);
            Assert.Equal("Configured from JSON", (string)Assert.Single(request.Events)["message"]!);
        }

        [Fact]
        public void SendsEventsLoggedThroughMicrosoftExtensionsLogging()
        {
            using var receiver = new Receiver();

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: receiver.Url)
                .CreateLogger())
            using (var factory = new SerilogLoggerFactory(logger))
            {
                factory.CreateLogger("Orders").LogWarning("Order {OrderId} is late", 7);
            }

            var logEvent = Assert.Single(receiver.Events);
            Assert.Equal("WARNING", (string)logEvent["level"]!);
            Assert.Equal("Order 7 is late", (string)logEvent["message"]!);
            Assert.Equal("Order {OrderId} is late", (string)logEvent["messageTemplate"]!);
            Assert.Equal(7, (int)logEvent["properties"]!["OrderId"]!);
            Assert.Equal("Orders", (string)logEvent["properties"]!["SourceContext"]!);
        }

        private sealed class Order
        {
            public int Id { get; set; }
            public string[] Items { get; set; } = new string[0];
        }

        private sealed class CountingHandler : HttpClientHandler
        {
            private int requests;

            public int Requests => requests;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref requests);
                return base.SendAsync(request, cancellationToken);
            }
        }

        private sealed class FailingOnceHandler : HttpClientHandler
        {
            private readonly Exception failure;
            private int requests;

            public FailingOnceHandler(Exception failure)
            {
                this.failure = failure;
            }

            public int Requests => requests;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref requests) == 1)
                {
                    throw failure;
                }

                return base.SendAsync(request, cancellationToken);
            }
        }
    }
}
