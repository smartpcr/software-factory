// -----------------------------------------------------------------------
// <copyright file="ChatHelper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace CopilotChat.Helpers;

using System.Diagnostics;
using GitHub.Copilot.SDK;
using Spectre.Console;

/// <summary>
/// Holds the response text and performance metrics from a chat interaction.
/// </summary>
/// <param name="Text">The full response text.</param>
/// <param name="Elapsed">Total wall-clock time for the response.</param>
/// <param name="TimeToFirstToken">Time until the first streaming token arrived.</param>
/// <param name="InputTokens">Number of input tokens consumed.</param>
/// <param name="OutputTokens">Number of output tokens generated.</param>
/// <param name="CacheReadTokens">Number of tokens read from cache.</param>
/// <param name="CacheWriteTokens">Number of tokens written to cache.</param>
public record ChatResponse(
    string Text,
    TimeSpan Elapsed,
    TimeSpan? TimeToFirstToken,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens);

/// <summary>
/// Helper methods for chat interactions with Copilot.
/// </summary>
public static class ChatHelper
{
    /// <summary>
    /// Minimum character length for a message block to be considered "substantial"
    /// (actual analysis vs. tool-call narration like "Let me read...").
    /// </summary>
    private const int SubstantialBlockThreshold = 500;

    /// <summary>
    /// Sends a message and streams the response to the console.
    /// Captures only the final substantial response, discarding intermediate
    /// tool-call narration ("Let me read...", "Now I'll check...").
    /// </summary>
    /// <param name="session">The Copilot session.</param>
    /// <param name="message">The message to send.</param>
    /// <returns>A <see cref="ChatResponse"/> containing the response text and metrics.</returns>
    public static async Task<ChatResponse> SendMessageAndStreamResponse(CopilotSession session, string message)
    {
        var done = new TaskCompletionSource();
        var hasStartedResponse = false;
        var currentBlock = new System.Text.StringBuilder();
        var messageBlocks = new List<string>();
        TimeSpan? timeToFirstToken = null;
        var inputTokens = 0;
        var outputTokens = 0;
        var cacheReadTokens = 0;
        var cacheWriteTokens = 0;
        var stopwatch = Stopwatch.StartNew();

        var subscription = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    if (!hasStartedResponse)
                    {
                        timeToFirstToken ??= stopwatch.Elapsed;
                        AnsiConsole.Markup("\n[green]🥔 Mr. Potato Head: [/]");
                        hasStartedResponse = true;
                    }

                    AnsiConsole.Write(delta.Data.DeltaContent);
                    currentBlock.Append(delta.Data.DeltaContent);
                    break;

                case AssistantMessageEvent msg:
                    if (!hasStartedResponse)
                    {
                        timeToFirstToken ??= stopwatch.Elapsed;
                        // Non-streaming fallback: no deltas were received
                        AnsiConsole.Markup("\n[green]🥔 Mr. Potato Head: [/]");
                        AnsiConsole.Write(msg.Data.Content);
                        currentBlock.Append(msg.Data.Content);
                    }

                    // End of a complete message block — save it and start a new one
                    AnsiConsole.WriteLine();
                    var blockText = currentBlock.ToString().Trim();
                    if (!string.IsNullOrEmpty(blockText))
                    {
                        messageBlocks.Add(blockText);
                    }

                    currentBlock.Clear();
                    hasStartedResponse = false;
                    break;

                case AssistantUsageEvent usage:
                    inputTokens += (int)(usage.Data.InputTokens ?? 0);
                    outputTokens += (int)(usage.Data.OutputTokens ?? 0);
                    cacheReadTokens += (int)(usage.Data.CacheReadTokens ?? 0);
                    cacheWriteTokens += (int)(usage.Data.CacheWriteTokens ?? 0);
                    break;

                case SessionIdleEvent:
                    stopwatch.Stop();
                    // Flush any remaining content
                    var remaining = currentBlock.ToString().Trim();
                    if (!string.IsNullOrEmpty(remaining))
                    {
                        messageBlocks.Add(remaining);
                    }

                    done.TrySetResult();
                    break;

                case SessionErrorEvent err:
                    stopwatch.Stop();
                    AnsiConsole.MarkupLine($"\n[red]❌ Error: {err.Data.Message}[/]");
                    done.TrySetResult();
                    break;
            }
        });

        try
        {
            await session.SendAsync(new MessageOptions { Prompt = message });
            await done.Task;
            AnsiConsole.WriteLine("\n");
        }
        finally
        {
            subscription.Dispose();
        }

        // Extract the actual analysis: use the last substantial block.
        // Intermediate narration ("Let me read...", "Now I'll check...") is short.
        // The final analysis is the large block at the end.
        var responseText = ExtractFinalResponse(messageBlocks);

        return new ChatResponse(
            responseText,
            stopwatch.Elapsed,
            timeToFirstToken,
            inputTokens,
            outputTokens,
            cacheReadTokens,
            cacheWriteTokens);
    }

    /// <summary>
    /// Extracts the final response from message blocks, discarding tool-call narration.
    /// Strategy: if only 1 block, return it. If multiple blocks, return the last block
    /// that exceeds <see cref="SubstantialBlockThreshold"/>. If no block is large enough,
    /// concatenate all blocks (simple conversation without tool calls).
    /// </summary>
    private static string ExtractFinalResponse(List<string> blocks)
    {
        if (blocks.Count == 0)
        {
            return string.Empty;
        }

        if (blocks.Count == 1)
        {
            return blocks[0];
        }

        // Find substantial blocks (actual analysis, not narration)
        var substantialBlocks = blocks.Where(b => b.Length >= SubstantialBlockThreshold).ToList();

        if (substantialBlocks.Count > 0)
        {
            // Return the last substantial block — this is the final analysis.
            // Earlier substantial blocks may be partial analyses between tool calls;
            // the model typically produces the comprehensive output last.
            return substantialBlocks[^1];
        }

        // No single block is large enough — this is likely a simple conversation
        // without tool-call narration. Concatenate all blocks.
        return string.Join("\n\n", blocks);
    }
}
