using Serilog;
using Serilog.Debugging;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using Xunit;

namespace BetterStack.Logs.Serilog.Tests
{
    /// <summary>
    /// Better Stack answers a request over 10 MB with 413, and the sink would keep retrying such a batch for ever.
    /// </summary>
    [Collection("SelfLog")]
    public class RequestSizeLimitTests
    {
        // Long enough for nothing to be sent before the logger is disposed
        private static readonly TimeSpan OnlyOnClose = TimeSpan.FromHours(1);

        [Fact]
        public void SplitsEventsOverTenMegabytesIntoSeveralRequests()
        {
            using var receiver = new Receiver();
            var padding = new string('x', 12 * 1024);

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: receiver.Url, batchInterval: OnlyOnClose)
                .CreateLogger())
            {
                for (var i = 1; i <= 1000; i++)
                {
                    logger.ForContext("Padding", padding).Information("Event {Number}", i);
                }
            }

            Assert.Equal(new[] { 421, 421, 158 },receiver.Requests.Select(request => request.Events.Count()));
            Assert.All(receiver.Requests, request => Assert.InRange(Encoding.UTF8.GetByteCount(request.Body), 0, 10 * 1024 * 1024));
            Assert.Equal(Enumerable.Range(1, 1000), receiver.Events.Select(logEvent => (int)logEvent["properties"]!["Number"]!));
        }

        [Fact]
        public void SendsEventsThatDoNotFitTogetherInSeparateRequests()
        {
            using var receiver = new Receiver();
            var padding = new string('x', 3 * 1024 * 1024);

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: receiver.Url, batchInterval: OnlyOnClose)
                .CreateLogger())
            {
                logger.ForContext("Padding", padding).Information("First");
                logger.ForContext("Padding", padding).Information("Second");
            }

            Assert.Equal(
                new[] { new[] { "First" }, new[] { "Second" } },
                receiver.Requests.Select(request => request.Events.Select(logEvent => (string)logEvent["message"]!).ToArray()));
        }

        [Fact]
        public void DropsEventOverFiveMegabytesAndSendsTheOthers()
        {
            using var receiver = new Receiver();
            var selfLog = new ConcurrentQueue<string>();
            SelfLog.Enable(selfLog.Enqueue);

            try
            {
                using (var logger = new LoggerConfiguration()
                    .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: receiver.Url, batchInterval: OnlyOnClose)
                    .CreateLogger())
                {
                    logger.Information("Before");
                    logger.ForContext("Padding", new string('x', 5 * 1024 * 1024)).Information("Too large");
                    logger.Information("After");
                }
            }
            finally
            {
                SelfLog.Disable();
            }

            Assert.Equal(new[] { "Before", "After" }, receiver.Events.Select(logEvent => (string)logEvent["message"]!));
            Assert.Contains(selfLog, line => line.Contains(
                " Log event exceeds the size limit of 5242880 bytes set for this sink and will be dropped; data: {\"dt\":"));
        }

        [Fact]
        public void SendsSmallEventsAsOneBatch()
        {
            using var receiver = new Receiver();

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: receiver.Url, batchInterval: OnlyOnClose)
                .CreateLogger())
            {
                for (var i = 1; i <= 1000; i++)
                {
                    logger.Information("Event {Number}", i);
                }
            }

            var request = Assert.Single(receiver.Requests);
            Assert.Equal(Enumerable.Range(1, 1000), request.Events.Select(logEvent => (int)logEvent["properties"]!["Number"]!));
        }
    }
}
