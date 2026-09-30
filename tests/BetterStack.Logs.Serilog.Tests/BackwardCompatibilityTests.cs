using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using Xunit;

namespace BetterStack.Logs.Serilog.Tests
{
    /// <summary>
    /// An application compiled against an older version of the package calls the exact signatures of that version.
    /// When one of them disappears, the application fails with MissingMethodException after upgrading the package.
    /// </summary>
    public class BackwardCompatibilityTests
    {
        [Fact]
        public void KeepsSinkMethodOfVersion110()
        {
            using var receiver = new Receiver();
            var method = SinkMethod(
                typeof(LoggerSinkConfiguration),
                typeof(string),
                typeof(string),
                typeof(long?),
                typeof(int?),
                typeof(TimeSpan?),
                typeof(LogEventLevel),
                typeof(LoggingLevelSwitch));
            var configuration = new LoggerConfiguration();

            method!.Invoke(null, new object?[] { configuration.WriteTo, "my-source-token", receiver.Url, null, null, null, LogEventLevel.Verbose, null });
            using (var logger = configuration.CreateLogger())
            {
                logger.Information("Hello");
            }

            Assert.Equal("Bearer my-source-token", Assert.Single(receiver.Requests).Authorization);
            Assert.Equal("Hello", (string)Assert.Single(receiver.Events)["message"]!);
        }

        [Fact]
        public void KeepsSinkMethodOfVersion120()
        {
            using var receiver = new Receiver();
            var method = SinkMethod(
                typeof(LoggerSinkConfiguration),
                typeof(string),
                typeof(string),
                typeof(long?),
                typeof(int?),
                typeof(TimeSpan?),
                typeof(LogEventLevel),
                typeof(LoggingLevelSwitch),
                typeof(HttpClientHandler));
            var configuration = new LoggerConfiguration();

            method!.Invoke(null, new object?[] { configuration.WriteTo, "my-source-token", receiver.Url, null, null, null, LogEventLevel.Verbose, null, null });
            using (var logger = configuration.CreateLogger())
            {
                logger.Information("Hello");
            }

            Assert.Equal("Bearer my-source-token", Assert.Single(receiver.Requests).Authorization);
            Assert.Equal("Hello", (string)Assert.Single(receiver.Events)["message"]!);
        }

        [Fact]
        public void KeepsSinkMethodOfVersion130()
        {
            using var receiver = new Receiver();
            var method = SinkMethod(
                typeof(LoggerSinkConfiguration),
                typeof(string),
                typeof(string),
                typeof(long?),
                typeof(int?),
                typeof(TimeSpan?),
                typeof(LogEventLevel),
                typeof(LoggingLevelSwitch),
                typeof(HttpClientHandler),
                typeof(string),
                typeof(IFormatProvider));
            var configuration = new LoggerConfiguration();

            method!.Invoke(null, new object?[] { configuration.WriteTo, "my-source-token", receiver.Url, null, null, null, LogEventLevel.Verbose, null, null, "[{Level:u3}] {Message}", null });
            using (var logger = configuration.CreateLogger())
            {
                logger.Information("Hello");
            }

            Assert.Equal("Bearer my-source-token", Assert.Single(receiver.Requests).Authorization);
            Assert.Equal("[INF] Hello", (string)Assert.Single(receiver.Events)["message"]!);
        }

        [Fact]
        public void KeepsTextFormatterConstructor()
        {
            Assert.NotNull(typeof(BetterStackTextFormatter).GetConstructor(Type.EmptyTypes));
        }

        [Fact]
        public void KeepsHttpClientConstructors()
        {
            // Compiles only when the call is not ambiguous
            using var client = new BetterStackHttpClient("my-source-token");

            Assert.NotNull(typeof(BetterStackHttpClient).GetConstructor(new[] { typeof(string) }));
            Assert.NotNull(typeof(BetterStackHttpClient).GetConstructor(new[] { typeof(string), typeof(HttpClientHandler) }));
        }

        [Fact]
        public void ResolvesPositionalCallsWithoutAmbiguity()
        {
            using var receiver = new Receiver();

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack("my-source-token", receiver.Url, null, null, null, LogEventLevel.Verbose, null)
                .WriteTo.BetterStack("my-source-token", receiver.Url, null, null, null, LogEventLevel.Verbose, null, null)
                .CreateLogger())
            {
                logger.Information("Hello");
            }

            Assert.Equal(new[] { "Hello", "Hello" }, receiver.Events.Select(logEvent => (string)logEvent["message"]!));
        }

        // Serilog.Sinks.Http has a class of the same name, so the type cannot be referenced directly
        private static MethodInfo? SinkMethod(params Type[] parameters)
        {
            return typeof(BetterStackHttpClient).Assembly
                .GetType("Serilog.LoggerSinkConfigurationExtensions")!
                .GetMethod("BetterStack", parameters);
        }
    }
}
