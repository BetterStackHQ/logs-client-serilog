using Microsoft.Extensions.Configuration;
using Serilog.Sinks.Http;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Threading.Tasks;
using System;

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
        public BetterStackHttpClient(string sourceToken)
        {
            this.httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sourceToken);
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
            var content = new StreamContent(contentStream);
            content.Headers.Add("Content-Type", "application/json");

            var response = await httpClient
                .PostAsync(requestUri, content)
                .ConfigureAwait(false);

            return response;
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
