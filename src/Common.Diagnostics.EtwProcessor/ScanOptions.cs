// -----------------------------------------------------------------------
// <copyright file="ScanOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace EtwProcessor
{
    using CommandLine;

    [Verb("scan", HelpText = "Scan input directory and report providers, time range, and event counts.")]
    public class ScanOptions
    {
        [Option('i', "input", Required = true, HelpText = "Input directory containing zip/etl/evtx files.")]
        public required string InputDirectory { get; set; }
    }
}