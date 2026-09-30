using Serilog;
using Serilog.Debugging;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BetterStack.Logs.Serilog.Tests
{
    /// <summary>
    /// The sink keeps a batch and retries it for ever after any unsuccessful response, even one that Better Stack would
    /// give again every time.
    /// </summary>
    [Collection("SelfLog")]
    public class RejectedBatchTests
    {
        [Theory]
        [InlineData(400)]
        [InlineData(401)]
        [InlineData(403)]
        [InlineData(413)]
        public void DropsBatchRejectedWithClientError(int status)
        {
            using var receiver = new Receiver((HttpStatusCode)status);

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: receiver.Url, batchInterval: TimeSpan.FromMilliseconds(100))
                .CreateLogger())
            {
                logger.Information("Rejected");
                receiver.WaitForRequests(1);
                logger.Information("Accepted");
                receiver.WaitForRequests(2);
            }

            Assert.Equal(
                new[] { new[] { "Rejected" }, new[] { "Accepted" } },
                receiver.Requests.Select(request => request.Events.Select(logEvent => (string)logEvent["message"]!).ToArray()));
        }

        [Theory]
        [InlineData(408)]
        [InlineData(429)]
        [InlineData(500)]
        [InlineData(503)]
        public void RetriesBatchAfterRetryableStatus(int status)
        {
            using var receiver = new Receiver((HttpStatusCode)status);

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
        [InlineData(401, "Unauthorized", "Invalid source token",
            "Better Stack rejected a batch of logs with 401 Unauthorized, the batch was dropped. " +
            "Check the source token and the endpoint. Response: Invalid source token")]
        [InlineData(403, "Forbidden", "",
            "Better Stack rejected a batch of logs with 403 Forbidden, the batch was dropped. " +
            "Check the source token and the endpoint. Response: ")]
        [InlineData(413, "Payload Too Large", "Payload needs to be smaller than 10MB",
            "Better Stack rejected a batch of logs with 413 Payload Too Large, the batch was dropped. " +
            "Response: Payload needs to be smaller than 10MB")]
        public async Task ReportsRejectedBatchInSelfLog(int status, string reasonPhrase, string body, string expected)
        {
            var selfLog = new ConcurrentQueue<string>();
            var handler = new RespondingHandler(new HttpResponseMessage((HttpStatusCode)status)
            {
                ReasonPhrase = reasonPhrase,
                Content = new StringContent(body),
            });
            using var client = new BetterStackHttpClient("my-source-token", handler);
            SelfLog.Enable(selfLog.Enqueue);

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync("http://127.0.0.1/", new MemoryStream(Encoding.UTF8.GetBytes("[]")), CancellationToken.None);
            }
            finally
            {
                SelfLog.Disable();
            }

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Contains(selfLog, line => line.EndsWith(" " + expected));
        }

        [Fact]
        public void ReportsRefusedConnectionInSelfLog()
        {
            var selfLog = new ConcurrentQueue<string>();
            var prefix = " Received failed HTTP shipping result ServiceUnavailable: " +
                "The request to Better Stack could not be sent: System.Net.Http.HttpRequestException: ";
            SelfLog.Enable(selfLog.Enqueue);

            try
            {
                using var logger = new LoggerConfiguration()
                    .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: ClosedEndpoint(), batchInterval: TimeSpan.FromMilliseconds(100))
                    .CreateLogger();
                logger.Information("Hello");
                Assert.True(SpinWait.SpinUntil(() => selfLog.Any(line => line.Contains(prefix)), TimeSpan.FromSeconds(10)), string.Join(Environment.NewLine, selfLog));
            }
            finally
            {
                SelfLog.Disable();
            }

            // The exception messages follow on the same line, down to the refused socket, without the stack traces
            Assert.Contains(selfLog, line => line.Contains(prefix) && line.Contains(" --> System.Net.Sockets.SocketException: "));
            Assert.DoesNotContain(selfLog, line => line.Contains(prefix) && line.Contains("\n"));
        }

        [Fact]
        public void ReportsInnerExceptionsOfFailedRequestInSelfLog()
        {
            var selfLog = new ConcurrentQueue<string>();
            var expected = " Received failed HTTP shipping result ServiceUnavailable: The request to Better Stack could not be sent: " +
                "System.Net.Http.HttpRequestException: outer --> System.IO.IOException: Connection reset by peer";
            SelfLog.Enable(selfLog.Enqueue);

            try
            {
                using var logger = new LoggerConfiguration()
                    .WriteTo.BetterStack(
                        sourceToken: "my-source-token",
                        betterStackEndpoint: "http://127.0.0.1/",
                        batchInterval: TimeSpan.FromMilliseconds(100),
                        httpClientHandler: new ThrowingHandler(new HttpRequestException("outer", new IOException("Connection reset by peer"))))
                    .CreateLogger();
                logger.Information("Hello");
                Assert.True(SpinWait.SpinUntil(() => selfLog.Any(line => line.Contains("HttpRequestException: outer")), TimeSpan.FromSeconds(10)), string.Join(Environment.NewLine, selfLog));
            }
            finally
            {
                SelfLog.Disable();
            }

            // Both messages, and nothing after them like the stack trace of Exception.ToString()
            Assert.Contains(selfLog, line => line.EndsWith(expected));
        }

        private static string ClosedEndpoint()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return $"http://127.0.0.1:{port}";
        }

        private sealed class ThrowingHandler : HttpClientHandler
        {
            private readonly Exception exception;

            public ThrowingHandler(Exception exception)
            {
                this.exception = exception;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                throw exception;
            }
        }

        private sealed class RespondingHandler : HttpClientHandler
        {
            private readonly HttpResponseMessage response;

            public RespondingHandler(HttpResponseMessage response)
            {
                this.response = response;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(response);
            }
        }
    }
}
