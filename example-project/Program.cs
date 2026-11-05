// This project uses new C# templates to generate top-level statements
// See https://aka.ms/new-console-template for more information

/**
 * This project showcases logging to Better Stack
 */
using Serilog;

// Read the source token and ingesting host from the environment
var sourceToken = Environment.GetEnvironmentVariable("BETTER_STACK_SOURCE_TOKEN") ?? "";
var ingestingHost = Environment.GetEnvironmentVariable("BETTER_STACK_INGESTING_HOST") ?? "";
if (sourceToken == "" || ingestingHost == "")
{
    Console.Error.WriteLine("Usage: BETTER_STACK_SOURCE_TOKEN=<source_token> BETTER_STACK_INGESTING_HOST=<ingesting_host> dotnet run");
    return 1;
}

// Enables Serilog internal logs to help troubleshoot logging, e.g. a batch of logs that Better Stack rejected
// They are counted, so that the program can tell at the end whether all logs were sent
var problems = 0;
Serilog.Debugging.SelfLog.Enable(message =>
{
    Interlocked.Increment(ref problems);
    Console.Error.WriteLine(message);
});

// Create logger
Log.Logger = new LoggerConfiguration()
    .WriteTo.BetterStack(
        sourceToken: sourceToken,
        betterStackEndpoint: $"https://{ingestingHost}"
    )
    .WriteTo.Console()
    .MinimumLevel.Debug()
    .CreateLogger();

// Following code showcases 6 Serilog's default log levels
// Additionally, it also show how to structure logs to add additional data

// Trace the code or add detailed debugging info using the Verbose() method
// Due to MinimumLevel setting, this log event will be skipped
Log.Verbose("Tracing the code!");

// Send debug messages using the Debug() method
Log.Debug("Debugging is hard, but can be easier with Better Stack!");

// Send informative messages about application progress using the Information() method
// All of the properties that you pass to the log will be stored in a structured
// form in the properties section of the logged event
Log.Information("User {User} - {UserId} just ordered item {Item}", "Josh", 95845, 75423);

// Use context to tag events with additional data
var loggerWithContext = Log.ForContext<Program>()
    .ForContext("ProcessId", 123)
    .ForContext("UserEmail", "user@example.com");

// Report non-critical issues using the Warning() method
loggerWithContext.Warning("Something is happening!");

// Send message about serious problems using the Error() method
loggerWithContext.Error("Error occurred! And it's not good.");

// Pass an exception as the first argument to send it along with its stack trace
try
{
    throw new InvalidOperationException("Item 75423 is out of stock");
}
catch (Exception exception)
{
    loggerWithContext.Error(exception, "Could not ship item {Item}", 75423);
}

// Report fatal errors that caused application to crash using the Fatal() method
loggerWithContext.Fatal("Application crashed! Needs to be fixed ASAP!");

// For logs to be send to Better Stack reliably, Serilog must be properly closed
// see https://github.com/serilog/serilog/wiki/Lifecycle-of-Loggers
Log.CloseAndFlush();

if (problems > 0)
{
    Console.Error.WriteLine("Serilog reported a problem, see above, so some logs may not have reached Better Stack");
    return 1;
}

Console.WriteLine("All done! Now, you can check Better Stack to see your logs");
return 0;
