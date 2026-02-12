// -----------------------------------------------------------------------
// <copyright file="CommandLineOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace CopilotChat;

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
    /// Gets or sets additional parameters for question prompt placeholders.
    /// Each value should be in key=value format (e.g., -p feature="ETW parsing" -p component=Parser).
    /// </summary>
    [Option('p', "param", Required = false, Separator = ' ', HelpText = "Question parameters as key=value. Repeatable. Resolves {key} placeholders in prompts.")]
    public IEnumerable<string>? Params { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to list available models and exit.
    /// </summary>
    [Option('l', "list-models", Required = false, HelpText = "Print all available models and exit.")]
    public bool ListModels { get; set; }

}