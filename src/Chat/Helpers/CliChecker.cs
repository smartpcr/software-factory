// -----------------------------------------------------------------------
// <copyright file="CliChecker.cs" company="AzureStack Compute">
// </copyright>
// -----------------------------------------------------------------------

namespace Chat.Helpers;

using System.Diagnostics;
using GitHub.Copilot.SDK;
using Spectre.Console;

/// <summary>
/// Checks if the Copilot CLI is installed and the user is authenticated.
/// Based on: https://github.com/jamesmontemagno/podcast-metadata-generator
/// </summary>
public static class CliChecker
{
    /// <summary>
    /// Result of the Copilot readiness check.
    /// </summary>
    public record CopilotStatus(
        bool IsInstalled,
        bool IsTokenSet,
        bool IsAuthenticated,
        string? ErrorMessage);

    /// <summary>
    /// Verifies Copilot CLI installation and authentication status.
    /// </summary>
    /// <returns>CopilotStatus with detailed information.</returns>
    public static async Task<CopilotStatus> CheckCopilotStatusAsync()
    {
        AnsiConsole.Markup("   Checking for Copilot CLI... ");

        // Check GH_TOKEN environment variable
        var ghToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        var isTokenSet = !string.IsNullOrEmpty(ghToken);

        // Check if copilot CLI is installed
        var isInstalled = await CheckCopilotInstalledAsync();

        if (!isInstalled)
        {
            AnsiConsole.MarkupLine("[red]❌ Not found![/]");
            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine("   Please install Copilot CLI:");
            AnsiConsole.WriteLine("   macOS/Linux: brew install copilot-cli");
            AnsiConsole.WriteLine("   Windows:     winget install GitHub.Copilot");
            AnsiConsole.WriteLine("   npm:         npm install -g @github/copilot");
            AnsiConsole.WriteLine("   Script:      curl -fsSL https://gh.io/copilot-install | bash");

            return new CopilotStatus(
                IsInstalled: false,
                IsTokenSet: isTokenSet,
                IsAuthenticated: false,
                ErrorMessage: "Copilot CLI is not installed.");
        }

        AnsiConsole.MarkupLine("[green]✅ Installed[/]");

        // Check GH_TOKEN
        AnsiConsole.Markup("   Checking GH_TOKEN environment variable... ");
        if (isTokenSet)
        {
            AnsiConsole.MarkupLine("[green]✅ Set[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]○ Not set (will use interactive auth)[/]");
        }

        // Check authentication status
        AnsiConsole.Markup("   Checking authentication... ");
        var authResult = await CheckCopilotAuthAsync();

        if (authResult.isAuthenticated)
        {
            AnsiConsole.MarkupLine("[green]✅ Authenticated[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[red]❌ Not authenticated[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine("   Please authenticate with Copilot:");
            AnsiConsole.WriteLine("   Run: copilot");
            AnsiConsole.WriteLine("   Then type: /login");
            AnsiConsole.WriteLine("   Or set GH_TOKEN environment variable with a token that has 'Copilot Requests' permission.");
        }

        AnsiConsole.WriteLine();

        return new CopilotStatus(
            IsInstalled: true,
            IsTokenSet: isTokenSet,
            IsAuthenticated: authResult.isAuthenticated,
            ErrorMessage: authResult.error);
    }

    /// <summary>
    /// Quick check if Copilot is ready (installed + authenticated or has token).
    /// </summary>
    /// <param name="status">The CopilotStatus to check.</param>
    /// <returns>True if Copilot is ready to use.</returns>
    public static bool IsReady(CopilotStatus status)
    {
        return status.IsInstalled && (status.IsTokenSet || status.IsAuthenticated);
    }

    private static async Task<bool> CheckCopilotInstalledAsync()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "copilot",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(bool isAuthenticated, string? error)> CheckCopilotAuthAsync()
    {
        // If GH_TOKEN is set, assume authenticated
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GH_TOKEN")))
        {
            return (true, null);
        }

        try
        {
            // Use the SDK to check authentication status
            using var client = new CopilotClient();
            await client.StartAsync();

            var authStatus = await client.GetAuthStatusAsync();

            await client.StopAsync();

            if (authStatus.IsAuthenticated)
            {
                return (true, null);
            }
            else
            {
                return (false, authStatus.StatusMessage ?? "Not authenticated. Run 'copilot' and type '/login', or set GH_TOKEN environment variable.");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Failed to check authentication: {ex.Message}");
        }
    }
}
