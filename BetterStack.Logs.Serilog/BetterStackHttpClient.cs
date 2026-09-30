using Microsoft.Extensions.Configuration;
using Serilog.Debugging;
using Serilog.Sinks.Http;
using System.ComponentModel;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Net;

namespace BetterStack.Logs.Serilog
{
    /// <summary>
    /// HTTP client sending JSON to Better Stack over the network.
    /// </summary>
    public class BetterStackHttpClient : IHttpClient
    {
        // Mirrors the default retries of the NLog client
        internal const int DefaultRetries = 10;

        private readonly HttpClient httpClient;
        private readonly int retries;

        // The sink sends one batch at a time and nothing else until it succeeds, so the failures in a row are attempts at
        // the same batch, and no two requests run at once
        private int failedAttempts;

        /// <summary>
        /// Initializes a new instance of the BetterStackHttpClient class with specified source token.
        /// </summary>
        /// <param name="sourceToken">
        /// Your source token (taken from https://logs.betterstack.com/dashboard -> Sources -> Edit)
        /// </param>
        /// <param name="httpClientHandler">
        /// Optional HttpClientHandler to configure the HttpClient.
        /// </param>
        #nullable enable
        public BetterStackHttpClient(string sourceToken, HttpClientHandler? httpClientHandler = null)
            : this(sourceToken, httpClientHandler, DefaultRetries)
        {
        }

        /// <summary>
        /// Initializes a new instance of the BetterStackHttpClient class with specified source token and number of retries.
        /// </summary>
        /// <param name="sourceToken">
        /// Your source token (taken from https://logs.betterstack.com/dashboard -> Sources -> Edit)
        /// </param>
        /// <param name="httpClientHandler">
        /// Optional HttpClientHandler to configure the HttpClient.
        /// </param>
        /// <param name="retries">
        /// The number of times a batch is sent again after its first attempt failed, before it is dropped. 0 sends a batch
        /// only once.
        /// </param>
        public BetterStackHttpClient(string sourceToken, HttpClientHandler? httpClientHandler, int retries)
        {
            if (retries < 0) throw new ArgumentOutOfRangeException(nameof(retries), retries, "retries cannot be negative, 0 sends a batch only once.");

            this.retries = retries;

            if (httpClientHandler != null)
            {
                this.httpClient = new HttpClient(httpClientHandler);
            }
            else
            {
                this.httpClient = new HttpClient();
            }            
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sourceToken);
        }

        /// <summary>
        /// Keeps assemblies compiled against versions older than 1.2.0 working.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public BetterStackHttpClient(string sourceToken) : this(sourceToken, null)
        {
        }

        ~BetterStackHttpClient()
        {
            Dispose(false);
        }

        /// <inheritdoc />
        public virtual void Configure(IConfiguration configuration)
        {
        }

        /// <inheritdoc />
        public virtual async Task<HttpResponseMessage> PostAsync(string requestUri, Stream contentStream)
        {
            return await PostAsync(requestUri, contentStream, CancellationToken.None);
        }

        /// <inheritdoc />
        public virtual async Task<HttpResponseMessage> PostAsync(string requestUri, Stream contentStream, CancellationToken cancellationToken)
        {
            using var content = new StreamContent(contentStream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            HttpResponseMessage response;
            try
            {
                response = await httpClient
                    .PostAsync(requestUri, content, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                var failure = $"{e.GetType()}: {e.Message}";
                if (IsLastAttempt())
                {
                    return Dropped(failure);
                }

                // The sink drops the batch when sending throws, but keeps it for a retry after an unsuccessful response.
                // The sink writes this body to SelfLog next to the status, so it has to say that Better Stack did not answer.
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent($"The request to Better Stack could not be sent and will be retried: {failure}")
                };
            }

            if (response.IsSuccessStatusCode)
            {
                failedAttempts = 0;
                return response;
            }

            var status = (int)response.StatusCode;
            if (status < 400 || status >= 500 || status == 408 || status == 429)
            {
                if (!IsLastAttempt())
                {
                    return response;
                }

                var failure = $"{status} {response.ReasonPhrase}. Response: {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}";
                response.Dispose();
                return Dropped(failure);
            }

            // Better Stack would reject the batch again on every retry, and the sink sends nothing else until it succeeds
            failedAttempts = 0;
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            SelfLog.WriteLine(
                "Better Stack rejected a batch of logs with {0}, the batch was dropped.{1} Response: {2}",
                $"{status} {response.ReasonPhrase}",
                status == 401 || status == 403 ? " Check the source token and the endpoint." : "",
                body);
            response.Dispose();

            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        // Counts a failed attempt at the current batch, and starts counting again for the next batch after the last one
        private bool IsLastAttempt()
        {
            if (++failedAttempts <= retries)
            {
                return false;
            }

            failedAttempts = 0;
            return true;
        }

        private HttpResponseMessage Dropped(string lastFailure)
        {
            // A batch that fails every time would otherwise hold back everything behind it for ever
            SelfLog.WriteLine(
                "A batch of logs was dropped after {0} {1} to send it to Better Stack. Last failure: {2}",
                retries + 1,
                retries == 0 ? "attempt" : "attempts",
                lastFailure);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                httpClient.Dispose();
            }
        }
    }
}
