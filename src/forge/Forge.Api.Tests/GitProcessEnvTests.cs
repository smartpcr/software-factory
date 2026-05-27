// -----------------------------------------------------------------------
// <copyright file="GitProcessEnvTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Diagnostics;
using Forge.Api.Services.Git;
using Xunit;

/// <summary>
/// Regression coverage for Forge git subprocess isolation. Git for Windows
/// 2.31.0.vfs.0.1 ignores GIT_CONFIG_GLOBAL, so HOME must point at the private
/// Forge copy to keep git from touching the operator's shared ~/.gitconfig.
/// </summary>
public class GitProcessEnvTests
{
    [Fact]
    public void ApplyFailFast_Isolates_Global_Config_With_Private_Home()
    {
        var psi = new ProcessStartInfo("git");

        GitProcessEnv.ApplyFailFast(psi);

        Assert.True(psi.Environment.TryGetValue("HOME", out var home));
        Assert.False(string.IsNullOrWhiteSpace(home));
        Assert.True(
            home!.Contains(Path.Combine("Forge", "git-home"), StringComparison.OrdinalIgnoreCase),
            $"HOME should point at Forge's private git home, but was '{home}'.");
        Assert.True(File.Exists(Path.Combine(home!, ".gitconfig")));
        Assert.Equal(Path.Combine(home!, ".gitconfig"), psi.Environment["GIT_CONFIG_GLOBAL"]);
    }
}
