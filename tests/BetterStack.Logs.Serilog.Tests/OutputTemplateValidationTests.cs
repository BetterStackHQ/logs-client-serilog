using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using System;
using System.IO;
using System.Reflection;
using System.Text;
using Xunit;

namespace BetterStack.Logs.Serilog.Tests
{
    public class OutputTemplateValidationTests
    {
        private static readonly DateTimeOffset Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.FromHours(2));

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\r\n")]
        public void IgnoresEmptyOutputTemplate(string outputTemplate)
        {
            var formatter = new BetterStackTextFormatter(outputTemplate, null);

            Assert.Equal(
                "{\"dt\":\"2026-01-02T01:04:05.0060000Z\",\"level\":\"INFO\"," +
                "\"message\":\"User \\\"Josh\\\" paid 12.5\",\"messageTemplate\":\"User {User} paid {Total}\"," +
                "\"properties\":{\"User\":\"Josh\",\"Total\":12.5}}" + Environment.NewLine,
                Format(formatter));
        }

        [Theory]
        [InlineData("\"\"")]
        [InlineData("\"   \"")]
        [InlineData("null")]
        public void IgnoresEmptyOutputTemplateFromJsonConfiguration(string outputTemplate)
        {
            using var receiver = new Receiver();
            var configuration = Configuration(receiver.Url, outputTemplate);

            using (var logger = new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger())
            {
                logger.Information("User {User} signed in", "Josh");
            }

            Assert.Equal("User \"Josh\" signed in", (string)Assert.Single(receiver.Events)["message"]!);
        }

        [Theory]
        [InlineData("{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Message:lj}", "2026-01-02 03:04:05.006 +02:00 User Josh paid 12.5")]
        [InlineData("[{Level:w3}] {Total:0.00} {Missing:%} {Message}", "[inf] 12.50  User \"Josh\" paid 12.5")]
        public void AcceptsValidOutputTemplate(string outputTemplate, string expectedMessage)
        {
            var formatter = new BetterStackTextFormatter(outputTemplate, null);

            Assert.Equal(expectedMessage, (string)JObject.Parse(Format(formatter))["message"]!);
        }

        [Fact]
        public void RejectsInvalidOutputTemplateAtConfigurationTime()
        {
            var e = Assert.Throws<ArgumentException>(() => new LoggerConfiguration()
                .WriteTo.BetterStack(sourceToken: "my-source-token", betterStackEndpoint: "http://127.0.0.1", outputTemplate: "{Timestamp:%}"));

            Assert.Equal("outputTemplate", e.ParamName);
            Assert.StartsWith("The output template {Timestamp:%} cannot format log events: ", e.Message);
        }

        [Fact]
        public void RejectsInvalidOutputTemplateFromJsonConfiguration()
        {
            var configuration = Configuration("http://127.0.0.1", "\"{Timestamp:%}\"");

            // Serilog.Settings.Configuration calls the sink method through reflection
            var e = Assert.Throws<TargetInvocationException>(() => new LoggerConfiguration().ReadFrom.Configuration(configuration));

            Assert.Equal("outputTemplate", Assert.IsType<ArgumentException>(e.InnerException).ParamName);
        }

        private static IConfiguration Configuration(string endpoint, string outputTemplateJson)
        {
            var appsettings = @"{
                ""Serilog"": {
                    ""WriteTo"": [
                        {
                            ""Name"": ""BetterStack"",
                            ""Args"": {
                                ""sourceToken"": ""my-source-token"",
                                ""betterStackEndpoint"": """ + endpoint + @""",
                                ""outputTemplate"": " + outputTemplateJson + @"
                            }
                        }
                    ]
                }
            }";
            return new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(appsettings)))
                .Build();
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
                });
            var output = new StringWriter();
            formatter.Format(logEvent, output);
            return output.ToString();
        }
    }
}
