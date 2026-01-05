// -----------------------------------------------------------------------
// <copyright file="KustoIngestor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace EtwProcessor
{
    using System.Diagnostics;
    using Kusto.Data;
    using Kusto.Data.Common;
    using Kusto.Data.Net.Client;
    using Kusto.Ingest;
    using Microsoft.Extensions.Logging;

    public class KustoIngestor
    {
        private readonly ILogger<KustoIngestor> logger;
        private string parsedDirectory = string.Empty;
        private string clusterUri = string.Empty;
        private string database = string.Empty;

        public KustoIngestor(ILogger<KustoIngestor> logger)
        {
            this.logger = logger;
        }

        public void Configure(string parsedDirectory, string clusterUri, string database)
        {
            this.parsedDirectory = parsedDirectory;
            this.clusterUri = clusterUri;
            this.database = database;
        }

        public int Run()
        {
            var stopwatch = Stopwatch.StartNew();

            // Find all provider directories
            var providerDirs = Directory.GetDirectories(this.parsedDirectory);
            if (providerDirs.Length == 0)
            {
                this.logger.LogWarning("No provider directories found in parsed directory.");
                return 1;
            }

            this.logger.LogInformation("Found {ProviderCount} provider directories", providerDirs.Length);

            var connectionString = new KustoConnectionStringBuilder(this.clusterUri)
                .WithAadUserPromptAuthentication();

            using var adminClient = KustoClientFactory.CreateCslAdminProvider(connectionString);
            using var ingestClient = KustoIngestFactory.CreateDirectIngestClient(connectionString);

            var totalIngested = 0L;

            foreach (var providerDir in providerDirs)
            {
                var providerName = Path.GetFileName(providerDir);
                var csvFiles = Directory.GetFiles(providerDir, "*.csv");

                if (csvFiles.Length == 0) continue;

                this.logger.LogInformation("Processing provider: {ProviderName} ({FileCount} files)", providerName, csvFiles.Length);

                foreach (var csvFile in csvFiles)
                {
                    var tableName = $"ETL-{Path.GetFileNameWithoutExtension(csvFile)}";

                    try
                    {
                        // Create table from CSV header
                        var schema = InferSchemaFromCsv(csvFile);
                        this.EnsureTable(adminClient, tableName, schema);

                        // Ingest CSV
                        var ingested = this.IngestCsvFile(ingestClient, csvFile, tableName);
                        totalIngested += ingested;
                        this.logger.LogDebug("  {FileName}: {RecordCount:N0} records", Path.GetFileName(csvFile), ingested);
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogError("Error ingesting {FileName}: {ErrorMessage}", Path.GetFileName(csvFile), ex.Message);
                    }
                }
            }

            stopwatch.Stop();
            this.logger.LogInformation("Ingestion completed in {ElapsedSeconds:F2} seconds", stopwatch.Elapsed.TotalSeconds);
            this.logger.LogInformation("Total records ingested: {TotalRecords:N0}", totalIngested);

            return 0;
        }

        private static List<(string Name, string Type)> InferSchemaFromCsv(string csvFile)
        {
            using var reader = new StreamReader(csvFile);
            var headerLine = reader.ReadLine();

            if (string.IsNullOrEmpty(headerLine))
            {
                throw new InvalidOperationException("CSV file is empty or has no header");
            }

            var columns = headerLine.Split(',');
            var schema = new List<(string Name, string Type)>();

            foreach (var column in columns)
            {
                var colName = column.Trim();
                var kustoType = colName switch
                {
                    "TimeStamp" => "datetime",
                    "ProcessID" or "ProcessId" or "EventId" or "Level" or "Opcode" => "int",
                    _ => "string"
                };
                schema.Add((colName, kustoType));
            }

            return schema;
        }

        private void EnsureTable(ICslAdminProvider adminClient, string tableName, List<(string Name, string Type)> schema)
        {
            // Check if table exists
            try
            {
                var showCmd = $".show table ['{tableName}']";
                adminClient.ExecuteControlCommand(this.database, showCmd);
                return; // Table exists
            }
            catch
            {
                // Table doesn't exist, create it
            }

            this.logger.LogDebug("Creating table {TableName}", tableName);

            // Create table
            var columns = string.Join(", ", schema.Select(c => $"{c.Name}: {c.Type}"));
            var createCmd = $".create table ['{tableName}'] ({columns})";
            adminClient.ExecuteControlCommand(this.database, createCmd);

            // Create CSV mapping
            var mappingJson = string.Join(",",
                schema.Select((c, i) => $"{{\"column\":\"{c.Name}\",\"datatype\":\"{c.Type}\",\"Ordinal\":{i}}}"));
            var mappingCmd = $".create-or-alter table ['{tableName}'] ingestion csv mapping 'CsvMapping' '[{mappingJson}]'";
            adminClient.ExecuteControlCommand(this.database, mappingCmd);
        }

        private long IngestCsvFile(IKustoIngestClient ingestClient, string csvFile, string tableName)
        {
            var properties = new KustoIngestionProperties(this.database, tableName)
            {
                Format = DataSourceFormat.csv,
                IngestionMapping = new IngestionMapping
                {
                    IngestionMappingReference = "CsvMapping"
                },
                AdditionalProperties = new Dictionary<string, string>
                {
                    ["ignoreFirstRecord"] = "true"
                }
            };

            using var stream = File.OpenRead(csvFile);
            ingestClient.IngestFromStream(stream, properties);

            // Count lines (excluding header)
            return File.ReadLines(csvFile).Count() - 1;
        }
    }
}
