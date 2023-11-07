// This project uses new C# templates to generate top-level statements
// See https://aka.ms/new-console-template for more information

/**
 * This project showcases logging to Better Stack
 */
using Serilog;

// Create logger
Log.Logger = new LoggerConfiguration()
    .WriteTo.BetterStack(sourceToken: "SOURCE_TOKEN")
    .WriteTo.Console()
    .MinimumLevel.Debug()
    .CreateLogger();

// Enables Serilog internal logs to help troubleshoot logging
Serilog.Debugging.SelfLog.Enable(Console.Error);

// Following code showcases 6 Serilog's default log levels
// Additionally, it also show how to structure logs to add additional data

// Trace the code or add detailed debugging info using the Verbose() method
// Due to MinimumLevel setting, this log event will be skipped
Log.Verbose("Tracing the code!");

// Send debug messages using the Debug() method
Log.Debug("Debugging is hard, but can be easier with Better Stack!");

// Send informative messages about application progress using the Info() method
// All of the properties that you pass to the log will be stored in a structured
// form in the context section of the logged event
Log.Information("User {User} - {UserId} just ordered item {Item}", "Josh", 95845, 75423);

// Use context to tag events with additional data
var loggerWithContext = Log.ForContext<Program>()
    .ForContext("ProcessId", 123)
    .ForContext("UserEmail", "user@example.com");

// Report non-critical issues using the Warn() method
loggerWithContext.Warning("Something is happening!");

// Send message about serious problems using the Error() method
loggerWithContext.Error("Error occurred! And it's not good.");

// Report fatal errors that caused application to crash using the Fatal() method
loggerWithContext.Fatal("Application crashed! Needs to be fixed ASAP!");

// For logs to be send to Better Stack reliably, Serilog must be properly closed
// see https://github.com/serilog/serilog/wiki/Lifecycle-of-Loggers
Log.CloseAndFlush();

Console.WriteLine("All done! Now, you can check Better Stack to see your logs");
