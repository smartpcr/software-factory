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
/// <para>
/// 2026-05-06 — extended with leniency-pass coverage. The live evaluator on
/// the engineer crew was burning iteration slots on parse fallbacks
/// (unquoted JS-style keys, model-emitted prose without a JSON fence). The
/// new tests lock down each lenient-recovery feature plus the cleanup-
/// ordering traps the rubber-duck pass surfaced — string contents must
/// never be corrupted; comments and braces inside strings must be ignored;
/// the latest plausible candidate wins overall, not the first fenced one.
/// </para>
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
        Assert.False(r.ParseRecovered);
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
    public void Dim_Over_Max_Is_Clamped_And_Marked_Recovered()
    {
        // 2026-05-07 — replaces the prior `Dim_Over_Max_Falls_Back` test.
        // Rejecting the whole shape on a 1-point overshoot destroyed the
        // iteration's signal (WI-HIGH-MEM-CONSUMPTION iter 7 collapsed to
        // 0 because the model emitted `tests: 17` against a 15 cap). The
        // parser now clamps and surfaces the drift via Notes + ParseRecovered.
        // correctness max is 40; 41 must clamp to 40.
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 95, \"dims\": { \"correctness\": 41 } }"));
        Assert.False(r.ParseFailed);
        Assert.True(r.ParseRecovered);
        Assert.Equal(95, r.Score);
        Assert.Equal(40, r.Dims["correctness"]);
        Assert.Contains("correctness=41->40", r.Notes);
    }

    [Fact]
    public void Production_Repro_Score_Preserved_When_Tests_Dim_Overflows()
    {
        // 2026-05-07 — exact repro of the WI-HIGH-MEM-CONSUMPTION iter 7
        // shape from the screenshot. Model emitted score 76 with a `tests`
        // dim of 17 against the 15 cap. Pre-fix this fell through to score 0;
        // post-fix the score is preserved, the dim is clamped to 15, and
        // operators see the drift in the notes prefix.
        var r = EvaluatorScoreParser.Parse(Wrap(
            "{ \"score\": 76, \"dims\": { \"correctness\": 29, \"quality\": 15, \"tests\": 17, \"scope\": 8, \"risk\": 7 }, \"verdict\": \"iterate\", \"notes\": \"needs more coverage\" }"));

        Assert.False(r.ParseFailed);
        Assert.True(r.ParseRecovered);
        Assert.Equal(76, r.Score);
        Assert.Equal("iterate", r.Verdict);
        Assert.Equal(15, r.Dims["tests"]);
        Assert.Equal(29, r.Dims["correctness"]);
        Assert.Contains("tests=17->15", r.Notes);
        Assert.Contains("needs more coverage", r.Notes);
    }

    [Fact]
    public void Negative_Dim_Is_Clamped_To_Zero()
    {
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 50, \"dims\": { \"risk\": -3 } }"));
        Assert.False(r.ParseFailed);
        Assert.True(r.ParseRecovered);
        Assert.Equal(0, r.Dims["risk"]);
        Assert.Contains("risk=-3->0", r.Notes);
    }

    [Fact]
    public void Combined_Lenient_And_Clamp_Recovery_Notes_Carry_Both_Prefixes()
    {
        // Unquoted keys (lenient cleanup) AND tests=17 (semantic clamp) in
        // one input — both repair markers must be present so operators can
        // tell the difference.
        var r = EvaluatorScoreParser.Parse(Wrap("{ score: 80, dims: { tests: 17 }, notes: \"ok\" }"));
        Assert.False(r.ParseFailed);
        Assert.True(r.ParseRecovered);
        Assert.Equal(80, r.Score);
        Assert.Equal(15, r.Dims["tests"]);
        Assert.Contains("[parsed via lenient recovery]", r.Notes);
        Assert.Contains("[parsed via dim clamping: tests=17->15]", r.Notes);
        Assert.Contains("ok", r.Notes);
    }

    [Fact]
    public void Notes_Are_Truncated_At_MaxNotesChars()
    {
        // Cap was bumped from 800 → 4000 in 2026-05-03 (see EvaluatorScoreParser
        // MaxNotesChars). Push past the new cap so we still exercise the
        // truncation path rather than just letting the input through.
        var huge = new string('x', 5000);
        var input = Wrap("{ \"score\": 91, \"notes\": \"" + huge + "\" }");
        var r = EvaluatorScoreParser.Parse(input);
        Assert.False(r.ParseFailed);
        Assert.Equal(4000, r.Notes.Length);
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
    // Score 0 ALWAYS iterates per the explicit comment in DeriveVerdict —
    // 0 indicates a pipeline fault (timeout, tool error) not work-quality
    // rejection. Prior test row asserted "reject" which contradicted the
    // implementation; corrected 2026-05-06 alongside the leniency upgrade.
    [InlineData(0, "iterate")]
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

    // ───────────────────────────────────────────────────────────────────
    // Leniency: cleanup pass recovers JSON5-ish drift
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Lenient_Unquoted_Keys_Parse_After_Cleanup()
    {
        // Real failure mode from the live workstream: model wrote unquoted
        // JS-style keys, surfaced as "'r'/'i' is an invalid start of a
        // property name. LineNumber 0 BytePositionInLine 1-2."
        var r = EvaluatorScoreParser.Parse(Wrap("{ score: 86, \"notes\": \"fine\" }"));
        Assert.False(r.ParseFailed);
        Assert.True(r.ParseRecovered);
        Assert.Equal(86, r.Score);
        Assert.Contains("fine", r.Notes);
    }

    [Fact]
    public void Lenient_Trailing_Comma_Parses()
    {
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 86, \"notes\": \"fine\", }"));
        Assert.False(r.ParseFailed);
        Assert.True(r.ParseRecovered);
        Assert.Equal(86, r.Score);
    }

    [Fact]
    public void Lenient_Line_Comment_Parses()
    {
        // Comment must be INSIDE the object so the candidate selector
        // doesn't simply exclude it (a comment trailing the closing `}`
        // is harmless because the bare-object scan stops at the `}`).
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 86, // 86 because tests pass\n  \"notes\": \"fine\" }"));
        Assert.False(r.ParseFailed);
        Assert.True(r.ParseRecovered);
        Assert.Equal(86, r.Score);
    }

    [Fact]
    public void Lenient_Block_Comment_Inside_Object_Parses()
    {
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 86, /* high */ \"notes\": \"fine\" }"));
        Assert.False(r.ParseFailed);
        Assert.True(r.ParseRecovered);
        Assert.Equal(86, r.Score);
    }

    [Fact]
    public void Lenient_Single_Quotes_Parse()
    {
        var r = EvaluatorScoreParser.Parse(Wrap("{ 'score': 86, 'notes': 'fine' }"));
        Assert.False(r.ParseFailed);
        Assert.True(r.ParseRecovered);
        Assert.Equal(86, r.Score);
        Assert.Contains("fine", r.Notes);
    }

    [Fact]
    public void Lenient_Mixed_Quoted_And_Unquoted_Keys()
    {
        // Model fixed half the keys but missed the rest.
        var r = EvaluatorScoreParser.Parse(Wrap("{ score: 86, \"notes\": \"fine\" }"));
        Assert.False(r.ParseFailed);
        Assert.True(r.ParseRecovered);
        Assert.Equal(86, r.Score);
    }

    [Fact]
    public void Lenient_Recovered_Notes_Carry_Recovery_Prefix()
    {
        // Operators should be able to spot lenient-recovery rows even before
        // the deferred forge.eval.parse_failed counter lands.
        var r = EvaluatorScoreParser.Parse(Wrap("{ score: 86, \"notes\": \"fine\" }"));
        Assert.True(r.ParseRecovered);
        Assert.StartsWith("[parsed via lenient recovery]", r.Notes);
    }

    [Fact]
    public void Strict_Pass_Does_Not_Set_ParseRecovered()
    {
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 91, \"notes\": \"ok\" }"));
        Assert.False(r.ParseFailed);
        Assert.False(r.ParseRecovered);
        Assert.Equal("ok", r.Notes);
    }

    [Fact]
    public void Lenient_Recovery_Producing_Reject_Is_Honored()
    {
        // Critique #5 — explicit policy lock. A recovered low score is real
        // because the model expressed an opinion in a recoverable shape;
        // the verdict math runs as normal. Operators retain the dispute
        // path. Burning an iteration on a fallback-iterate would be worse.
        var r = EvaluatorScoreParser.Parse(Wrap("{ score: 5, \"notes\": \"bad\" }"));
        Assert.False(r.ParseFailed);
        Assert.True(r.ParseRecovered);
        Assert.Equal(5, r.Score);
        Assert.Equal("reject", r.Verdict);
    }

    // ───────────────────────────────────────────────────────────────────
    // Leniency: notes alias mapping (critique #2)
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Notes_Alias_Reasoning_Is_Mapped()
    {
        // Without alias mapping, a recovered { score: 86, reasoning: "..." }
        // would silently produce empty Notes, leaving the next-iteration
        // generator with no actionable feedback.
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 86, \"reasoning\": \"index path missing\" }"));
        Assert.False(r.ParseFailed);
        Assert.Equal(86, r.Score);
        Assert.Contains("index path missing", r.Notes);
    }

    [Fact]
    public void Notes_Alias_Feedback_Is_Mapped()
    {
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 86, \"feedback\": \"add tests\" }"));
        Assert.False(r.ParseFailed);
        Assert.Contains("add tests", r.Notes);
    }

    [Fact]
    public void Notes_Alias_Review_Is_Mapped()
    {
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 86, \"review\": \"good\" }"));
        Assert.False(r.ParseFailed);
        Assert.Contains("good", r.Notes);
    }

    [Fact]
    public void Notes_Wins_Over_Alias_When_Both_Present()
    {
        // Canonical key is authoritative.
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 86, \"notes\": \"primary\", \"reasoning\": \"secondary\" }"));
        Assert.False(r.ParseFailed);
        Assert.Equal("primary", r.Notes);
    }

    // ───────────────────────────────────────────────────────────────────
    // Leniency: latest candidate wins (critique #1)
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Latest_Candidate_Wins_Over_Earlier_Fenced_Example()
    {
        // Earlier fenced "example" + later authoritative bare object —
        // parser must pick the LATER one or it preserves the existing bug.
        var msg = "Here's the schema for reference:\n\n"
                + Wrap("{ \"score\": 0, \"notes\": \"example\" }")
                + "\n\nMy actual review:\n\n"
                + "{ \"score\": 86, \"notes\": \"real\" }";
        var r = EvaluatorScoreParser.Parse(msg);
        Assert.False(r.ParseFailed);
        Assert.Equal(86, r.Score);
        Assert.Contains("real", r.Notes);
    }

    [Fact]
    public void Latest_Candidate_Wins_When_Earlier_Block_Is_Valid_Later_Needs_Cleanup()
    {
        // Worst case: earlier candidate parses strictly, later one needs
        // the lenient pass. The later one is the real answer.
        var msg = Wrap("{ \"score\": 0, \"notes\": \"example\" }")
                + "\n\nReal answer:\n\n"
                + "{ score: 86, \"notes\": \"real\" }";
        var r = EvaluatorScoreParser.Parse(msg);
        Assert.False(r.ParseFailed);
        Assert.True(r.ParseRecovered);
        Assert.Equal(86, r.Score);
        Assert.Contains("real", r.Notes);
    }

    [Fact]
    public void Two_Fenced_Blocks_Latest_Wins()
    {
        var msg = Wrap("{ \"score\": 0, \"notes\": \"example\" }")
                + "\n\n"
                + Wrap("{ \"score\": 86, \"notes\": \"real\" }");
        var r = EvaluatorScoreParser.Parse(msg);
        Assert.False(r.ParseFailed);
        Assert.Equal(86, r.Score);
        Assert.Contains("real", r.Notes);
    }

    [Fact]
    public void Unlabeled_Fence_With_Json_Body_Is_Accepted()
    {
        // Critique #9 — preserve current behavior: unlabeled ``` fences
        // whose body starts with `{` are valid candidates.
        var msg = "Result:\n```\n{ \"score\": 86, \"notes\": \"ok\" }\n```";
        var r = EvaluatorScoreParser.Parse(msg);
        Assert.False(r.ParseFailed);
        Assert.Equal(86, r.Score);
    }

    // ───────────────────────────────────────────────────────────────────
    // Leniency: cleanup must NEVER corrupt string-literal contents
    // (critique #3 + #10 cleanup-ordering traps)
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Cleanup_Does_Not_Corrupt_String_Values_With_Colon_And_Comma()
    {
        // The colon-and-comma inside the string must NOT be interpreted as
        // a key separator. (Naive regex-based unquoted-key repair would
        // rewrite "score:" inside the string, breaking the JSON.)
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 86, \"notes\": \"score: 86, fine\" }"));
        Assert.False(r.ParseFailed);
        Assert.Equal("score: 86, fine", r.Notes);
    }

    [Fact]
    public void Brace_Inside_Double_Quoted_Notes_Does_Not_Confuse_Selector()
    {
        // The `{` inside the string must NOT be treated as the start of a
        // top-level object by the candidate selector.
        var msg = "{ \"score\": 86, \"notes\": \"saw { in code\" }";
        var r = EvaluatorScoreParser.Parse(msg);
        Assert.False(r.ParseFailed);
        Assert.Equal(86, r.Score);
        Assert.Contains("saw { in code", r.Notes);
    }

    [Fact]
    public void Comment_Inside_String_Is_Not_Stripped()
    {
        // Strict parse should pass on the original — no cleanup needed.
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 86, \"notes\": \"see /* this */ context\" }"));
        Assert.False(r.ParseFailed);
        Assert.False(r.ParseRecovered);
        Assert.Contains("/* this */", r.Notes);
    }

    [Fact]
    public void Comment_Containing_Brace_Does_Not_Break_Selection()
    {
        // The `}` inside the comment must NOT close the outer object from
        // the selector's point of view, AND the comment must be stripped
        // for the parse to succeed.
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 86, /* close }} */ \"notes\": \"fine\" }"));
        Assert.False(r.ParseFailed);
        Assert.True(r.ParseRecovered);
        Assert.Equal(86, r.Score);
    }

    [Fact]
    public void Single_Quoted_Containing_Double_Quote_Skipped_Safely()
    {
        // Conservative single→double conversion refuses to mutate runs
        // that contain `"` (would corrupt on rewrap). Result: the strict
        // parse still fails; we fall back rather than silently mangle.
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 86, \"notes\": 'has \"quote\" inside' }"));
        Assert.True(r.ParseFailed);
    }

    [Fact]
    public void Single_Quoted_Containing_Backslash_Escape_Skipped_Safely()
    {
        // Backslash escapes inside single-quoted strings are also a
        // hands-off case — the conversion would have to interpret JS-style
        // escapes, which is beyond what we want to commit to.
        var r = EvaluatorScoreParser.Parse(Wrap("{ \"score\": 86, \"notes\": 'has \\'apostrophe\\' inside' }"));
        Assert.True(r.ParseFailed);
    }

    [Fact]
    public void Repaired_Identifier_Outside_Allowlist_Is_Not_Quoted()
    {
        // Critique #7 — known-key repair only. An unknown identifier
        // outside the allowlist must NOT be quoted, so a malformed
        // `{ randomKey: … }` still falls back rather than silently
        // succeeding with a parse the operator doesn't expect.
        var r = EvaluatorScoreParser.Parse(Wrap("{ randomKey: 5, score: 86 }"));
        Assert.True(r.ParseFailed);
    }
}
