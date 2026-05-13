// -----------------------------------------------------------------------
// <copyright file="MemoryCompactor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text;
using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Forge.Api.Realtime;

/// <summary>
/// Phase 55 / Phase F (docs/forge/sloppy_guard.md) -- bounds the
/// growth of Claude/SDK conversation history before it crosses the
/// model's context window or runs up the input bill. Triggered after
/// each <see cref="GeneratorDelegate"/> / <see cref="EvaluatorDelegate"/>
/// upsert via <see cref="CompactIfNeededAsync"/>: when the memory
/// row's <see cref="AgentMemoryRow.ApproxInputTokens"/> exceeds the
/// effective threshold, the older turns are folded into one synthetic
/// summary user-turn (produced by a cheap Haiku invocation) and the
/// most-recent <see cref="RecencyKeep"/> turns are kept verbatim.
///
/// <para><b>Effective threshold formula:</b>
/// <c>min(crew.CompactionTokenThreshold ?? 300_000, model.ContextWindow × 0.3)</c>.
/// The 30% clamp leaves headroom for the system prompt, the new user
/// turn, and the model's generated response. Per-crew override exists
/// because long-context-tier crews (1M Opus) can afford a higher
/// threshold than mixed-window pairs.</para>
///
/// <para><b>Only Claude/SDK is compacted.</b> Claude/CLI's
/// <c>--resume</c> stores history on disk under
/// <c>~/.claude/projects/</c>; Forge doesn't own the bytes, so it
/// can't rewrite them. Copilot/SDK history lives inside the SDK's
/// session object — also not Forge-owned. Today only the SDK path
/// persists <see cref="AgentMemoryRow.MessagesJson"/>, which is what
/// this service operates on.</para>
/// </summary>
public sealed class MemoryCompactor
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Phase F default. Operators override per crew via
    /// <see cref="CrewRow.CompactionTokenThreshold"/>. Locked in the
    /// docs/forge/sloppy_guard.md "Locked decisions" table.
    /// </summary>
    public const long DefaultThreshold = 300_000;

    /// <summary>
    /// How many of the most-recent turns to keep verbatim past the
    /// compaction boundary. Three covers (prior user, prior assistant,
    /// current user) — enough for the model to ground the next reply
    /// in the immediate state even when the summary loses detail.
    /// </summary>
    public const int RecencyKeep = 3;

    /// <summary>
    /// Cheap-but-capable summariser. Haiku 4.5 has a 200K context and
    /// ~10× cheaper input than Opus, fits inside the threshold window
    /// for one-shot summarisation.
    /// </summary>
    public const string SummarizerModel = "claude-haiku-4-5-20251001";

    private const string SummarizerSoul =
        "You compact long agent conversations into dense bullet-point summaries. "
        + "Preserve every decision, file edit, dead end, and open question. No prose, "
        + "no commentary, no preamble. Output only the bullet list.";

    private const string SummarizerAgentId = "compactor";

    private readonly AgentMemoryStore store;
    private readonly IAgentRunner runner;
    private readonly IModelRegistry models;
    private readonly TaskChannel taskChannel;
    private readonly ILogger<MemoryCompactor>? logger;

    public MemoryCompactor(
        AgentMemoryStore store,
        IAgentRunner runner,
        IModelRegistry models,
        TaskChannel taskChannel,
        ILogger<MemoryCompactor>? logger = null)
    {
        this.store = store;
        this.runner = runner;
        this.models = models;
        this.taskChannel = taskChannel;
        this.logger = logger;
    }

    /// <summary>
    /// Check the memory row's input-token meter against the effective
    /// threshold and compact when over. Returns <c>true</c> when a
    /// compaction actually ran; <c>false</c> when not needed, when the
    /// row has no SDK history, when there are fewer than
    /// <c>RecencyKeep + 1</c> turns, or when the summariser failed.
    /// Best-effort: any exception is swallowed and logged so the
    /// caller's iter-end path continues.
    /// </summary>
    /// <param name="generatorModelId">Model id of the cell whose
    /// memory we're compacting — used to clamp the effective threshold
    /// to that model's context window. Pass the generator's own model
    /// for the generator memory; same for evaluator.</param>
    /// <param name="crewThresholdOverride"><see cref="CrewRow.CompactionTokenThreshold"/>
    /// when set; otherwise null falls through to <see cref="DefaultThreshold"/>.</param>
    public async Task<bool> CompactIfNeededAsync(
        string workItemId,
        string agentId,
        string role,
        string generatorModelId,
        long? crewThresholdOverride,
        CancellationToken ct)
    {
        AgentMemoryRow? row;
        try
        {
            row = await this.store.GetAsync(workItemId, agentId, role, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogWarning(ex,
                "MemoryCompactor.GetAsync threw for {WorkItem}/{Agent}/{Role}; skipping.",
                workItemId, agentId, role);
            return false;
        }

        if (row is null) return false;
        if (string.IsNullOrEmpty(row.MessagesJson)) return false;

        var effective = ComputeEffectiveThreshold(generatorModelId, crewThresholdOverride);
        if (row.ApproxInputTokens < effective) return false;

        List<ConversationTurn>? turns;
        try
        {
            turns = JsonSerializer.Deserialize<List<ConversationTurn>>(row.MessagesJson, JsonOpts);
        }
        catch (JsonException ex)
        {
            this.logger?.LogWarning(ex,
                "MemoryCompactor: failed to deserialize MessagesJson for {WorkItem}/{Agent}/{Role}; clearing.",
                workItemId, agentId, role);
            return false;
        }
        if (turns is null || turns.Count <= RecencyKeep + 1) return false;

        var olderTurns = turns.Take(turns.Count - RecencyKeep).ToList();
        var recentTurns = turns.Skip(turns.Count - RecencyKeep).ToList();
        var priorTokens = row.ApproxInputTokens;

        var summarizerPrompt = BuildSummarizerPrompt(olderTurns);
        var summarizerContext = new AgentContext(
            CrewId: "compactor",
            Model: SummarizerModel,
            SoulMd: SummarizerSoul,
            WorktreePath: string.Empty,
            AllowedTools: Array.Empty<string>(),
            Backend: AgentBackend.Claude,
            InvokeMode: AgentInvokeMode.Sdk);

        decimal summaryCost = 0m;
        var output = new StringBuilder();
        var summarizerFailed = false;
        try
        {
            var invocation = await this.runner.StartAsync(
                workItemId, SummarizerAgentId, summarizerPrompt, summarizerContext, ct);
            await foreach (var evt in invocation.Events.WithCancellation(ct))
            {
                if (evt.Kind == AgentEventKind.Token && !string.IsNullOrEmpty(evt.Text))
                {
                    output.Append(evt.Text);
                }
                if (evt.CostUsd.HasValue) summaryCost += evt.CostUsd.Value;
                if (evt.Kind == AgentEventKind.Failed)
                {
                    this.logger?.LogWarning(
                        "MemoryCompactor summariser failed for {WorkItem}/{Agent}/{Role}: {Error}",
                        workItemId, agentId, role, evt.Error);
                    summarizerFailed = true;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogWarning(ex,
                "MemoryCompactor summariser threw for {WorkItem}/{Agent}/{Role}; skipping compaction.",
                workItemId, agentId, role);
            return false;
        }

        var summary = output.ToString().Trim();
        if (summarizerFailed || string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        var firstIter = olderTurns[0].Iter;
        var lastIter = olderTurns[^1].Iter;
        var summaryHeader =
            $"## Compacted summary (iter {firstIter}–{lastIter}, {olderTurns.Count} turns folded)\n\n";
        var compactedContent = summaryHeader + summary;

        var newTurns = new List<ConversationTurn>(RecencyKeep + 1)
        {
            new ConversationTurn(
                Role: "user",
                Content: compactedContent,
                Iter: lastIter,
                Tokens: Math.Max(1, compactedContent.Length / 4)),
        };
        newTurns.AddRange(recentTurns);

        try
        {
            await this.store.UpsertWithTurnsAsync(
                workItemId, agentId, role,
                backend: row.Backend, invokeMode: row.InvokeMode,
                sessionId: row.SessionId, turns: newTurns,
                lastIter: row.LastIter, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogWarning(ex,
                "MemoryCompactor.UpsertWithTurnsAsync threw for {WorkItem}/{Agent}/{Role}; row left unchanged.",
                workItemId, agentId, role);
            return false;
        }

        var newTokens = 0L;
        foreach (var t in newTurns) newTokens += t.Tokens;

        try
        {
            await this.taskChannel.EmitAsync(workItemId, "task.memory.compacted", new
            {
                role,
                agentId,
                compactedTurns = olderTurns.Count,
                priorTokens,
                newTokens,
                summaryCostUsd = summaryCost,
                effectiveThreshold = effective,
            }, agentId: agentId, ct: ct);
        }
        catch
        {
            // Telemetry is best-effort; the rewrite already landed.
        }

        return true;
    }

    /// <summary>
    /// Compute the effective compaction trigger. <c>min(crewOverride
    /// ?? 300K, model.ContextWindow × 0.3)</c>. When the model isn't
    /// found in the registry (e.g. a Copilot-vendor id), the crew
    /// override (or default) wins.
    /// </summary>
    public long ComputeEffectiveThreshold(string modelId, long? crewThresholdOverride)
    {
        var baseThreshold = crewThresholdOverride ?? DefaultThreshold;
        var model = this.models.Find(modelId);
        if (model is null || model.ContextWindow <= 0) return baseThreshold;
        var modelCap = (long)(model.ContextWindow * 0.3);
        return Math.Min(baseThreshold, modelCap);
    }

    private static string BuildSummarizerPrompt(IReadOnlyList<ConversationTurn> turns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Compact the following agent conversation history into a dense");
        sb.AppendLine("bullet-point summary. PRESERVE every:");
        sb.AppendLine("  - File path the agent touched (path + one-line what-changed)");
        sb.AppendLine("  - Concrete decision made (chose A over B because …)");
        sb.AppendLine("  - Dead end already tried (so future iters don't re-attempt)");
        sb.AppendLine("  - Open question raised");
        sb.AppendLine("  - Evaluator feedback item the generator should still address");
        sb.AppendLine();
        sb.AppendLine("Drop chitchat, restated rubrics, full file dumps, and verbose");
        sb.AppendLine("reasoning. Output ONLY the bullet list — no preamble, no prose.");
        sb.AppendLine();
        sb.AppendLine("──── CONVERSATION TURNS ────");
        sb.AppendLine();
        foreach (var t in turns)
        {
            sb.AppendLine($"### {t.Role} (iter {t.Iter})");
            sb.AppendLine();
            sb.AppendLine(t.Content);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
