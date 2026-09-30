using Serilog.Events;
using Serilog.Parsing;
using System;
using System.IO;
using Xunit;

namespace BetterStack.Logs.Serilog.Tests
{
    public class BetterStackTextFormatterTests
    {
        private static readonly DateTimeOffset Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.FromHours(2));

        [Fact]
        public void WritesEventAsSingleJsonLine()
        {
            var logEvent = Event(
                LogEventLevel.Information,
                "User {User} ordered {Count} items",
                null,
                new LogEventProperty("User", new ScalarValue("Josh")),
                new LogEventProperty("Count", new ScalarValue(3)));

            Assert.Equal(
                "{\"dt\":\"2026-01-02T01:04:05.0060000Z\",\"level\":\"INFO\"," +
                "\"message\":\"User \\\"Josh\\\" ordered 3 items\",\"messageTemplate\":\"User {User} ordered {Count} items\"," +
                "\"properties\":{\"User\":\"Josh\",\"Count\":3}}" + Environment.NewLine,
                Format(logEvent));
        }

        [Fact]
        public void OmitsPropertiesAndExceptionWhenThereAreNone()
        {
            var logEvent = Event(LogEventLevel.Warning, "Disk is almost full", null);

            Assert.Equal(
                "{\"dt\":\"2026-01-02T01:04:05.0060000Z\",\"level\":\"WARNING\"," +
                "\"message\":\"Disk is almost full\",\"messageTemplate\":\"Disk is almost full\"}" + Environment.NewLine,
                Format(logEvent));
        }

        [Fact]
        public void WritesException()
        {
            var logEvent = Event(LogEventLevel.Error, "Payment failed", new InvalidOperationException("Card declined"));

            Assert.Equal(
                "{\"dt\":\"2026-01-02T01:04:05.0060000Z\",\"level\":\"ERROR\"," +
                "\"message\":\"Payment failed\",\"messageTemplate\":\"Payment failed\"," +
                "\"exception\":\"System.InvalidOperationException: Card declined\"}" + Environment.NewLine,
                Format(logEvent));
        }

        [Theory]
        [InlineData(LogEventLevel.Verbose, "VERBOSE")]
        [InlineData(LogEventLevel.Debug, "DEBUG")]
        [InlineData(LogEventLevel.Information, "INFO")]
        [InlineData(LogEventLevel.Warning, "WARNING")]
        [InlineData(LogEventLevel.Error, "ERROR")]
        [InlineData(LogEventLevel.Fatal, "FATAL")]
        public void WritesUpperCasedLevel(LogEventLevel level, string expected)
        {
            var logEvent = Event(level, "Hello", null);

            Assert.Equal(
                "{\"dt\":\"2026-01-02T01:04:05.0060000Z\",\"level\":\"" + expected + "\"," +
                "\"message\":\"Hello\",\"messageTemplate\":\"Hello\"}" + Environment.NewLine,
                Format(logEvent));
        }

        private static LogEvent Event(LogEventLevel level, string messageTemplate, Exception? exception, params LogEventProperty[] properties)
        {
            return new LogEvent(Timestamp, level, exception, new MessageTemplateParser().Parse(messageTemplate), properties);
        }

        private static string Format(LogEvent logEvent)
        {
            var output = new StringWriter();
            new BetterStackTextFormatter().Format(logEvent, output);
            return output.ToString();
        }
    }
}
