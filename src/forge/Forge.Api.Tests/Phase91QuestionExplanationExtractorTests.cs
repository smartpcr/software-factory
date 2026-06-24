// -----------------------------------------------------------------------
// <copyright file="Phase91QuestionExplanationExtractorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api.Services;
using Xunit;

/// <summary>
/// Phase 91 — <see cref="OpenQuestionsExtractor"/> must parse the
/// operator-facing rationale (explanation / per-choice logic / recommendation)
/// the agent emits alongside each open question into
/// <see cref="OpenQuestionsExtractor.OpenQuestion.ExplanationJson"/>, and stay
/// backward-compatible (null) when the agent omits it.
/// </summary>
public class Phase91QuestionExplanationExtractorTests
{
    [Fact]
    public void Parses_Explanation_ChoiceLogic_And_Recommendation()
    {
        const string gen = """
            ## Iteration Summary
            Did the thing.

            ```json open-questions
            { "openQuestions": [
                { "id": "auth-method", "text": "Which auth flow?", "type": "choice",
                  "choices": ["OAuth", "PAT"],
                  "explanation": "Auth flow decides how Forge proves identity to the API.",
                  "choiceExplanations": { "OAuth": "interactive broker token", "PAT": "headless static token" },
                  "recommendation": "OAuth",
                  "recommendationRationale": "The broker is available and avoids long-lived secrets." }
            ] }
            ```
            """;

        var result = OpenQuestionsExtractor.Extract(gen);

        var q = Assert.Single(result);
        Assert.Equal("auth-method", q.ExternalId);
        Assert.Equal("choice", q.Kind);
        Assert.NotNull(q.ExplanationJson);

        using var doc = JsonDocument.Parse(q.ExplanationJson!);
        var root = doc.RootElement;
        Assert.Equal("Auth flow decides how Forge proves identity to the API.", root.GetProperty("explanation").GetString());
        Assert.Equal("OAuth", root.GetProperty("recommendation").GetString());
        Assert.Equal("The broker is available and avoids long-lived secrets.", root.GetProperty("recommendationRationale").GetString());
        Assert.Equal("interactive broker token", root.GetProperty("choiceExplanations").GetProperty("OAuth").GetString());
        Assert.Equal("headless static token", root.GetProperty("choiceExplanations").GetProperty("PAT").GetString());
        // Agent-authored explanations are marked generated:false.
        Assert.False(root.GetProperty("generated").GetBoolean());
    }

    [Fact]
    public void Parses_ChoiceAnalysis_Pros_And_Cons()
    {
        const string gen = """
            ```json open-questions
            { "openQuestions": [
                { "id": "close-decision", "text": "How to close?", "type": "choice",
                  "choices": ["Accept", "Expand"],
                  "explanation": "Decide how to finish the stage.",
                  "choiceAnalysis": [
                    { "choice": "Accept", "pros": ["ships now", "small diff"], "cons": ["defers residual"] },
                    { "choice": "Expand", "pros": ["compiles fully"], "cons": ["large scope", "slower"] }
                  ],
                  "recommendation": "Accept",
                  "recommendationRationale": "Lowest risk." }
            ] }
            ```
            """;

        var q = Assert.Single(OpenQuestionsExtractor.Extract(gen));
        Assert.NotNull(q.ExplanationJson);
        using var doc = JsonDocument.Parse(q.ExplanationJson!);
        var ca = doc.RootElement.GetProperty("choiceAnalysis");
        Assert.Equal(2, ca.GetArrayLength());
        var first = ca[0];
        Assert.Equal("Accept", first.GetProperty("choice").GetString());
        Assert.Equal(2, first.GetProperty("pros").GetArrayLength());
        Assert.Equal("ships now", first.GetProperty("pros")[0].GetString());
        Assert.Equal("defers residual", first.GetProperty("cons")[0].GetString());
        Assert.Equal("Accept", doc.RootElement.GetProperty("recommendation").GetString());
    }

    [Fact]
    public void OpenQuestion_With_Recommendation_But_No_Choices()
    {
        const string gen = """
            ```json open-questions
            { "openQuestions": [
                { "id": "retry-cap", "text": "Cap retries?", "type": "open",
                  "explanation": "How many times to retry a transient failure before giving up.",
                  "recommendation": "3 attempts with exponential backoff" }
            ] }
            ```
            """;

        var q = Assert.Single(OpenQuestionsExtractor.Extract(gen));
        Assert.Equal("open", q.Kind);
        Assert.NotNull(q.ExplanationJson);
        using var doc = JsonDocument.Parse(q.ExplanationJson!);
        Assert.Equal("3 attempts with exponential backoff", doc.RootElement.GetProperty("recommendation").GetString());
        // No choiceExplanations key for an open question.
        Assert.False(doc.RootElement.TryGetProperty("choiceExplanations", out _));
    }

    [Fact]
    public void No_Explanation_Fields_Yields_Null_ExplanationJson()
    {
        // Back-compat: pre-Phase-91 agents emit only id/text/type/choices.
        const string gen = """
            ```json open-questions
            { "openQuestions": [
                { "id": "q1", "text": "Pick one?", "type": "choice", "choices": ["A", "B"] }
            ] }
            ```
            """;

        var q = Assert.Single(OpenQuestionsExtractor.Extract(gen));
        Assert.Null(q.ExplanationJson);
    }
}
