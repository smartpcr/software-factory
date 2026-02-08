// -----------------------------------------------------------------------
// <copyright file="ChatHelper.cs" company="AzureStack Compute">
// </copyright>
// -----------------------------------------------------------------------

namespace Chat.Helpers;

using GitHub.Copilot.SDK;
using Spectre.Console;

/// <summary>
/// Helper methods for chat interactions with Copilot.
/// </summary>
public static class ChatHelper
{
    /// <summary>
    /// Sends a message and streams the response to the console.
    /// </summary>
    /// <param name="session">The Copilot session.</param>
    /// <param name="message">The message to send.</param>
    /// <returns>The full response text.</returns>
    public static async Task<string> SendMessageAndStreamResponse(CopilotSession session, string message)
    {
        var done = new TaskCompletionSource();
        var hasStartedResponse = false;
        var responseBuilder = new System.Text.StringBuilder();

        var subscription = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    if (!hasStartedResponse)
                    {
                        AnsiConsole.Markup("\n[green]🥔 Mr. Potato Head: [/]");
                        hasStartedResponse = true;
                    }

                    AnsiConsole.Write(delta.Data.DeltaContent);
                    responseBuilder.Append(delta.Data.DeltaContent);
                    break;

                case AssistantMessageEvent msg:
                    if (!hasStartedResponse)
                    {
                        // Non-streaming fallback
                        AnsiConsole.Markup("\n[green]🥔 Mr. Potato Head: [/]");
                        AnsiConsole.Write(msg.Data.Content);
                        responseBuilder.Append(msg.Data.Content);
                    }

                    break;

                case SessionIdleEvent:
                    done.TrySetResult();
                    break;

                case SessionErrorEvent err:
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

        return responseBuilder.ToString();
    }
}
