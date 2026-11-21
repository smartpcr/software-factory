// -----------------------------------------------------------------------
// <copyright file="QuestionOptionsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Auth;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using OpenQuestion = Forge.Api.Services.OpenQuestionsExtractor.OpenQuestion;

/// <summary>Operator rule: an agent question is never shown without options to pick from.</summary>
public sealed class QuestionOptionsTests
{
    private static readonly IReadOnlyDictionary<string, DeliverableQuestionRow> NoneStored =
        new Dictionary<string, DeliverableQuestionRow>(StringComparer.OrdinalIgnoreCase);

    private sealed class FakeSynthesizer(Func<OpenQuestion, QuestionOptionsResult?> draft) : IQuestionOptionsSynthesizer
    {
        public int Calls;

        public Task<QuestionOptionsResult?> SynthesizeAsync(OpenQuestion question, CancellationToken ct)
        {
            Interlocked.Increment(ref this.Calls);
            return Task.FromResult(draft(question));
        }
    }

    private static string Recommendation(string? explanationJson) =>
        JsonDocument.Parse(explanationJson!).RootElement.GetProperty("recommendation").GetString()!;

    [Fact]
    public async Task Agent_Choices_Are_Kept_Without_A_Model_Call()
    {
        var synth = new FakeSynthesizer(_ => throw new InvalidOperationException("must not be called"));
        var q = new OpenQuestion("auth", "Which auth flow?", "choice", ["OAuth", "API key"]);
        var result = Assert.Single(await QuestionOptions.EnsureAsync([q], NoneStored, synth, default));
        Assert.Same(q, result);
        Assert.Equal(0, synth.Calls);
    }

    [Fact]
    public async Task Open_Question_Gets_Drafted_Options()
    {
        var synth = new FakeSynthesizer(_ => new QuestionOptionsResult(["The operator runs it now", "Defer to a follow-up PR"],
            JsonSerializer.Serialize(new { recommendation = "The operator runs it now" })));
        var q = new OpenQuestion("closure-owner", "Who runs the manual probe?", "open", null);
        var result = Assert.Single(await QuestionOptions.EnsureAsync([q], NoneStored, synth, default));
        Assert.Equal("choice", result.Kind);
        Assert.Equal(["The operator runs it now", "Defer to a follow-up PR"], result.Choices);
        Assert.Equal(1, synth.Calls);
    }

    [Fact]
    public async Task Re_Emitted_Question_Reuses_Stored_Options()
    {
        var synth = new FakeSynthesizer(_ => throw new InvalidOperationException("must not be called"));
        var stored = new Dictionary<string, DeliverableQuestionRow>(StringComparer.OrdinalIgnoreCase)
        {
            ["closure-owner"] = new() { ExternalId = "closure-owner", Kind = "choice", ChoicesJson = "[\"Me\",\"Later\"]", ExplanationJson = "{\"recommendation\":\"Me\"}" },
        };
        var result = Assert.Single(await QuestionOptions.EnsureAsync(
            [new OpenQuestion("Closure-Owner", "Who runs the manual probe?", "open", null)], stored, synth, default));
        Assert.Equal(["Me", "Later"], result.Choices);
        Assert.Equal("Me", Recommendation(result.ExplanationJson));
        Assert.Equal(0, synth.Calls);
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("no-draft")]
    [InlineData("no-synthesizer")]
    public async Task Without_A_Usable_Draft_Falls_Back_To_Recommendation_Plus_Proceed_Or_Defer(string scenario)
    {
        IQuestionOptionsSynthesizer? synth = scenario switch
        {
            "throws" => new FakeSynthesizer(_ => throw new HttpRequestException("model down")),
            "no-draft" => new FakeSynthesizer(_ => null),
            _ => null,
        };
        var q = new OpenQuestion("owner", "Who records the PR review?", "open", null,
            JsonSerializer.Serialize(new { explanation = "ctx", recommendation = "The operator records it", generated = false }));
        var result = Assert.Single(await QuestionOptions.EnsureAsync([q], NoneStored, synth, default));
        Assert.Equal("choice", result.Kind);
        Assert.Equal(["The operator records it", QuestionOptions.BestJudgmentChoice, QuestionOptions.DeferChoice], result.Choices);
        // The recommendation still names an option verbatim, so the UI and chat star it.
        Assert.Equal("The operator records it", Recommendation(result.ExplanationJson));
        Assert.Equal("ctx", JsonDocument.Parse(result.ExplanationJson!).RootElement.GetProperty("explanation").GetString());
    }

