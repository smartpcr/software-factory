// -----------------------------------------------------------------------
// <copyright file="TestLoggerFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Core.UnitTests;

using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

/// <summary>
/// Factory for creating Serilog-based loggers for tests.
/// </summary>
public static class TestLoggerFactory
{
    private static ILoggerFactory? loggerFactory;

    /// <summary>
    /// Gets or creates a shared logger factory with console output.
    /// </summary>
    public static ILoggerFactory Instance
    {
        get
        {
            if (loggerFactory == null)
            {
                var serilogLogger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.Console(
                        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                    .Enrich.FromLogContext()
                    .CreateLogger();

                loggerFactory = new SerilogLoggerFactory(serilogLogger);
            }

            return loggerFactory;
        }
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
    /// Creates a custom logger factory with specific configuration.
    /// </summary>
    public static ILoggerFactory CreateFactory(Action<LoggerConfiguration>? configure = null)
    {
        var config = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .Enrich.FromLogContext();

        configure?.Invoke(config);

        return new SerilogLoggerFactory(config.CreateLogger());
    }

    /// <summary>
    /// Creates a logger factory that also writes to a file.
    /// </summary>
    public static ILoggerFactory CreateFactoryWithFile(string logFilePath)
    {
        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                logFilePath,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day)
            .Enrich.FromLogContext()
            .CreateLogger();

        return new SerilogLoggerFactory(serilogLogger);
    }
}
