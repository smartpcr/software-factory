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
using Microsoft.Extensions.Options;

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
/// <c>min(crew.CompactionTokenThreshold ?? 300_000,
/// model.ContextWindow × <see cref="CompactionOptions.ContextWindowRatio"/>)</c>.
/// The ratio (default <c>0.5</c>, bound from
/// <c>Forge:Compaction:ContextWindowRatio</c>) leaves headroom for the
/// system prompt, the new user turn, and the model's generated
/// response. Per-crew override exists because long-context-tier crews
/// (1M Opus) can afford a higher absolute floor than mixed-window
/// pairs. The ratio is operator-tunable so a pool of 1M-context cells
/// can compact later than a pool of 128K cells without touching code.</para>
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
    private readonly double contextWindowRatio;

    public MemoryCompactor(
        AgentMemoryStore store,
        IAgentRunner runner,
        IModelRegistry models,
        TaskChannel taskChannel,
        IOptions<CompactionOptions> options,
        ILogger<MemoryCompactor>? logger = null)
    {
        this.store = store;
        this.runner = runner;
        this.models = models;
        this.taskChannel = taskChannel;
        this.logger = logger;
        var configured = options?.Value?.ContextWindowRatio ?? 0.5;
        // Clamp to (0, 1] so a typo can't disable the cap or invert it.
        if (configured <= 0 || double.IsNaN(configured) || double.IsInfinity(configured))
        {
            configured = 0.5;
        }
        else if (configured > 1)
        {
            configured = 1;
        }

        this.contextWindowRatio = configured;
    }

    /// <summary>
    /// The configured fraction of <c>model.ContextWindow</c> used to
    /// clamp the effective compaction threshold. Mirrors
    /// <see cref="CompactionOptions.ContextWindowRatio"/> after the
    /// constructor's defensive clamp. Exposed so the
    /// <c>/api/workstreams/{id}/memory</c> endpoint can surface the
    /// ratio in its tooltip payload.
    /// </summary>
    public double ContextWindowRatio => this.contextWindowRatio;

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
    /// ?? <see cref="DefaultThreshold"/>, model.ContextWindow ×
    /// <see cref="ContextWindowRatio"/>)</c>. When the model isn't
    /// found in the registry (e.g. a Copilot-vendor id), the crew
    /// override (or default) wins.
    /// </summary>
    public long ComputeEffectiveThreshold(string modelId, long? crewThresholdOverride)
        => this.ComputeThresholdDetail(modelId, crewThresholdOverride).EffectiveThreshold;

    /// <summary>
    /// Compute the effective compaction trigger along with the inputs
    /// that produced it (model context window, configured ratio, crew
    /// override, default floor) so callers — primarily the
    /// <c>/api/workstreams/{id}/memory</c> endpoint — can surface a
    /// precise tooltip ("clamp by 200K × 0.5 = 100K" vs "crew override
    /// 42K" vs "global default 300K") without re-deriving the math on
    /// the client.
    /// </summary>
    public ThresholdDetail ComputeThresholdDetail(string modelId, long? crewThresholdOverride)
    {
        var baseThreshold = crewThresholdOverride ?? DefaultThreshold;
        var model = this.models.Find(modelId);
        long? contextWindow = (model is not null && model.ContextWindow > 0) ? model.ContextWindow : null;
        long? modelCap = contextWindow.HasValue
            ? (long)(contextWindow.Value * this.contextWindowRatio)
            : null;

        long effective;
        ThresholdSource source;
        if (modelCap.HasValue && modelCap.Value < baseThreshold)
        {
            effective = modelCap.Value;
            source = ThresholdSource.ModelWindowClamp;
        }
        else
        {
            effective = baseThreshold;
            source = crewThresholdOverride.HasValue
                ? ThresholdSource.CrewOverride
                : ThresholdSource.GlobalDefault;
        }

        return new ThresholdDetail(
            EffectiveThreshold: effective,
            ContextWindowTokens: contextWindow,
            ContextWindowRatio: this.contextWindowRatio,
            DefaultThreshold: DefaultThreshold,
            CrewOverride: crewThresholdOverride,
            Source: source);
    }

    /// <summary>
    /// Breakdown of how the compactor arrived at the effective
    /// threshold for a given (model, crewOverride) pair.
    /// </summary>
    public sealed record ThresholdDetail(
        long EffectiveThreshold,
        long? ContextWindowTokens,
        double ContextWindowRatio,
        long DefaultThreshold,
        long? CrewOverride,
        ThresholdSource Source);

    /// <summary>
    /// Which input pinned the effective compaction threshold.
    /// </summary>
    public enum ThresholdSource
    {
        /// <summary>No crew override; model-window clamp was looser than the default.</summary>
        GlobalDefault,

        /// <summary>Crew override was set and tighter than the model-window clamp.</summary>
        CrewOverride,

        /// <summary>Model context window × ratio was tighter than the crew/default floor.</summary>
        ModelWindowClamp,
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
