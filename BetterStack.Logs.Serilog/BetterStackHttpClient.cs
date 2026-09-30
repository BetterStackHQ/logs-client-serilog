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
        private readonly HttpClient httpClient;

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
        {
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
                // Like Exception.ToString() without the stack traces, a reset or a TLS failure says why only in an inner exception
                var failure = $"{e.GetType()}: {e.Message}";
                for (var inner = e.InnerException; inner != null; inner = inner.InnerException)
                {
                    failure += $" --> {inner.GetType()}: {inner.Message}";
                }

                // The sink drops the batch when sending throws, but keeps it for a retry after an unsuccessful response.
                // The sink writes this body to SelfLog next to the status, so it has to say that Better Stack did not answer.
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent($"The request to Better Stack could not be sent: {failure}")
                };
            }

            var status = (int)response.StatusCode;
            if (status < 400 || status >= 500 || status == 408 || status == 429)
            {
                return response;
            }

            // Better Stack would reject the batch again on every retry, and the sink sends nothing else until it succeeds
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            SelfLog.WriteLine(
                "Better Stack rejected a batch of logs with {0}, the batch was dropped.{1} Response: {2}",
                $"{status} {response.ReasonPhrase}",
                status == 401 || status == 403 ? " Check the source token and the endpoint." : "",
                body);
            response.Dispose();

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