    [Fact]
    public async Task Single_Option_Is_Not_Enough()
    {
        var result = Assert.Single(await QuestionOptions.EnsureAsync(
            [new OpenQuestion("q", "Ship it?", "choice", ["Yes"])], NoneStored, null, default));
        Assert.Equal(["Yes", QuestionOptions.BestJudgmentChoice, QuestionOptions.DeferChoice], result.Choices);
        Assert.Equal("Yes", Recommendation(result.ExplanationJson));
    }

    [Fact]
    public async Task Markdown_Questions_Come_Out_With_Options()
    {
        var parsed = OpenQuestionsExtractor.Extract("## Open Questions\n1. Who runs the real-key manual check?\n");
        var result = Assert.Single(await QuestionOptions.EnsureAsync(parsed, NoneStored, null, default));
        Assert.True(QuestionOptions.HasOptions(result.Kind, result.Choices));
    }

    [Fact]
    public void Draft_Keeps_Agent_Explanation_And_Only_Recommends_A_Real_Option()
    {
        var q = new OpenQuestion("q", "Who?", "open", null, JsonSerializer.Serialize(new { explanation = "agent ctx", recommendation = "someone", generated = false }));
        var draft = QuestionOptions.ParseDraft("""
            Here you go: {"choices":["Operator","Agent","Operator"],"explanation":"model ctx",
            "choiceAnalysis":[{"choice":"Operator","pros":["fast"],"cons":[]},{"choice":"Invented","pros":[],"cons":[]}],
            "recommendation":"Agent","recommendationRationale":"cheaper"}
            """, q);
        Assert.NotNull(draft);
        Assert.Equal(["Operator", "Agent"], draft.Choices);
        var e = JsonDocument.Parse(draft.ExplanationJson).RootElement;
        Assert.Equal("agent ctx", e.GetProperty("explanation").GetString());
        Assert.Equal("Agent", e.GetProperty("recommendation").GetString());
        Assert.Equal("cheaper", e.GetProperty("recommendationRationale").GetString());
        Assert.False(e.GetProperty("generated").GetBoolean());
        Assert.Equal("Operator", Assert.Single(e.GetProperty("choiceAnalysis").EnumerateArray()).GetProperty("choice").GetString());
    }

    [Theory]
    [InlineData("""{"choices":["Only one"]}""")]
    [InlineData("""{"explanation":"no choices"}""")]
    [InlineData("not json at all")]
    public void Draft_Without_Two_Options_Is_Rejected(string modelText) =>
        Assert.Null(QuestionOptions.ParseDraft(modelText, new OpenQuestion("q", "Who?", "open", null)));

    [Fact]
    public async Task Questions_Endpoint_Backfills_Unanswered_Open_Questions_Once_And_Leaves_Answered_Ones()
    {
        var synth = new FakeSynthesizer(_ => new QuestionOptionsResult(["The operator runs it now", "Defer to a follow-up PR"],
            JsonSerializer.Serialize(new { recommendation = "The operator runs it now", generated = true })));
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddScoped<IQuestionOptionsSynthesizer>(_ => synth)));
        await CapabilityParityTests.SeedTargetsAsync(app);
        await app.EnrollAsync("po", productId: "p");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.DeliverableQuestions.Add(new DeliverableQuestionRow
            {
                WorkItemId = "ws-p", ExternalId = "closure-owner", Iter = 8, Text = "Who runs the manual probe?", Kind = "open", CreatedAt = DateTimeOffset.UtcNow,
            });
            db.DeliverableQuestions.Add(new DeliverableQuestionRow
            {
                WorkItemId = "ws-p", ExternalId = "history", Iter = 2, Text = "Old question?", Kind = "open",
                Answer = "done", AnsweredAt = DateTimeOffset.UtcNow, AnsweredBy = "po", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        using var client = await app.PairedClientAsync("po");
        client.DefaultRequestHeaders.Add(ProductScope.HeaderName, "p");

        for (var load = 0; load < 2; load++)
        {
            using var response = await client.GetAsync("/api/work-items/ws-p/questions");
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()
                .ToDictionary(i => i.GetProperty("externalId").GetString()!);
            var open = items["closure-owner"];
            Assert.Equal("choice", open.GetProperty("kind").GetString());
            Assert.Equal(["The operator runs it now", "Defer to a follow-up PR"], open.GetProperty("choices").EnumerateArray().Select(c => c.GetString()));
            Assert.Equal("open", items["history"].GetProperty("kind").GetString());
        }
        Assert.Equal(1, synth.Calls);
        using var verify = app.Services.CreateScope();
        var row = await verify.ServiceProvider.GetRequiredService<ProductDbContext>().DeliverableQuestions.SingleAsync(q => q.ExternalId == "closure-owner");
        Assert.Equal("choice", row.Kind);
    }
}