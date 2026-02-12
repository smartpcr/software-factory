// -----------------------------------------------------------------------
// <copyright file="ChatSettings.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace CopilotChat;

/// <summary>
/// Configuration settings for the Chat application.
/// Can be loaded from appsettings.json, environment variables, or command line arguments.
/// </summary>
public class ChatSettings
{
    /// <summary>
    /// Gets or sets the model ID to use (e.g., "gpt-4o", "claude-sonnet-4-5-20250929").
    /// When set, skips interactive model selection.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets the question number to ask (from the Questions folder).
    /// When set, runs in non-interactive mode: sends the question, prints the response, and exits.
    /// </summary>
    public int? Question { get; set; }

    /// <summary>
    /// Gets or sets the path to a repository for context.
    /// Used to provide repository context to the chat session.
    /// </summary>
    public string? RepoPath { get; set; }

    /// <summary>
    /// Gets or sets additional parameters for question prompt placeholders.
    /// Keys map to {placeholder} tokens in question files.
    /// </summary>
    public Dictionary<string, string> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets a value indicating whether to list available models and exit.
    /// </summary>
    public bool ListModels { get; set; }

}