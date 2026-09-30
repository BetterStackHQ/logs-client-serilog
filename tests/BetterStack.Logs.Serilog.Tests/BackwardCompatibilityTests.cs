using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
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
            // Compiles only when the calls are not ambiguous
            using var client = new BetterStackHttpClient("my-source-token");
            using var clientWithHandler = new BetterStackHttpClient("my-source-token", null);
            using var clientWithRetries = new BetterStackHttpClient("my-source-token", null, 3);

            Assert.NotNull(typeof(BetterStackHttpClient).GetConstructor(new[] { typeof(string) }));
            Assert.NotNull(typeof(BetterStackHttpClient).GetConstructor(new[] { typeof(string), typeof(HttpClientHandler) }));
            Assert.NotNull(typeof(BetterStackHttpClient).GetConstructor(new[] { typeof(string), typeof(HttpClientHandler), typeof(int) }));
        }

        [Fact]
        public void ShowsOnlyTheCurrentSinkMethod()
        {
            var methods = typeof(BetterStackHttpClient).Assembly
                .GetType("Serilog.LoggerSinkConfigurationExtensions")!
                .GetMethods()
                .Where(method => method.Name == "BetterStack")
                .ToList();

            // The current one and the hidden ones of versions 1.1.0, 1.2.0 and 1.3.0
            Assert.Equal(4, methods.Count);
            var visible = Assert.Single(methods, method => method.GetCustomAttribute<EditorBrowsableAttribute>() == null);
            Assert.Equal(
                new[]
                {
                    "sinkConfiguration", "sourceToken", "betterStackEndpoint", "queueLimitBytes", "batchSize", "batchInterval",
                    "restrictedToMinimumLevel", "levelSwitch", "httpClientHandler", "outputTemplate", "formatProvider",
                    "maxFlushTime", "retries",
                },
                visible.GetParameters().Select(parameter => parameter.Name));
            Assert.Equal(
                new[]
                {
                    typeof(LoggerSinkConfiguration), typeof(string), typeof(string), typeof(long?), typeof(int?), typeof(TimeSpan?),
                    typeof(LogEventLevel), typeof(LoggingLevelSwitch), typeof(HttpClientHandler), typeof(string), typeof(IFormatProvider),
                    typeof(TimeSpan?), typeof(int?),
                },
                visible.GetParameters().Select(parameter => parameter.ParameterType));
        }

        [Fact]
        public void ResolvesPositionalCallsWithoutAmbiguity()
        {
            using var receiver = new Receiver();

            using (var logger = new LoggerConfiguration()
                .WriteTo.BetterStack("my-source-token", receiver.Url, null, null, null, LogEventLevel.Verbose, null)
                .WriteTo.BetterStack("my-source-token", receiver.Url, null, null, null, LogEventLevel.Verbose, null, null)
                .WriteTo.BetterStack("my-source-token", receiver.Url, null, null, null, LogEventLevel.Verbose, null, null, null, null)
                .WriteTo.BetterStack("my-source-token", receiver.Url, null, null, null, LogEventLevel.Verbose, null, null, null, null, null)
                .WriteTo.BetterStack("my-source-token", receiver.Url, null, null, null, LogEventLevel.Verbose, null, null, null, null, null, null)
                .WriteTo.BetterStack("my-source-token", receiver.Url)
                .CreateLogger())
            {
                logger.Information("Hello");
            }

            Assert.Equal(Enumerable.Repeat("Hello", 6), receiver.Events.Select(logEvent => (string)logEvent["message"]!));

            // Compiles only when the call is not ambiguous, not run as it would send to Better Stack
            Action<LoggerConfiguration> sourceTokenOnly = configuration => configuration.WriteTo.BetterStack("my-source-token");
            Assert.NotNull(sourceTokenOnly);
        }

        [Fact]
        public void ResolvesJsonConfigurationWithAnyOptionalArguments()
        {
            using var receiver = new Receiver();
            var optionalArguments = new[]
            {
                @"""queueLimitBytes"": 104857600",
                @"""batchSize"": 100",
                @"""batchInterval"": ""01:00:00""",
                @"""restrictedToMinimumLevel"": ""Verbose""",
                @"""levelSwitch"": ""$switch""",
                @"""outputTemplate"": ""{Message}""",
                @"""formatProvider"": ""System.Globalization.CultureInfo::InvariantCulture""",
                @"""maxFlushTime"": ""00:00:10""",
                @"""retries"": 3",
            };
            var combinations = 1 << optionalArguments.Length;

            for (var combination = 0; combination < combinations; combination++)
            {
                var arguments = optionalArguments.Where((argument, index) => (combination & 1 << index) != 0);
                var appsettings = @"{
                    ""Serilog"": {
                        ""LevelSwitches"": { ""$switch"": ""Verbose"" },
                        ""WriteTo"": [
                            {
                                ""Name"": ""BetterStack"",
                                ""Args"": {
                                    ""sourceToken"": ""my-source-token"",
                                    ""betterStackEndpoint"": """ + receiver.Url + @"""" + string.Concat(arguments.Select(argument => ", " + argument)) + @"
                                }
                            }
                        ]
                    }
                }";
                var configuration = new ConfigurationBuilder()
                    .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(appsettings)))
                    .Build();

                using var logger = new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger();
                logger.Information("Hello");
            }

            Assert.Equal(Enumerable.Repeat("Hello", 512), receiver.Events.Select(logEvent => (string)logEvent["message"]!));
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
