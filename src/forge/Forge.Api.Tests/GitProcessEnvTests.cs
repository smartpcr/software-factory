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

    [Fact]
    public void ApplyFailFast_Shards_Private_Home_Per_CloneRoot()
    {
        // Two different clone roots must get DISTINCT private HOMEs so their
        // git subprocesses (and GCM's credential-hint rewrites) never share a
        // .gitconfig — that cross-repo sharing was the exit-128
        // "Permission denied / unknown error reading configuration files" race.
        var a = new ProcessStartInfo("git") { WorkingDirectory = @"C:\forge\download" };
        var b = new ProcessStartInfo("git") { WorkingDirectory = @"C:\forge\asz-infra" };

        GitProcessEnv.ApplyFailFast(a);
        GitProcessEnv.ApplyFailFast(b);

        var homeA = a.Environment["HOME"];
        var homeB = b.Environment["HOME"];
        Assert.False(string.IsNullOrWhiteSpace(homeA));
        Assert.False(string.IsNullOrWhiteSpace(homeB));
        Assert.NotEqual(homeA, homeB);
        Assert.True(File.Exists(Path.Combine(homeA!, ".gitconfig")));
        Assert.True(File.Exists(Path.Combine(homeB!, ".gitconfig")));
    }

    [Fact]
    public void ApplyFailFast_Collapses_Worktrees_Of_Same_Clone_To_One_Home()
    {
        // Every worktree of a clone shares the clone's .git commondir, so they
        // must map to the SAME private HOME (matching SharedRepoGitGate's
        // per-clone-root serialization that keeps them from racing).
        var cloneRoot = new ProcessStartInfo("git") { WorkingDirectory = @"C:\forge\download" };
        var worktree = new ProcessStartInfo("git") { WorkingDirectory = @"C:\forge\download\.worktree\ws-12345" };

        GitProcessEnv.ApplyFailFast(cloneRoot);
        GitProcessEnv.ApplyFailFast(worktree);

        Assert.Equal(cloneRoot.Environment["HOME"], worktree.Environment["HOME"]);
    }
}
