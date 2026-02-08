// -----------------------------------------------------------------------
// <copyright file="CommandLineOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Chat;

using CommandLine;

/// <summary>
/// Command-line options parsed via CommandLineParser.
/// </summary>
public class CommandLineOptions
{
    /// <summary>
    /// Gets or sets the model ID to use.
    /// </summary>
    [Option('m', "model", Required = false, HelpText = "Model ID to use (e.g., gpt-4o). Skips interactive selection.")]
    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets the question number to run from the Questions folder.
    /// </summary>
    [Option('q', "question", Required = false, HelpText = "Question number from the Questions folder. Runs non-interactively and exits.")]
    public int? Question { get; set; }

    /// <summary>
    /// Gets or sets the repository path for context.
    /// </summary>
    [Option('r', "repo", Required = false, HelpText = "Path to a repository for context.")]
    public string? RepoPath { get; set; }

    /// <summary>
    /// Gets or sets the feature name for question prompts that use the {feature} placeholder.
    /// </summary>
    [Option('f', "feature", Required = false, HelpText = "Feature name for feature-specific questions (e.g., 'ETW parsing').")]
    public string? Feature { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to list available models and exit.
    /// </summary>
    [Option('l', "list-models", Required = false, HelpText = "Print all available models and exit.")]
    public bool ListModels { get; set; }
}