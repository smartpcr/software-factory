// -----------------------------------------------------------------------
// <copyright file="QuestionOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text;
using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using OpenQuestion = Forge.Api.Services.OpenQuestionsExtractor.OpenQuestion;

/// <summary>Options and rationale produced for a question an agent asked without options.</summary>
public sealed record QuestionOptionsResult(IReadOnlyList<string> Choices, string ExplanationJson);

/// <summary>Drafts concrete answer options for an option-less agent question.</summary>
public interface IQuestionOptionsSynthesizer
{
    /// <summary>Returns options, or null when no model could produce usable ones.</summary>
    Task<QuestionOptionsResult?> SynthesizeAsync(OpenQuestion question, CancellationToken ct);
}

/// <summary>
/// Operator rule: an agent question always comes with options, and the operator answers by
/// picking one; typed answers are refused (<see cref="OpenQuestionService"/>). Agents are
/// prompted to emit choices; this is the server-side guarantee for the ones that do not.
/// The fixed fallback options always include a proceed and a defer path so a question can
/// never be left unanswerable.
/// </summary>
public static class QuestionOptions
{
    public const int MinChoices = 2;
    public const int MaxChoices = 5;
    private const int MaxChoiceLength = 200;

    public const string BestJudgmentChoice = "Proceed with your best judgment and record the assumption";
    public const string DeferChoice = "Defer: leave this out of the current scope";

    public static bool HasOptions(string? kind, IReadOnlyList<string>? choices) =>
        string.Equals(kind, "choice", StringComparison.OrdinalIgnoreCase) && choices is { Count: >= MinChoices };

