// -----------------------------------------------------------------------
// <copyright file="CopilotRuntimeResolutionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Agents;
using Xunit;

/// <summary>
/// The shared Copilot server must spawn the runtime bundled with the SDK
/// (protocol-matched) rather than a self-updating system copilot.exe, unless
/// an operator configured an explicit, existing path.
/// </summary>
public sealed class CopilotRuntimeResolutionTests
{
    [Fact]
    public void Bundled_Runtime_Wins_Over_System_Installs_And_A_Missing_Override()
    {
        var dir = Path.Combine(Path.GetTempPath(), "forge-copilot-resolve-" + Guid.NewGuid().ToString("N"));
        var bundled = Path.Combine(dir, "runtimes", "win-x64", "native", "copilot.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
        File.WriteAllText(bundled, string.Empty);
        try
        {
            Assert.Equal(bundled, CopilotServerManager.ResolveCopilotCli(null, dir));
            Assert.Equal(bundled, CopilotServerManager.ResolveCopilotCli(Path.Combine(dir, "missing", "copilot.exe"), dir));

            var explicitPath = Path.Combine(dir, "pinned-copilot.exe");
            File.WriteAllText(explicitPath, string.Empty);
            Assert.Equal(explicitPath, CopilotServerManager.ResolveCopilotCli(explicitPath, dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_Forge_Build_Output_Carries_The_Bundled_Runtime()
    {
        // The SDK package's build targets place it next to the app; if a
        // build opts out (CopilotSkipCliDownload) Forge silently falls back
        // to system installs, so guard it here.
        var bundled = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "copilot.exe");
        Assert.True(File.Exists(bundled), $"Missing bundled Copilot runtime at {bundled}");
        Assert.Equal(bundled, CopilotServerManager.ResolveCopilotCli(null, AppContext.BaseDirectory));
    }
}