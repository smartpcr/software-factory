// -----------------------------------------------------------------------
// <copyright file="ParseOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace EtwProcessor
{
    using CommandLine;

    [Verb("parse", HelpText = "Parse ETL/EVTX files and export to CSV files organized by provider.")]
    public class ParseOptions
    {
        [Option('i', "input", Required = true, HelpText = "Input directory containing zip/etl/evtx files.")]
        public required string InputDirectory { get; set; }

        [Option('o', "output", Required = true, HelpText = "Output directory for parsed CSV files.")]
        public required string OutputDirectory { get; set; }
    }
}