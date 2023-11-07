# Example project

To help you get started with using BetterStack in your .NET projects,
we have prepared a simple program that showcases the usage of BetterStack logger.

## Download and install the example project

You can download the example project from GitHub directly or you can clone it to a select directory.

## Run the example project using Visual Studio
 
Replace `SOURCE_TOKEN` with your actual source token in the `Program.cs` file.
You can find your source token by going to [Better Stack Logs](https://logs.betterstack.com/dashboard) -> Sources -> Edit.

Open the `ExampleProject.csproj` file in the Visual Studio.
Then click on the green play button `ExampleProject` or press **F5** to run the application.

You should see the following output:

```powershell
All done! Now, you can check Better Stack to see your logs
```

## Run in the command line

Replace `SOURCE_TOKEN` with your actual source token in the `Program.cs` file.
You can find your source token by going to [Better Stack Logs](https://logs.betterstack.com/dashboard) -> Sources -> Edit.

Open the command line in the project's directory and enter the following command:

```powershell
dotnet run
```

You should see the following output:

```powershell
All done! Now, you can check Better Stack to see your logs
```

# Setup

This part shows and explains the usage of the `BetterStack.Logs.Serilog` package for .NET as shown in the example application.

## Create logger

In the source code of the project, initialize the static property `Log.Logger` like this:

```csharp
using Serilog;

// Create logger
Log.Logger = new LoggerConfiguration()
    .WriteTo.BetterStack(sourceToken: "SOURCE_TOKEN")
    .CreateLogger();
```

You can also add [other Serilog sinks](https://github.com/serilog/serilog/wiki/Provided-Sinks), for example `.WriteTo.Console()` to write also to console.

You can set the minimum logging level, for example `.MinimumLevel.Debug()` to see DEBUG logs as well.

For more on Serilog configuration, see [their official docs](https://github.com/serilog/serilog/wiki/Configuration-Basics).

# Logging

The `Logger` instance we created in the setup is used to send log messages to Better Stack.
It provides 6 logging methods for the 6 default log levels. The log levels and their method are:

- **VERBOSE** - Trace the code or add detailed debugging info using the `Verbose()` method
- **DEBUG** - Send debug messages using the `Debug()` method
- **INFORMATION** - Send informative messages about the application progress using the `Information()` method
- **WARNING** - Report non-critical issues using the `Warning()` method
- **ERROR** - Send messages about serious problems using the `Error()` method
- **FATAL** - Report fatal errors that caused the application to crash using the `Fatal()` method

## Logging example

To send a log message of select log level, use the corresponding method.
In this example, we will send the **DEBUG** level log and **ERROR** level log.

```csharp
// Send debug messages using the Debug() method
Log.Debug("Debugging is hard, but can be easier with Better Stack!");

// Send message about serious problems using the Error() method
Log.Error("Error occurred! And it's not good.");
```

## Additional configuration

The BetterStack sink will send you logs periodically in batches to optimize network traffic with several retries in case of unexpected HTTP errors.
You can adjust this behavior by setting the `queueLimitBytes`, `batchSize`, and `batchInterval` parameters to your custom values in your config.

```csharp

// Create logger
Log.Logger = new LoggerConfiguration()
    .WriteTo.BetterStack(
        sourceToken: "SOURCE_TOKEN",
        queueLimitBytes: 100 * 1024 * 1024,
        batchSize: 100,
        batchInterval: TimeSpan.FromSeconds(30)
    )
    .CreateLogger();
```

## Structuring the logs

All of the properties that you pass to the log will be stored in a structured form in the `properties` section of the logged event.

```csharp
Log.Info("User {User} - {UserId} just ordered item {Item}", "Josh", 95845, 75423);
```
