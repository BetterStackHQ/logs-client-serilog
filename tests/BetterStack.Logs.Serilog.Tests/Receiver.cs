using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BetterStack.Logs.Serilog.Tests
{
    /// <summary>
    /// Local HTTP server standing in for Better Stack, recording every request it receives.
    /// </summary>
    internal sealed class Receiver : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        private readonly ConcurrentQueue<HttpStatusCode> firstResponses;
        private readonly ConcurrentQueue<ReceivedRequest> requests = new ConcurrentQueue<ReceivedRequest>();

        /// <param name="firstResponses">
        /// Status codes of the first responses, in order. Every later request gets 202 Accepted like in Better Stack.
        /// </param>
        public Receiver(params HttpStatusCode[] firstResponses)
        {
            this.firstResponses = new ConcurrentQueue<HttpStatusCode>(firstResponses);

            Url = $"http://127.0.0.1:{FreePort()}";
            listener.Prefixes.Add(Url + "/");
            listener.Start();
            Task.Run(Listen);
        }

        public string Url { get; }

        public IReadOnlyList<ReceivedRequest> Requests => requests.ToArray();

        /// <summary>
        /// Events of all received requests, in the order they arrived.
        /// </summary>
        public IReadOnlyList<JObject> Events => requests.SelectMany(request => request.Events).ToArray();

        public void WaitForRequests(int count)
        {
            if (!SpinWait.SpinUntil(() => requests.Count >= count, TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException($"Received {requests.Count} requests, expected {count}.");
            }
        }

        public void Dispose()
        {
            listener.Close();
        }

        private async Task Listen()
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException)
                {
                    // The listener was closed
                    return;
                }

                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    requests.Enqueue(new ReceivedRequest(
                        context.Request.HttpMethod,
                        context.Request.Url!.AbsolutePath,
                        context.Request.Headers["Authorization"],
                        context.Request.Headers["Content-Type"],
                        reader.ReadToEnd()));
                }

                context.Response.StatusCode = (int)(firstResponses.TryDequeue(out var status) ? status : HttpStatusCode.Accepted);
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }

    internal sealed class ReceivedRequest
    {
        public ReceivedRequest(string method, string path, string? authorization, string? contentType, string body)
        {
            Method = method;
            Path = path;
            Authorization = authorization;
            ContentType = contentType;
            Body = body;
        }

        public string Method { get; }
        public string Path { get; }
        public string? Authorization { get; }
        public string? ContentType { get; }
        public string Body { get; }

        // Dates are kept as the strings that were sent
        public IEnumerable<JObject> Events => JsonConvert
            .DeserializeObject<JArray>(Body, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })!
            .Cast<JObject>();
    }
}
