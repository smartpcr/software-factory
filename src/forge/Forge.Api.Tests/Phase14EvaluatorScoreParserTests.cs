// -----------------------------------------------------------------------
// <copyright file="Phase14EvaluatorScoreParserTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services;
using Xunit;

/// <summary>
/// Phase 14.3 — pure-function tests for <see cref="EvaluatorScoreParser"/>.
/// Keep these as a fast unit suite (no DI / no DB).
/// </summary>
public class Phase14EvaluatorScoreParserTests
{
    private const string Fence = "```";

    private static string Wrap(string body) => Fence + "json\n" + body + "\n" + Fence;

    [Fact]
    public void HappyPath_Parses_Score_And_Derives_Pass()
    {
        var r = EvaluatorScoreParser.Parse(
            "Some chatter from the model.\n\n" +
            Wrap("{ \"score\": 92, \"dims\": { \"correctness\": 38, \"quality\": 23, \"tests\": 14, \"scope\": 9, \"risk\": 8 }, \"verdict\": \"pass\", \"notes\": \"Looks good.\" }"));
        Assert.False(r.ParseFailed);
        Assert.Equal(92, r.Score);
        Assert.Equal("pass", r.Verdict);
        Assert.Equal("pass", r.ModelVerdict);
        Assert.False(r.VerdictMismatch);
        Assert.Equal(38, r.Dims["correctness"]);
        Assert.Equal("Looks good.", r.Notes);
    }

    [Fact]
    public void Score_70_Is_Iterate_Boundary()
    {
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 70, \"verdict\": \"iterate\" }"));
        Assert.False(r.ParseFailed);
        Assert.Equal("iterate", r.Verdict);
    }

    [Fact]
    public void Score_69_Is_Reject()
    {
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 69, \"verdict\": \"reject\" }"));
        Assert.False(r.ParseFailed);
        Assert.Equal("reject", r.Verdict);
    }

    [Fact]
    public void Derived_Verdict_Overrides_Model_Verdict_On_Mismatch()
    {
        // Model claims "pass" but score 80 → derived "iterate". The math wins.
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 80, \"verdict\": \"pass\" }"));
        Assert.False(r.ParseFailed);
        Assert.Equal("iterate", r.Verdict);
        Assert.Equal("pass", r.ModelVerdict);
        Assert.True(r.VerdictMismatch);
    }

    [Fact]
    public void Empty_Input_Falls_Back_To_Iterate_With_Score_Zero()
    {
        var r = EvaluatorScoreParser.Parse(string.Empty);
        Assert.True(r.ParseFailed);
        Assert.Equal(0, r.Score);
        Assert.Equal("iterate", r.Verdict);
    }

    [Fact]
    public void Missing_Json_Block_Falls_Back()
    {
        var r = EvaluatorScoreParser.Parse("Just some prose, no JSON anywhere here.");
        Assert.True(r.ParseFailed);
        Assert.Equal("iterate", r.Verdict);
    }

    [Fact]
    public void Malformed_Json_Falls_Back()
    {
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 90, \"dims\": { broken"));
        Assert.True(r.ParseFailed);
    }

    [Fact]
    public void Score_Out_Of_Range_Falls_Back()
    {
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 150 }"));
        Assert.True(r.ParseFailed);
    }

    [Fact]
    public void Dim_Over_Max_Falls_Back()
    {
        // correctness max is 40; 41 must fail.
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 95, \"dims\": { \"correctness\": 41 } }"));
        Assert.True(r.ParseFailed);
    }

    [Fact]
    public void Notes_Are_Truncated_To_800_Chars()
    {
        var huge = new string('x', 1000);
        var input = Wrap("{ \"score\": 91, \"notes\": \"" + huge + "\" }");
        var r = EvaluatorScoreParser.Parse(input);
        Assert.False(r.ParseFailed);
        Assert.Equal(800, r.Notes.Length);
    }

    [Fact]
    public void Bare_Json_Without_Fence_Still_Parses()
    {
        var r = EvaluatorScoreParser.Parse("{ \"score\": 91, \"verdict\": \"pass\" }");
        Assert.False(r.ParseFailed);
        Assert.Equal(91, r.Score);
        Assert.Equal("pass", r.Verdict);
    }

    [Theory]
    [InlineData(0, "reject")]
    [InlineData(50, "reject")]
    [InlineData(69, "reject")]
    [InlineData(70, "iterate")]
    [InlineData(89, "iterate")]
    [InlineData(90, "pass")]
    [InlineData(100, "pass")]
    public void DeriveVerdict_Matches_Spec_Boundaries(int score, string expected)
    {
        Assert.Equal(expected, EvaluatorScoreParser.DeriveVerdict(score));
    }
}
