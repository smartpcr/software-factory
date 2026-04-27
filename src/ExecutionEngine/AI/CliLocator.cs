// -----------------------------------------------------------------------
// <copyright file="CliLocator.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.AI;

using System.Diagnostics;

/// <summary>
/// Resolves the full path to CLI executables (copilot, claude) that may not
/// be directly executable from child processes due to .ps1 shims.
/// </summary>
public static class CliLocator
{
    private static string? cachedCopilotPath;
    private static string? cachedClaudePath;

    /// <summary>
    /// Resolves the copilot CLI executable path.
    /// </summary>
    public static string Copilot => cachedCopilotPath ??= Resolve("copilot");

    /// <summary>
    /// Resolves the claude CLI executable path.
    /// </summary>
    public static string Claude => cachedClaudePath ??= Resolve("claude");

    private static string Resolve(string name)
    {
        // Try npm global native binary first (bypasses .cmd/.ps1 shim issues)
        var npmNativePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm", "node_modules", $"@github/{name}", "node_modules",
            $"@github/{name}-win32-x64", $"{name}.exe");
        if (File.Exists(npmNativePath))
        {
            return npmNativePath;
        }

        // Try well-known npm global .cmd path
        var npmPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            $"{name}.cmd");
        if (File.Exists(npmPath))
        {
            return npmPath;
        }

        // Try where.exe to find it
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = name,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                // Pick the first .exe or .cmd, skip .ps1
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var path = line.Trim();
                    if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }
                }
            }
        }
        catch
        {
            // where.exe not available
        }

        // Fallback to bare name (hope PATH works)
        return name;
    }
}