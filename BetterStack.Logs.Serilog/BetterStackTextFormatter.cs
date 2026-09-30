#nullable enable

using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Formatting.Json;
using Serilog.Formatting;
using System.IO;
using System;

namespace BetterStack.Logs.Serilog
{
    /// <summary>
    /// JSON formatter serializing log events for ingestion by Better Stack.
    /// </summary>
    public class BetterStackTextFormatter : ITextFormatter
    {
        private readonly JsonValueFormatter jsonValueFormatter;
        private readonly MessageTemplateTextFormatter? messageFormatter;
        private readonly IFormatProvider? formatProvider;

        public BetterStackTextFormatter() : this(null, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the BetterStackTextFormatter class with a custom format of the message.
        /// </summary>
        /// <param name="outputTemplate">
        /// A message template describing the format of the message, e.g. "[{Level:u3}] {SourceContext} - {Message:lj}".
        /// The log message is rendered on its own when null, empty or whitespace.
        /// </param>
        /// <param name="formatProvider">
        /// Supplies culture-specific formatting information for the message, or null.
        /// </param>
        /// <exception cref="ArgumentException">The output template cannot format log events.</exception>
        public BetterStackTextFormatter(string? outputTemplate, IFormatProvider? formatProvider)
        {
            this.jsonValueFormatter = new JsonValueFormatter();
            this.formatProvider = formatProvider;

            // An empty setting, e.g. an environment override, would blank the message of every event
            if (!string.IsNullOrWhiteSpace(outputTemplate))
            {
                // netstandard2.0 does not tell the compiler that IsNullOrWhiteSpace rules out null
                this.messageFormatter = new MessageTemplateTextFormatter(outputTemplate!, formatProvider);

                // A template that cannot format this event would drop every event, so it fails when the logger is configured
                var probe = new LogEvent(DateTimeOffset.Now, LogEventLevel.Information, null, MessageTemplate.Empty, new LogEventProperty[0]);
                try
                {
                    messageFormatter.Format(probe, new StringWriter());
                }
                catch (Exception e)
                {
                    throw new ArgumentException($"The output template {outputTemplate} cannot format log events: {e.Message}", nameof(outputTemplate), e);
                }
            }
        }

        public void Format(LogEvent logEvent, TextWriter output)
        {
            try
            {
                var buffer = new StringWriter();
                FormatContent(logEvent, buffer);

                // If formatting was successful, write to output
                output.WriteLine(buffer.ToString());
            }
            catch (Exception e)
            {
                SelfLog.WriteLine(
                    "Event at {0} with message template {1} could not be formatted into JSON and will be dropped: {2}",
                    logEvent.Timestamp.ToString("o"),
                    logEvent.MessageTemplate.Text,
                    e
                );
            }
        }

        private void FormatContent(LogEvent logEvent, TextWriter output)
        {
            if (logEvent == null) throw new ArgumentNullException(nameof(logEvent));
            if (output == null) throw new ArgumentNullException(nameof(output));

            output.Write("{\"dt\":\"");
            output.Write(logEvent.Timestamp.UtcDateTime.ToString("o"));

            output.Write("\",\"level\":\"");
            var level = logEvent.Level.ToString().ToUpper();
            output.Write(level == "INFORMATION" ? "INFO" : level);

            output.Write("\",\"message\":");
            JsonValueFormatter.WriteQuotedJsonString(RenderMessage(logEvent), output);

            output.Write(",\"messageTemplate\":");
            JsonValueFormatter.WriteQuotedJsonString(logEvent.MessageTemplate.Text, output);

            if (logEvent.Exception != null)
            {
                output.Write(",\"exception\":");
                JsonValueFormatter.WriteQuotedJsonString(logEvent.Exception.ToString(), output);
            }

            if (logEvent.Properties.Count != 0)
            {
                output.Write(",\"properties\":{");

                var delimiter = string.Empty;
                foreach (var property in logEvent.Properties)
                {
                    output.Write(delimiter);
                    delimiter = ",";

                    JsonValueFormatter.WriteQuotedJsonString(property.Key, output);
                    output.Write(':');
                    jsonValueFormatter.Format(property.Value, output);
                }

                output.Write('}');
            }

            output.Write('}');
        }

        private string RenderMessage(LogEvent logEvent)
        {
            if (messageFormatter == null)
            {
                return logEvent.MessageTemplate.Render(logEvent.Properties, formatProvider);
            }

            var message = new StringWriter();
            messageFormatter.Format(logEvent, message);

            // Templates written for files and consoles end with {NewLine}{Exception}, which leaves a line break at the end
            return message.ToString().TrimEnd('\r', '\n');
        }
    }
}
