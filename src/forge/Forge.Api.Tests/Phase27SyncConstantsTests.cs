// -----------------------------------------------------------------------
// <copyright file="Phase27SyncConstantsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using FluentAssertions;
using Forge.Api.Services;
using Xunit;

/// <summary>
/// forge_implementation.md Phase 27 — locks the build-sync constants in
/// <see cref="SyncConstants"/> against the values declared in the spec.
/// Mirrors the Phase 14.14 / Phase 23 spec-consistency-test pattern: when
/// the spec changes, this test is the canary that catches the drift.
/// </summary>
public class Phase27SyncConstantsTests
{
    [Fact]
    public void BuildSync_constants_match_spec_Q27_1()
    {
        // Phase 34 bumped Q27.1's 20-min cadence to 2 hr to reduce ADO/GitHub load.
        SyncConstants.BuildSyncIntervalSeconds.Should().Be(7200);
        SyncConstants.BuildSyncMaxParallel.Should().Be(8);
    }

    [Fact]
    public void BuildLogTailBytes_is_256KB_Q27_4()
    {
        SyncConstants.BuildLogTailBytes.Should().Be(262144);
    }

    [Fact]
    public void Artifact_caps_match_spec_Q27_5()
    {
        SyncConstants.BuildArtifactSizeCapMb.Should().Be(256);
        SyncConstants.BuildArtifactStorageCapGb.Should().Be(10);
        SyncConstants.BuildArtifactRetentionDays.Should().Be(30);
    }

    [Fact]
    public void Failure_patterns_contain_spec_Q27_6_set()
    {
        SyncConstants.BuildFailurePatterns.Should().NotBeEmpty();
        SyncConstants.BuildFailurePatterns.Should().Contain(p => p.Contains("error CS"));      // .NET
        SyncConstants.BuildFailurePatterns.Should().Contain(p => p.Contains("FAILED"));         // cmake/make
        SyncConstants.BuildFailurePatterns.Should().Contain(p => p.Contains("assert"));         // test framework
        SyncConstants.BuildFailurePatterns.Should().Contain(p => p.Contains("npm ERR! code"));  // npm
        SyncConstants.BuildFailurePatterns.Should().Contain(p => p.Contains(@"Error:?\s+"));    // generic stack-trace
    }

    [Fact]
    public void BuildFix_iteration_cap_is_5_Q27_8()
    {
        SyncConstants.BuildFixMaxIterations.Should().Be(5);
    }
}
