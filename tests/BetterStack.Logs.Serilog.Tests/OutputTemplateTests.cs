using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using System;
using System.Globalization;
using System.IO;
using System.Text;
using Xunit;

namespace BetterStack.Logs.Serilog.Tests
{
    public class OutputTemplateTests
    {
        private static readonly DateTimeOffset Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.FromHours(2));

        [Fact]
        public void RendersMessageWithOutputTemplate()
        {
            var formatter = new BetterStackTextFormatter("{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext} - {Message:lj}", null);

            Assert.Equal(
                "{\"dt\":\"2026-01-02T01:04:05.0060000Z\",\"level\":\"INFO\"," +
                "\"message\":\"03:04:05 [INF] Orders - User Josh paid 12.5\",\"messageTemplate\":\"User {User} paid {Total}\"," +
                "\"properties\":{\"User\":\"Josh\",\"Total\":12.5,\"SourceContext\":\"Orders\"}}" + Environment.NewLine,
                Format(formatter));
        }

        [Fact]
        public void TrimsLineBreaksAtTheEndOfTheMessage()
        {
            var formatter = new BetterStackTextFormatter("[{Level:u3}] {Message:lj}{NewLine}{Exception}", null);

            Assert.Equal(
                "{\"dt\":\"2026-01-02T01:04:05.0060000Z\",\"level\":\"INFO\"," +
                "\"message\":\"[INF] User Josh paid 12.5\",\"messageTemplate\":\"User {User} paid {Total}\"," +
                "\"properties\":{\"User\":\"Josh\",\"Total\":12.5,\"SourceContext\":\"Orders\"}}" + Environment.NewLine,
                Format(formatter));
        }

        [Fact]
        public void RendersMessageWithOutputTemplateAndFormatProvider()
        {
            var formatter = new BetterStackTextFormatter("[{Level:u3}] {Message:l}", new NumberFormatInfo { NumberDecimalSeparator = "," });

            Assert.Equal(
                "{\"dt\":\"2026-01-02T01:04:05.0060000Z\",\"level\":\"INFO\"," +
                "\"message\":\"[INF] User Josh paid 12,5\",\"messageTemplate\":\"User {User} paid {Total}\"," +
                "\"properties\":{\"User\":\"Josh\",\"Total\":12.5,\"SourceContext\":\"Orders\"}}" + Environment.NewLine,
                Format(formatter));
        }

        [Fact]
        public void RendersMessageWithFormatProviderOnly()
        {
            var formatter = new BetterStackTextFormatter(null, new NumberFormatInfo { NumberDecimalSeparator = "," });

            Assert.Equal(
                "{\"dt\":\"2026-01-02T01:04:05.0060000Z\",\"level\":\"INFO\"," +
                "\"message\":\"User \\\"Josh\\\" paid 12,5\",\"messageTemplate\":\"User {User} paid {Total}\"," +
                "\"properties\":{\"User\":\"Josh\",\"Total\":12.5,\"SourceContext\":\"Orders\"}}" + Environment.NewLine,
                Format(formatter));
        }

        [Fact]
        public void SinkRendersMessageWithOutputTemplate()
        {
            using var receiver = new Receiver();

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(
                    sourceToken: "my-source-token",
                    betterStackEndpoint: receiver.Url,
                    outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext} - {Message:lj}{NewLine}{Exception}")
                .CreateLogger())
            {
                logger.Write(new LogEvent(
                    Timestamp,
                    LogEventLevel.Error,
                    new InvalidOperationException("Card declined"),
                    new MessageTemplateParser().Parse("Payment of {Total} failed"),
                    new[]
                    {
                        new LogEventProperty("Total", new ScalarValue(12.5)),
                        new LogEventProperty("SourceContext", new ScalarValue("Orders")),
                    }));
            }

            var logEvent = Assert.Single(receiver.Events);
            Assert.Equal(
                "03:04:05 [ERR] Orders - Payment of 12.5 failed" + Environment.NewLine +
                "System.InvalidOperationException: Card declined",
                (string)logEvent["message"]!);
            Assert.Equal("2026-01-02T01:04:05.0060000Z", (string)logEvent["dt"]!);
            Assert.Equal("ERROR", (string)logEvent["level"]!);
            Assert.Equal("Payment of {Total} failed", (string)logEvent["messageTemplate"]!);
            Assert.Equal("System.InvalidOperationException: Card declined", (string)logEvent["exception"]!);
            Assert.Equal("{\"Total\":12.5,\"SourceContext\":\"Orders\"}", logEvent["properties"]!.ToString(Formatting.None));
        }

        [Fact]
        public void SinkRendersMessageWithFormatProvider()
        {
            using var receiver = new Receiver();

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack(
                    sourceToken: "my-source-token",
                    betterStackEndpoint: receiver.Url,
                    formatProvider: new NumberFormatInfo { NumberDecimalSeparator = "," })
                .CreateLogger())
            {
                logger.Information("Payment of {Total} received", 12.5);
            }

            Assert.Equal("Payment of 12,5 received", (string)Assert.Single(receiver.Events)["message"]!);
        }

        [Fact]
        public void ReadsOutputTemplateFromJsonConfiguration()
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
                                ""outputTemplate"": ""[{Level:u3}] {SourceContext} - {Message:lj}""
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
                logger.ForContext("SourceContext", "Orders").Information("User {User} signed in", "Josh");
            }

            Assert.Equal("[INF] Orders - User Josh signed in", (string)Assert.Single(receiver.Events)["message"]!);
        }

        private static string Format(BetterStackTextFormatter formatter)
        {
            var logEvent = new LogEvent(
                Timestamp,
                LogEventLevel.Information,
                null,
                new MessageTemplateParser().Parse("User {User} paid {Total}"),
                new[]
                {
                    new LogEventProperty("User", new ScalarValue("Josh")),
                    new LogEventProperty("Total", new ScalarValue(12.5)),
                    new LogEventProperty("SourceContext", new ScalarValue("Orders")),
                });
            var output = new StringWriter();
            formatter.Format(logEvent, output);
            return output.ToString();
        }
    }
}
