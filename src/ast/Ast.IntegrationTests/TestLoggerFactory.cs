// -----------------------------------------------------------------------
// <copyright file="TestLoggerFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.IntegrationTests;

using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Sinks.SystemConsole.Themes;

/// <summary>
/// Factory for creating Serilog-based loggers for tests with colored console output.
/// </summary>
public static class TestLoggerFactory
{
    private static ILoggerFactory? loggerFactory;
    private static TestContext? testContext;

    /// <summary>
    /// Initializes the logger factory with TestContext support.
    /// Call this in ClassInitialize to enable TestContext logging.
    /// </summary>
    /// <param name="context">The test context from MSTest.</param>
    /// <param name="minimumLevel">The minimum log level. Defaults to Debug.</param>
    public static void Initialize(TestContext context, LogEventLevel minimumLevel = LogEventLevel.Debug)
    {
        testContext = context;
        loggerFactory = null; // Reset to recreate with new settings
        CreateLoggerFactory(minimumLevel);
    }

    /// <summary>
    /// Gets or creates a shared logger factory with colored console output.
    /// </summary>
    public static ILoggerFactory Instance => loggerFactory ?? CreateLoggerFactory(LogEventLevel.Debug);

    private static ILoggerFactory CreateLoggerFactory(LogEventLevel minimumLevel)
    {
        if (loggerFactory != null)
        {
            return loggerFactory;
        }

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                theme: AnsiConsoleTheme.Code,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}    {Message:lj}{NewLine}{Exception}");

        // Also write to TestContext if available
        if (testContext != null)
        {
            loggerConfig.WriteTo.Sink(new TestContextSink(testContext));
        }

        var serilogLogger = loggerConfig.CreateLogger();
        loggerFactory = new SerilogLoggerFactory(serilogLogger);

        return loggerFactory;
    }

    /// <summary>
    /// Creates a logger for the specified type.
    /// </summary>
    public static ILogger<T> CreateLogger<T>() => Instance.CreateLogger<T>();

    /// <summary>
    /// Creates a logger with the specified category name.
    /// </summary>
    public static Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) =>
        Instance.CreateLogger(categoryName);

    /// <summary>
    /// Serilog sink that writes to MSTest TestContext.
    /// </summary>
    private sealed class TestContextSink(TestContext context) : Serilog.Core.ILogEventSink
    {
        private readonly TestContext context = context;

        public void Emit(LogEvent logEvent)
        {
            var message = logEvent.RenderMessage();
            var level = logEvent.Level switch
            {
                LogEventLevel.Verbose => "VRB",
                LogEventLevel.Debug => "DBG",
                LogEventLevel.Information => "INF",
                LogEventLevel.Warning => "WRN",
                LogEventLevel.Error => "ERR",
                LogEventLevel.Fatal => "FTL",
                _ => "???"
            };

            this.context.WriteLine($"[{logEvent.Timestamp:HH:mm:ss} {level}] {message}");

            if (logEvent.Exception != null)
            {
                this.context.WriteLine($"Exception: {logEvent.Exception}");
            }
        }
    }
}