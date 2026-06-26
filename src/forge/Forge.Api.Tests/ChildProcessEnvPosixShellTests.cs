// -----------------------------------------------------------------------
// <copyright file="ChildProcessEnvPosixShellTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System;
using Forge.Api.Agents;
using Xunit;

/// <summary>
/// Unit tests for <see cref="ChildProcessEnv.ComputePosixUsrBinToAppend"/> — the
/// pure, filesystem-free core that decides whether to append git's
/// <c>usr\bin</c> to a gate child's PATH so GNU <c>make</c> finds a POSIX
/// <c>sh</c> (rather than dying on <c>cmd.exe</c> at the first <c>mkdir -p</c>).
/// Root cause: code-intelligence 3D-RENDER stage 3.1, where <c>make probe-self</c>
/// exited 2 in the gate but passed for the agent (which ran make via git-bash).
/// </summary>
public class ChildProcessEnvPosixShellTests
{
    private const string GitCmd = @"C:\Program Files\Git\cmd";
    private const string GitUsrBin = @"C:\Program Files\Git\usr\bin";

    // Probe that reports git.exe under …\Git\cmd and sh.exe under …\Git\usr\bin.
    // Trailing slashes are trimmed to model real File.Exists/Path.Combine, which
    // resolve "…\cmd\" and "…\cmd" identically.
    private static bool GitInstalledProbe(string dir, string file)
    {
        var d = dir.TrimEnd('\\', '/');
        return (string.Equals(d, GitCmd, StringComparison.OrdinalIgnoreCase) && file == "git.exe")
            || (string.Equals(d, GitUsrBin, StringComparison.OrdinalIgnoreCase) && file == "sh.exe");
    }

    [Fact]
    public void AppendsGitUsrBin_WhenShMissingButGitOnPath()
    {
        var path = $@"C:\Windows\System32;{GitCmd}";
        var result = ChildProcessEnv.ComputePosixUsrBinToAppend(path, GitInstalledProbe);
        Assert.Equal(GitUsrBin, result);
    }

    [Fact]
    public void NoOp_WhenShAlreadyOnPath()
    {
        // A POSIX sh is already resolvable (e.g. an MSYS2 bin on PATH) → don't
        // touch PATH at all, even though git is also present.
        var path = $@"C:\msys64\usr\bin;{GitCmd}";
        bool Probe(string dir, string file) =>
            (string.Equals(dir, @"C:\msys64\usr\bin", StringComparison.OrdinalIgnoreCase) && file == "sh.exe")
            || GitInstalledProbe(dir, file);

        Assert.Null(ChildProcessEnv.ComputePosixUsrBinToAppend(path, Probe));
    }

    [Fact]
    public void NoOp_WhenUsrBinAlreadyOnPath()
    {
        // usr\bin already present ⇒ sh.exe is found in the first pass ⇒ null
        // (no duplicate append).
        var path = $@"{GitUsrBin};{GitCmd}";
        Assert.Null(ChildProcessEnv.ComputePosixUsrBinToAppend(path, GitInstalledProbe));
    }

    [Fact]
    public void NoOp_WhenNoGitOnPath()
    {
        var path = @"C:\Windows\System32;C:\Windows";
        Assert.Null(ChildProcessEnv.ComputePosixUsrBinToAppend(path, static (_, _) => false));
    }

    [Fact]
    public void NoOp_WhenGitPresentButUsrBinHasNoSh()
    {
        // git.exe found, but no sh under the derived usr\bin → can't confirm a
        // POSIX shell, so leave PATH alone rather than append a bogus dir.
        var path = GitCmd;
        bool Probe(string dir, string file) =>
            string.Equals(dir, GitCmd, StringComparison.OrdinalIgnoreCase) && file == "git.exe";

        Assert.Null(ChildProcessEnv.ComputePosixUsrBinToAppend(path, Probe));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoOp_OnEmptyPath(string? path)
    {
        Assert.Null(ChildProcessEnv.ComputePosixUsrBinToAppend(path, static (_, _) => true));
    }

    [Fact]
    public void DerivesUsrBin_RegardlessOfTrailingSlashOnGitCmd()
    {
        var path = $@"{GitCmd}\;C:\Windows\System32";
        var result = ChildProcessEnv.ComputePosixUsrBinToAppend(path, GitInstalledProbe);
        Assert.Equal(GitUsrBin, result);
    }
}
