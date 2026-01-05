// -----------------------------------------------------------------------
// <copyright file="IngestOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace EtwProcessor
{
    using CommandLine;

    [Verb("ingest", HelpText = "Ingest parsed CSV files into Kusto/Azure Data Explorer.")]
    public class IngestOptions
    {
        [Option('i', "input", Required = true, HelpText = "Directory containing parsed CSV files (organized by provider).")]
        public required string ParsedDirectory { get; set; }

        [Option('c', "cluster", Required = true, HelpText = "Kusto cluster URI (e.g., https://mycluster.kusto.windows.net).")]
        public required string ClusterUri { get; set; }

        [Option('d', "database", Required = true, HelpText = "Kusto database name.")]
        public required string Database { get; set; }
    }
}