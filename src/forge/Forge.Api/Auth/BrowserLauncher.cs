// -----------------------------------------------------------------------
// <copyright file="BrowserLauncher.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 9.4 — opens a URL in a browser. Edge-first on Windows so OAuth
/// consent feels like a focused popup; falls back to <c>msedge.exe {url}</c>
/// (regular tab) and finally to the system default. Behaviour overridable
/// via <c>Forge:Auth:BrowserPreference</c> (<c>edge</c> | <c>default</c>).
/// Non-Windows hosts always use the system default.
/// </summary>
public interface IBrowserLauncher
{
    /// <summary>Launches the URL. Returns true when a child process was started.</summary>
    bool Launch(string url);
}

public class BrowserLauncher : IBrowserLauncher
{
    private readonly ForgeAuthOptions options;
    private readonly ILogger<BrowserLauncher> logger;

    public BrowserLauncher(IOptions<ForgeAuthOptions> options, ILogger<BrowserLauncher> logger)
    {
        this.options = options.Value;
        this.logger = logger;
    }

    public bool Launch(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        var preferEdge =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            !string.Equals(this.options.BrowserPreference, "default", StringComparison.OrdinalIgnoreCase);

        if (preferEdge)
        {
            // 1) Chromeless app-style popup (preferred).
            if (TryStart("msedge.exe", $"--app={url} --new-window", this.logger)) return true;
            // 2) Regular Edge tab.
            if (TryStart("msedge.exe", url, this.logger)) return true;
            // 3) Fall through to system default.
        }

        // System default — Process.Start with UseShellExecute respects user's
        // browser preference on every supported platform.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "BrowserLauncher fallback failed for {Url}", url);
            return false;
        }
    }

    private static bool TryStart(string fileName, string arguments, ILogger logger)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                CreateNoWindow = true,
            };
            var proc = Process.Start(psi);
            return proc != null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "BrowserLauncher could not start {File}", fileName);
            return false;
        }
    }
}