    public static IReadOnlyList<string> ParseChoices(string? choicesJson)
    {
        if (string.IsNullOrEmpty(choicesJson)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<string[]>(choicesJson) ?? Array.Empty<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Give every question at least <see cref="MinChoices"/> options. In order: keep the
    /// agent's own options; reuse options already stored for the same question id (agents
    /// re-emit the same question every iteration, so this avoids a model call per iteration);
    /// ask <paramref name="synthesizer"/>; otherwise fall back to fixed options built around
    /// the agent's recommendation.
    /// </summary>
    public static async Task<IReadOnlyList<OpenQuestion>> EnsureAsync(
        IReadOnlyList<OpenQuestion> questions,
        IReadOnlyDictionary<string, DeliverableQuestionRow> storedById,
        IQuestionOptionsSynthesizer? synthesizer,
        CancellationToken ct)
    {
        var tasks = questions.Select(async q =>
        {
            if (HasOptions(q.Kind, q.Choices)) return q;
            if (storedById.TryGetValue(q.ExternalId, out var stored) && ParseChoices(stored.ChoicesJson) is { Count: >= MinChoices } reused)
                return q with { Kind = "choice", Choices = reused, ExplanationJson = q.ExplanationJson ?? stored.ExplanationJson };
            QuestionOptionsResult? drafted = null;
            if (synthesizer is not null)
            {
                try { drafted = await synthesizer.SynthesizeAsync(q, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* A model failure must never leave the question option-less. */ }
            }
            drafted ??= Fallback(q);
            return q with { Kind = "choice", Choices = drafted.Choices, ExplanationJson = drafted.ExplanationJson };
        });
        return await Task.WhenAll(tasks);
    }

    /// <summary>Fixed options: the agent's recommendation (when it gave one), then proceed or defer.</summary>
    public static QuestionOptionsResult Fallback(OpenQuestion question)
    {
        var explanation = ReadExplanation(question.ExplanationJson);
        var recommendation = explanation.TryGetValue("recommendation", out var r) ? r as string : null;
        var choices = new List<string>();
        foreach (var seed in question.Choices ?? Array.Empty<string>()) AddChoice(choices, seed);
        AddChoice(choices, recommendation);
        AddChoice(choices, BestJudgmentChoice);
        AddChoice(choices, DeferChoice);
        // The wizard and chat buttons star the option equal to the recommendation, verbatim.
        if (recommendation is null || !choices.Contains(recommendation)) explanation["recommendation"] = choices[0];
        if (!explanation.ContainsKey("generated")) explanation["generated"] = true;
        return new QuestionOptionsResult(choices.Take(MaxChoices).ToList(), JsonSerializer.Serialize(explanation));
    }

    /// <summary>
    /// Parse a model reply of the shape <c>{ choices: [], explanation, choiceAnalysis, recommendation,
    /// recommendationRationale }</c>. Agent-authored explanation fields win over drafted ones; the
    /// recommendation is kept only when it names one of the options verbatim.
    /// </summary>
    public static QuestionOptionsResult? ParseDraft(string? modelText, OpenQuestion question)
    {
        var json = modelText is null ? null : ExtractJsonObject(modelText);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            var choices = new List<string>();
            foreach (var c in arr.EnumerateArray())
                if (c.ValueKind == JsonValueKind.String) AddChoice(choices, c.GetString());
            if (choices.Count < MinChoices) return null;
            choices = choices.Take(MaxChoices).ToList();

            var explanation = ReadExplanation(question.ExplanationJson);
            var agentAuthored = explanation.Count > 0;
            if (!explanation.ContainsKey("explanation") && ReadString(root, "explanation") is { } text) explanation["explanation"] = text;
            if (root.TryGetProperty("choiceAnalysis", out var ca) && ca.ValueKind == JsonValueKind.Array)
            {
                var analysis = new List<object>();
                foreach (var entry in ca.EnumerateArray())
                {
                    if (ReadString(entry, "choice") is not { } choice || !choices.Contains(choice)) continue;
                    analysis.Add(new Dictionary<string, object?> { ["choice"] = choice, ["pros"] = ReadStrings(entry, "pros"), ["cons"] = ReadStrings(entry, "cons") });
                }
                if (analysis.Count > 0) explanation["choiceAnalysis"] = analysis;
            }
            var drafted = ReadString(root, "recommendation");
            var current = explanation.TryGetValue("recommendation", out var r) ? r as string : null;
            if (current is null || !choices.Contains(current))
            {
                explanation["recommendation"] = drafted is not null && choices.Contains(drafted) ? drafted : choices[0];
                if (ReadString(root, "recommendationRationale") is { } why) explanation["recommendationRationale"] = why;
            }
            if (!agentAuthored) explanation["generated"] = true;
            return new QuestionOptionsResult(choices, JsonSerializer.Serialize(explanation));
        }
        catch (JsonException) { return null; }
    }

    public static string BuildPrompt(OpenQuestion question)
    {
        var explanation = ReadExplanation(question.ExplanationJson);
        var sb = new StringBuilder();
        sb.AppendLine("An automated engineering agent asked a human operator the question below without offering answer options.");
        sb.AppendLine("Draft the options the operator should pick from.");
        sb.AppendLine();
        sb.AppendLine($"QUESTION: {question.Text}");
        if (explanation.TryGetValue("explanation", out var e) && e is string context) sb.AppendLine($"AGENT CONTEXT: {context}");
        if (explanation.TryGetValue("recommendation", out var r) && r is string rec) sb.AppendLine($"AGENT RECOMMENDATION: {rec}");
        foreach (var seed in question.Choices ?? Array.Empty<string>()) sb.AppendLine($"EXISTING OPTION: {seed}");
        sb.AppendLine();
        sb.AppendLine($"Give {MinChoices}-4 concrete, mutually exclusive options that each fully answer the question.");
        sb.AppendLine("For a \"who\" or \"which value\" question, name concrete candidates (for example \"The operator runs it now\", \"Defer it to a follow-up\").");
        sb.AppendLine("If the agent gave a recommendation, include it as one option. The operator can ONLY pick one of these options (typed answers are not accepted), so cover every realistic answer; never add an \"Other\" option.");
        sb.AppendLine("Return ONLY one minified JSON object, no prose, no code fences:");
        sb.AppendLine("{\"choices\":[\"...\"],\"explanation\":\"2-4 plain sentences defining any terms\",\"choiceAnalysis\":[{\"choice\":\"<option VERBATIM>\",\"pros\":[\"short\"],\"cons\":[\"short\"]}],\"recommendation\":\"<one option VERBATIM>\",\"recommendationRationale\":\"1-3 sentences\"}");
        return sb.ToString();
    }

    /// <summary>Extract the first balanced JSON object from text that may wrap it in fences or prose.</summary>
    public static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inStr = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inStr)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') inStr = true;
            else if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return text[start..(i + 1)];
        }
        return null;
    }

    private static void AddChoice(List<string> choices, string? value)
    {
        var choice = value?.Trim();
        if (string.IsNullOrEmpty(choice) || choice.Length > MaxChoiceLength || choices.Contains(choice, StringComparer.OrdinalIgnoreCase)) return;
        choices.Add(choice);
    }

    private static Dictionary<string, object?> ReadExplanation(string? explanationJson)
    {
        if (string.IsNullOrEmpty(explanationJson)) return new(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(explanationJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new(StringComparer.Ordinal);
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
                result[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.Clone();
            return result;
        }
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static List<string> ReadStrings(JsonElement obj, string name)
    {
        var result = new List<string>();
        if (obj.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var v in arr.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())) result.Add(v.GetString()!.Trim());
        return result;
    }
}

/// <summary>
/// One bounded, tool-less model call that drafts options. Uses the first crew whose model is
/// registered and is not the deterministic local stub, as the Explain endpoint does.
/// </summary>
public sealed class QuestionOptionsSynthesizer(ControlDbContext control, IAgentRunner runner, IModelRegistry models) : IQuestionOptionsSynthesizer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
    private readonly SemaphoreSlim resolveGate = new(1, 1);
    private (string CrewId, string ModelId)? resolved;

    public async Task<QuestionOptionsResult?> SynthesizeAsync(OpenQuestion question, CancellationToken ct)
    {
        var (crewId, modelId) = await this.ResolveModelAsync(ct);
        if (modelId == "local-stub") return null;
        var context = new AgentContext(CrewId: crewId, Model: modelId, SoulMd: string.Empty, WorktreePath: string.Empty,
            AllowedTools: Array.Empty<string>(), MaxTaskBudgetUsd: null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        var invocation = await runner.StartAsync($"question-options:{question.ExternalId}", "question-options",
            QuestionOptions.BuildPrompt(question), context, cts.Token);
        var text = new StringBuilder();
        await foreach (var ev in invocation.Events.WithCancellation(cts.Token))
        {
            if (ev.Kind == AgentEventKind.Token && ev.Text is { Length: > 0 } t) text.Append(t);
            else if (ev.Kind is AgentEventKind.Finished or AgentEventKind.Failed) break;
        }
        return QuestionOptions.ParseDraft(text.ToString(), question);
    }

    private async Task<(string CrewId, string ModelId)> ResolveModelAsync(CancellationToken ct)
    {
        await this.resolveGate.WaitAsync(ct);
        try
        {
            if (this.resolved is { } cached) return cached;
            var crews = await control.Crews.AsNoTracking()
                .Where(c => c.Model != null && c.Model != "" && c.Model != "local-stub")
                .Select(c => new { c.Id, c.Model })
                .ToListAsync(ct);
            var pick = crews.FirstOrDefault(c => models.Find(c.Model!) is not null);
            this.resolved = pick is null ? ("question-options", "local-stub") : (pick.Id, pick.Model!);
            return this.resolved.Value;
        }
        finally { this.resolveGate.Release(); }
    }
}