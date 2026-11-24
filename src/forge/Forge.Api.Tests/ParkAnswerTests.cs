// -----------------------------------------------------------------------
// <copyright file="ParkAnswerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// An answer that parks the work ("pause until the repair merges") must stop the
/// workstream, not just sit in the prompt as text. Observed 2026-11-24 on
/// harness:DSH-M2 stage 2.8: six more iterations each reported "still paused" until
/// the no-progress guard stalled it.
/// </summary>
public class ParkAnswerTests
{
    private const string Pause = "Pause until the repair merges";
    private const string Keep = "Keep iterating";
    private static readonly DateTimeOffset Answered = new(2026, 11, 24, 20, 22, 0, TimeSpan.Zero);

    [Fact]
    public void Extractor_keeps_park_choices_that_are_among_the_question_choices()
    {
        const string text = """
            ```json open-questions
            { "openQuestions": [
              { "id": "wait", "text": "How should this wait?", "type": "choice",
                "choices": ["Pause until the repair merges", "Keep iterating"],
                "parks": ["Pause until the repair merges", "Not a choice"] }
            ] }
            ```
            """;

        var q = Assert.Single(OpenQuestionsExtractor.Extract(text));

        Assert.Equal(new[] { Pause }, QuestionOptions.ParkChoices(q.ExplanationJson));
    }

    [Fact]
    public void A_park_answer_not_yet_applied_is_in_force()
    {
        var row = Row(1, Pause, Answered, parks: true);

        Assert.Same(row, QuestionOptions.FindParkInForce(new[] { row }));
    }

    [Fact]
    public void An_answer_that_is_not_a_park_choice_is_not_in_force()
    {
        Assert.Null(QuestionOptions.FindParkInForce(new[] { Row(1, Keep, Answered, parks: true) }));
        Assert.Null(QuestionOptions.FindParkInForce(new[] { Row(1, Pause, Answered, parks: false) }));
        Assert.Null(QuestionOptions.FindParkInForce(new[] { Row(1, null, null, parks: true) }));
    }

    [Fact]
    public void An_applied_park_stays_applied_when_the_agent_re_asks_the_question()
    {
        // Retry resumes the work; the agent then re-emits the question, and the new row
        // inherits the answer (DeliverableLoop carry-forward) but not the stamp.
        var applied = Row(1, Pause, Answered, parks: true);
        applied.ExplanationJson = QuestionOptions.WithParkApplied(applied.ExplanationJson, Answered.AddMinutes(1));
        var reAsked = Row(2, Pause, Answered, parks: true);

        Assert.Null(QuestionOptions.FindParkInForce(new[] { applied, reAsked }));
    }

    [Fact]
    public void Answering_with_a_park_choice_again_parks_again()
    {
        var row = Row(1, Pause, Answered, parks: true);
        row.ExplanationJson = QuestionOptions.WithParkApplied(row.ExplanationJson, Answered.AddMinutes(1));
        row.AnsweredAt = Answered.AddMinutes(5);

        Assert.Same(row, QuestionOptions.FindParkInForce(new[] { row }));
    }

    [Fact]
    public void Park_choices_declared_on_an_earlier_row_still_count()
    {
        var older = Row(1, null, null, parks: true);
        var latest = Row(2, Pause, Answered, parks: false);

        Assert.Same(latest, QuestionOptions.FindParkInForce(new[] { older, latest }));
    }

    [Fact]
    public void A_regenerated_explanation_keeps_the_park_keys()
    {
        var previous = QuestionOptions.WithParkApplied(Row(1, Pause, Answered, parks: true).ExplanationJson, Answered);
        var regenerated = JsonSerializer.Serialize(new { explanation = "new prose", generated = true });

        var kept = QuestionOptions.KeepParkKeys(previous, regenerated);

        Assert.Equal(new[] { Pause }, QuestionOptions.ParkChoices(kept));
        Assert.Contains("parkAppliedAt", kept, StringComparison.Ordinal);
        Assert.Contains("new prose", kept, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyParkAnswer_parks_once_and_stamps_the_answer()
    {
        const string id = "ws-park-apply";
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot host + migrations */ }
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var row = Row(1, Pause, Answered, parks: true);
            row.Id = 0;
            row.WorkItemId = id;
            db.DeliverableQuestions.Add(row);
            await db.SaveChangesAsync();
        }

        var scopes = f.Services.GetRequiredService<IServiceScopeFactory>();
        var park = await CrewStoryArtifactStrategy.ApplyParkAnswerAsync(scopes, id, default);
        var again = await CrewStoryArtifactStrategy.ApplyParkAnswerAsync(scopes, id, default);

        Assert.NotNull(park);
        Assert.Equal("wait", park!.ExternalId);
        Assert.Null(again);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var stored = await db.DeliverableQuestions.AsNoTracking().SingleAsync(q => q.WorkItemId == id);
            Assert.Contains("parkAppliedAt", stored.ExplanationJson, StringComparison.Ordinal);
        }
    }

    private static DeliverableQuestionRow Row(int iter, string? answer, DateTimeOffset? answeredAt, bool parks) => new()
    {
        Id = iter,
        WorkItemId = "ws-park",
        ExternalId = "wait",
        Iter = iter,
        Text = "How should this wait?",
        Kind = "choice",
        ChoicesJson = JsonSerializer.Serialize(new[] { Pause, Keep }),
        ExplanationJson = parks
            ? JsonSerializer.Serialize(new { recommendation = Pause, parks = new[] { Pause } })
            : JsonSerializer.Serialize(new { recommendation = Pause }),
        Answer = answer,
        AnsweredAt = answeredAt,
    };
}