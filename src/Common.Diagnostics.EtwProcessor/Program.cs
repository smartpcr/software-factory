//-------------------------------------------------------------------------------
// <copyright file="Program.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
//-------------------------------------------------------------------------------

namespace EtwProcessor
{
    using CommandLine;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Serilog;
    using Serilog.Events;

    public class Program
    {
        private static readonly CancellationTokenSource cts = new();
        private static Action? cleanupAction;

        static int Main(string[] args)
        {
            // Configure Serilog with colored console
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                    theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code)
                .CreateLogger();

            // Handle Ctrl+C
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true; // Prevent immediate termination
                Log.Warning("Cancellation requested (Ctrl+C). Cleaning up...");
                cts.Cancel();

                // Execute cleanup action if set
                cleanupAction?.Invoke();
            };

            // Handle process exit
            AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
            {
                cleanupAction?.Invoke();
                Log.CloseAndFlush();
            };

            try
            {
                return Parser.Default.ParseArguments<ScanOptions, ParseOptions, IngestOptions>(args)
                    .MapResult(
                        (ScanOptions opts) => RunScan(opts),
                        (ParseOptions opts) => RunParse(opts),
                        (IngestOptions opts) => RunIngest(opts),
                        errs => 1);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Operation cancelled");
                return 1;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static ServiceProvider BuildServiceProvider()
        {
            var services = new ServiceCollection();

            // Add logging with Serilog
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog(dispose: false);
            });

            // Register services
            services.AddTransient<ZipHelper>();
            services.AddTransient<EtwScanner>();
            services.AddTransient<EtwParser>();
            services.AddTransient<KustoIngestor>();

            return services.BuildServiceProvider();
        }

        private static int RunScan(ScanOptions opts)
        {
            using var serviceProvider = BuildServiceProvider();
            var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

            logger.LogInformation("=== ETW/EVTX Scanner ===");
            logger.LogInformation("Input Directory: {InputDirectory}", opts.InputDirectory);

            if (!Directory.Exists(opts.InputDirectory))
            {
                logger.LogError("Input directory does not exist: {InputDirectory}", opts.InputDirectory);
                return 1;
            }

            var scanner = serviceProvider.GetRequiredService<EtwScanner>();
            scanner.Configure(opts.InputDirectory, cts.Token);

            // Set cleanup action for Ctrl+C
            cleanupAction = () => scanner.CleanupTempDirectory();

            try
            {
                return scanner.Run();
            }
            finally
            {
                cleanupAction = null;
            }
        }

        private static int RunParse(ParseOptions opts)
        {
            using var serviceProvider = BuildServiceProvider();
            var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

            logger.LogInformation("=== ETW/EVTX Parser ===");
            logger.LogInformation("Input Directory: {InputDirectory}", opts.InputDirectory);
            logger.LogInformation("Output Directory: {OutputDirectory}", opts.OutputDirectory);

            if (!Directory.Exists(opts.InputDirectory))
            {
                logger.LogError("Input directory does not exist: {InputDirectory}", opts.InputDirectory);
                return 1;
            }

            var parser = serviceProvider.GetRequiredService<EtwParser>();
            parser.Configure(opts.InputDirectory, opts.OutputDirectory, cts.Token);

            // Set cleanup action for Ctrl+C
            cleanupAction = () => parser.CleanupTempDirectory();

            try
            {
                return parser.Run();
            }
            finally
            {
                cleanupAction = null;
            }
        }

        private static int RunIngest(IngestOptions opts)
        {
            using var serviceProvider = BuildServiceProvider();
            var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

            logger.LogInformation("=== Kusto Ingestion ===");
            logger.LogInformation("Parsed Directory: {ParsedDirectory}", opts.ParsedDirectory);
            logger.LogInformation("Cluster: {ClusterUri}", opts.ClusterUri);
            logger.LogInformation("Database: {Database}", opts.Database);

            if (!Directory.Exists(opts.ParsedDirectory))
            {
                logger.LogError("Parsed directory does not exist: {ParsedDirectory}", opts.ParsedDirectory);
                return 1;
            }

            var ingestor = serviceProvider.GetRequiredService<KustoIngestor>();
            ingestor.Configure(opts.ParsedDirectory, opts.ClusterUri, opts.Database);
            return ingestor.Run();
        }
    }
}