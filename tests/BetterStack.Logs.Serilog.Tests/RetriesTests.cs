using Microsoft.Extensions.Configuration;
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
    /// The sink retries a batch after every unsuccessful response and sends nothing else until it succeeds, so a batch that
    /// fails the same way every time blocks everything behind it. The sink's back-off makes real retries take minutes, so
    /// most tests drive the client the way the sink does: the same batch again after every failure.
    /// </summary>
    [Collection("SelfLog")]
    public class RetriesTests
    {
        [Theory]
        [InlineData("500 Internal Server Error", HttpStatusCode.InternalServerError,
            "A batch of logs was dropped after 11 attempts to send it to Better Stack. Last failure: 500 Internal Server Error. Response: Oops")]
        [InlineData("408 Request Timeout", HttpStatusCode.RequestTimeout,
            "A batch of logs was dropped after 11 attempts to send it to Better Stack. Last failure: 408 Request Timeout. Response: Oops")]
        [InlineData("429 Too Many Requests", (HttpStatusCode)429,
            "A batch of logs was dropped after 11 attempts to send it to Better Stack. Last failure: 429 Too Many Requests. Response: Oops")]
        [InlineData("reset", HttpStatusCode.ServiceUnavailable,
            "A batch of logs was dropped after 11 attempts to send it to Better Stack. Last failure: " +
            "System.Net.Http.HttpRequestException: An error occurred while sending the request. --> System.IO.IOException: Connection reset by peer")]
        [InlineData("timeout", HttpStatusCode.ServiceUnavailable,
            "A batch of logs was dropped after 11 attempts to send it to Better Stack. Last failure: System.Threading.Tasks.TaskCanceledException: A task was canceled.")]
        public async Task DropsBatchAfterTenRetries(string failure, HttpStatusCode failedStatus, string expectedSelfLog)
        {
            var handler = new ScriptedHandler(request => request <= 11 ? Fail(failure) : Accept());
            using var client = new BetterStackHttpClient("my-source-token", handler);
            var selfLog = new ConcurrentQueue<string>();
            SelfLog.Enable(selfLog.Enqueue);

            var statuses = new List<HttpStatusCode>();
            try
            {
                for (var attempt = 1; attempt <= 11; attempt++)
                {
                    statuses.Add((await Post(client, "[\"First\"]")).StatusCode);
                }

                statuses.Add((await Post(client, "[\"Second\"]")).StatusCode);
            }
            finally
            {
                SelfLog.Disable();
            }

            Assert.Equal(Enumerable.Repeat(failedStatus, 10).Concat(new[] { HttpStatusCode.Accepted, HttpStatusCode.Accepted }), statuses);
            Assert.Equal(Enumerable.Repeat("[\"First\"]", 11).Concat(new[] { "[\"Second\"]" }), handler.Bodies);
            Assert.Single(selfLog, line => line.EndsWith(" " + expectedSelfLog));
        }

        [Theory]
        [InlineData(0, 1, "A batch of logs was dropped after 1 attempt to send it to Better Stack. Last failure: 500 Internal Server Error. Response: Oops")]
        [InlineData(3, 4, "A batch of logs was dropped after 4 attempts to send it to Better Stack. Last failure: 500 Internal Server Error. Response: Oops")]
        public async Task DropsBatchAfterConfiguredRetries(int retries, int attempts, string expectedSelfLog)
        {
            var handler = new ScriptedHandler(request => request <= attempts ? Fail("500 Internal Server Error") : Accept());
            using var client = new BetterStackHttpClient("my-source-token", handler, retries);
            var selfLog = new ConcurrentQueue<string>();
            SelfLog.Enable(selfLog.Enqueue);

            var statuses = new List<HttpStatusCode>();
            try
            {
                for (var attempt = 1; attempt <= attempts; attempt++)
                {
                    statuses.Add((await Post(client, "[\"First\"]")).StatusCode);
                }

                statuses.Add((await Post(client, "[\"Second\"]")).StatusCode);
            }
            finally
            {
                SelfLog.Disable();
            }

            Assert.Equal(
                Enumerable.Repeat(HttpStatusCode.InternalServerError, attempts - 1).Concat(new[] { HttpStatusCode.Accepted, HttpStatusCode.Accepted }),
                statuses);
            Assert.Equal(Enumerable.Repeat("[\"First\"]", attempts).Concat(new[] { "[\"Second\"]" }), handler.Bodies);
            Assert.Single(selfLog, line => line.EndsWith(" " + expectedSelfLog));
        }

        [Fact]
        public void RejectsNegativeRetries()
        {
            var e = Assert.Throws<ArgumentOutOfRangeException>(() => new LoggerConfiguration()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: "http://127.0.0.1", retries: -1));

            Assert.Equal("retries", e.ParamName);
            Assert.Equal("retries", Assert.Throws<ArgumentOutOfRangeException>(() => new BetterStackHttpClient("my-source-token", null, -1)).ParamName);
        }

        [Fact]
        public async Task DeliversBatchOnTheLastAttempt()
        {
            var accepted = Accept();
            var handler = new ScriptedHandler(request => request <= 10 ? Fail("500 Internal Server Error") : accepted);
            using var client = new BetterStackHttpClient("my-source-token", handler);

            for (var attempt = 1; attempt <= 10; attempt++)
            {
                Assert.Equal(HttpStatusCode.InternalServerError, (await Post(client, "[\"First\"]")).StatusCode);
            }

            Assert.Same(accepted, await Post(client, "[\"First\"]"));
        }

        [Fact]
        public async Task CountsAgainForTheNextBatch()
        {
            var handler = new ScriptedHandler(request => Fail("500 Internal Server Error"));
            using var client = new BetterStackHttpClient("my-source-token", handler);

            var statuses = new List<HttpStatusCode>();
            for (var attempt = 1; attempt <= 22; attempt++)
            {
                statuses.Add((await Post(client, attempt <= 11 ? "[\"First\"]" : "[\"Second\"]")).StatusCode);
            }

            var batch = Enumerable.Repeat(HttpStatusCode.InternalServerError, 10).Concat(new[] { HttpStatusCode.Accepted });
            Assert.Equal(batch.Concat(batch), statuses);
        }

        [Theory]
        [InlineData(202)] // delivered
        [InlineData(401)] // rejected, and dropped right away
        public async Task CountsAgainAfterBatchIsDoneWith(int status)
        {
            var handler = new ScriptedHandler(request => request == 6 ? new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("") } : Fail("500 Internal Server Error"));
            using var client = new BetterStackHttpClient("my-source-token", handler);

            var statuses = new List<HttpStatusCode>();
            for (var attempt = 1; attempt <= 17; attempt++)
            {
                statuses.Add((await Post(client, attempt <= 6 ? "[\"First\"]" : "[\"Second\"]")).StatusCode);
            }

            Assert.Equal(
                Enumerable.Repeat(HttpStatusCode.InternalServerError, 5)
                    .Concat(new[] { HttpStatusCode.Accepted })
                    .Concat(Enumerable.Repeat(HttpStatusCode.InternalServerError, 10))
                    .Concat(new[] { HttpStatusCode.Accepted }),
                statuses);
        }

        [Fact]
        public void SinkMovesOnAfterRetriesFromJsonConfiguration()
        {
            using var receiver = new Receiver(HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError);
            var appsettings = @"{
                ""Serilog"": {
                    ""WriteTo"": [
                        {
                            ""Name"": ""BetterStack"",
                            ""Args"": {
                                ""sourceToken"": ""my-source-token"",
                                ""betterStackEndpoint"": """ + receiver.Url + @""",
                                ""batchInterval"": ""00:00:00.1"",
                                ""retries"": 1
                            }
                        }
                    ]
                }
            }";
            var configuration = new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(appsettings)))
                .Build();

            using (var logger = new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger())
            {
                logger.Information("First");
                receiver.WaitForRequests(1);
                logger.Information("Second");
                receiver.WaitForRequests(3);
            }

            // Without the limit the sink backs off for 10 seconds before the third attempt at the first batch
            Assert.Equal(
                new[] { "First", "First", "Second" },
                receiver.Requests.Select(request => (string)Assert.Single(request.Events)["message"]!));
        }

        private static Task<HttpResponseMessage> Post(BetterStackHttpClient client, string body)
        {
            return client.PostAsync("http://127.0.0.1/", new MemoryStream(Encoding.UTF8.GetBytes(body)), CancellationToken.None);
        }

        private static HttpResponseMessage Accept()
        {
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        /// <param name="failure">"reset", "timeout", or the status code and reason phrase of the response</param>
        private static HttpResponseMessage Fail(string failure)
        {
            switch (failure)
            {
                case "reset":
                    // Like HttpClient on .NET, which says what went wrong only in the inner exception
                    throw new HttpRequestException("An error occurred while sending the request.", new IOException("Connection reset by peer"));
                case "timeout":
                    throw new TaskCanceledException();
                default:
                    return new HttpResponseMessage((HttpStatusCode)int.Parse(failure.Substring(0, 3)))
                    {
                        ReasonPhrase = failure.Substring(4),
                        Content = new StringContent("Oops"),
                    };
            }
        }

        /// <summary>
        /// Answers the requests with the given function of their number, starting at 1, and records their bodies.
        /// </summary>
        private sealed class ScriptedHandler : HttpClientHandler
        {
            private readonly Func<int, HttpResponseMessage> respond;
            private readonly ConcurrentQueue<string> bodies = new ConcurrentQueue<string>();

            public ScriptedHandler(Func<int, HttpResponseMessage> respond)
            {
                this.respond = respond;
            }

            public IEnumerable<string> Bodies => bodies;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                bodies.Enqueue(await request.Content!.ReadAsStringAsync());
                return respond(bodies.Count);
            }
        }
    }
}
