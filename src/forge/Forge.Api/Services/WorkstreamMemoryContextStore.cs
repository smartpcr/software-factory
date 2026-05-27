// -----------------------------------------------------------------------
// <copyright file="WorkstreamMemoryContextStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Writes concise, iteration-forward context into the worktree-local Copilot
/// memory store. This is intentionally a summary/pointer file, not a raw
/// transcript dump.
/// </summary>
public sealed class WorkstreamMemoryContextStore
{
    public const string FileName = "workstream-context.md";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ContextEventKinds = new(StringComparer.Ordinal)
    {
        "task.operator-note",
        "task.build-gate.failed",
        "task.test-gate.failed",
        "task.gate.recovery-attempted",
        "task.gate.recovery-exhausted",
        "task.merge-failed",
        "task.review-fix-skipped",
        "task.sloppy",
        "task.sloppy-reassigned",
        "task.sloppy-terminal-failure",
        "task.delivery-failed",
    };

    private readonly ProductDbContext db;
    private readonly ILogger<WorkstreamMemoryContextStore>? logger;

    public WorkstreamMemoryContextStore(ProductDbContext db, ILogger<WorkstreamMemoryContextStore>? logger = null)
    {
        this.db = db;
        this.logger = logger;
    }

    public static string? MemoryDirectoryForWorktree(string? worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath)) return null;
        return Path.Combine(worktreePath, ".forge", "memory");
    }

    public async Task<string> WriteAsync(
        string workItemId,
        string worktreePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workItemId)) throw new ArgumentException("workItemId required", nameof(workItemId));
        if (string.IsNullOrWhiteSpace(worktreePath)) throw new ArgumentException("worktreePath required", nameof(worktreePath));

        var memoryDir = MemoryDirectoryForWorktree(worktreePath)
            ?? throw new ArgumentException("worktreePath required", nameof(worktreePath));
        Directory.CreateDirectory(memoryDir);

        var content = await this.BuildAsync(workItemId, worktreePath, ct);
        var path = Path.Combine(memoryDir, FileName);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
        return path;
    }

    private async Task<string> BuildAsync(string workItemId, string worktreePath, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Forge workstream memory");
        sb.AppendLine();
        sb.AppendLine("Concise context required to move the next iteration forward. Do not treat this as a transcript; raw prompts, full logs, and full file contents are intentionally omitted.");
        sb.AppendLine();
        sb.AppendLine($"- Work item: `{workItemId}`");
        sb.AppendLine($"- Generated UTC: {DateTimeOffset.UtcNow:O}");

        var wi = await this.db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        var ws = await this.db.Workstreams.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (wi is not null || ws is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Current workstream");
            if (wi is not null)
            {
                sb.AppendLine($"- Work item title: {OneLine(wi.Title, 240)}");
                sb.AppendLine($"- State/execution: `{wi.State}` / `{wi.ExecutionStatus}`; pair attempt {wi.PairAttemptCount}; trial {wi.TrialCount}; points {wi.Points}");
                if (!string.IsNullOrWhiteSpace(wi.PlanAnchor)) sb.AppendLine($"- Work item plan anchor: `{wi.PlanAnchor}`");
                if (!string.IsNullOrWhiteSpace(wi.FailureReason)) sb.AppendLine($"- Work item failure reason: {OneLine(wi.FailureReason, 500)}");
            }

            if (ws is not null)
            {
                sb.AppendLine($"- Workstream: {OneLine(ws.Name, 240)}");
                sb.AppendLine($"- Status/category: `{ws.Status}` / `{ws.Category}`");
                sb.AppendLine($"- Branch/base: `{ws.Branch}` -> `{ws.Base}`");
                if (!string.IsNullOrWhiteSpace(ws.PlanAnchor)) sb.AppendLine($"- Workstream plan anchor: `{ws.PlanAnchor}`");
                if (!string.IsNullOrWhiteSpace(ws.Summary)) sb.AppendLine($"- Summary: {TrimBlock(ws.Summary, 800)}");
                if (!string.IsNullOrWhiteSpace(ws.StallReason)) sb.AppendLine($"- Stall reason: {TrimBlock(ws.StallReason, 800)}");
                await this.AppendStoryAndDependencyContextAsync(sb, ws, ct);
                await this.AppendReviewContextAsync(sb, ws, ct);
            }
        }

        await this.AppendDeliverablesAsync(sb, workItemId, ct);
        await this.AppendQuestionsAsync(sb, workItemId, ct);
        await this.AppendContextEventsAsync(sb, workItemId, ct);
        await this.AppendPromptPointersAsync(sb, workItemId, ct);
        await this.AppendPriorNotesAsync(sb, worktreePath, ct);

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private async Task AppendStoryAndDependencyContextAsync(StringBuilder sb, WorkstreamRow ws, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(ws.StorySpawnId))
        {
            var spawn = await this.db.StorySpawns.AsNoTracking().FirstOrDefaultAsync(s => s.Id == ws.StorySpawnId, ct);
            if (spawn is not null)
            {
                sb.AppendLine();
                sb.AppendLine("## Story and plan context");
                sb.AppendLine($"- Story: `{spawn.StoryId}` — {OneLine(spawn.StoryTitle, 240)}");
                sb.AppendLine($"- Spawn phase/base: `{spawn.Phase}` / `{spawn.BaseBranch}`");
                if (!string.IsNullOrWhiteSpace(spawn.LastError)) sb.AppendLine($"- Last orchestrator error: {TrimBlock(spawn.LastError, 800)}");
                if (!string.IsNullOrWhiteSpace(spawn.DecisionPinsJson)) sb.AppendLine($"- Decision pins JSON pointer: story_spawns.DecisionPinsJson ({spawn.DecisionPinsJson.Length} chars)");
            }
        }

        var deps = ParseStringList(ws.DepsJson);
        if (deps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Dependency context");
            var names = deps.Take(12).ToList();
            foreach (var name in names)
            {
                var dep = await this.db.Workstreams.AsNoTracking()
                    .FirstOrDefaultAsync(w => w.StorySpawnId == ws.StorySpawnId && w.Name == name, ct);
                sb.Append("- ");
                sb.Append(OneLine(name, 180));
                if (dep is not null)
                {
                    sb.Append($" — status `{dep.Status}`");
                    if (!string.IsNullOrWhiteSpace(dep.Branch)) sb.Append($", branch `{dep.Branch}`");
                }
                sb.AppendLine();
            }
            if (deps.Count > names.Count) sb.AppendLine($"- ... {deps.Count - names.Count} additional dependencies omitted.");
        }
    }

    private async Task AppendDeliverablesAsync(StringBuilder sb, string workItemId, CancellationToken ct)
    {
        var deliverables = await this.db.Deliverables.AsNoTracking()
            .Where(d => d.WorkItemId == workItemId)
            .OrderByDescending(d => d.Id)
            .Take(5)
            .ToListAsync(ct);
        if (deliverables.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("## Iteration summaries and evaluator feedback");
        foreach (var d in deliverables.OrderBy(d => d.Id))
        {
            sb.AppendLine($"### Iteration {d.Iter} — verdict `{d.Verdict}`, score {d.Score}, pair {d.PairAttempt ?? 1}");
            if (!string.IsNullOrWhiteSpace(d.CommitSha))
            {
                var shortSha = d.CommitSha!.Length > 12 ? d.CommitSha[..12] : d.CommitSha;
                sb.AppendLine($"- Commit: `{shortSha}` {OneLine(d.CommitMessage, 240)}");
            }
            if (d.FilesChanged is not null)
            {
                sb.AppendLine($"- Files changed: {d.FilesChanged} (added {d.FilesAdded ?? 0}, modified {d.FilesModified ?? 0}, deleted {d.FilesDeleted ?? 0})");
            }
            if (!string.IsNullOrWhiteSpace(d.FailureKind) || !string.IsNullOrWhiteSpace(d.FailureReason))
            {
                sb.AppendLine($"- Failure: `{d.FailureKind ?? "unknown"}` — {OneLine(d.FailureReason, 500)}");
            }
            if (!string.IsNullOrWhiteSpace(d.GeneratorSummary))
            {
                sb.AppendLine("- Generator summary:");
                sb.AppendLine(Indent(TrimBlock(d.GeneratorSummary, 1200)));
            }
            if (!string.IsNullOrWhiteSpace(d.Notes))
            {
                sb.AppendLine("- Evaluator feedback:");
                sb.AppendLine(Indent(TrimBlock(d.Notes, 1600)));
            }
        }
    }

    private async Task AppendQuestionsAsync(StringBuilder sb, string workItemId, CancellationToken ct)
    {
        var questions = await this.db.DeliverableQuestions.AsNoTracking()
            .Where(q => q.WorkItemId == workItemId)
            .OrderByDescending(q => q.Id)
            .Take(10)
            .ToListAsync(ct);
        if (questions.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("## Open questions and operator answers");
        foreach (var q in questions.OrderBy(q => q.Id))
        {
            sb.AppendLine($"- Q: {OneLine(q.Text, 500)}");
            sb.AppendLine($"  A: {(string.IsNullOrWhiteSpace(q.Answer) ? "UNANSWERED" : OneLine(q.Answer, 500))}");
        }
    }

    private async Task AppendContextEventsAsync(StringBuilder sb, string workItemId, CancellationToken ct)
    {
        var events = await this.db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == workItemId && ContextEventKinds.Contains(e.Kind))
            .OrderByDescending(e => e.Id)
            .Take(12)
            .ToListAsync(ct);
        if (events.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("## Gate failures, operator notes, and recovery events");
        foreach (var e in events.OrderBy(e => e.Id))
        {
            sb.AppendLine($"- `{e.Kind}` at {e.At:O}: {SummarizePayload(e.PayloadJson)}");
        }
    }

    private async Task AppendReviewContextAsync(StringBuilder sb, WorkstreamRow ws, CancellationToken ct)
    {
        var prIds = ParseStringList(ws.PrsJson);
        if (prIds.Count == 0) return;

        var comments = await this.db.PrComments.AsNoTracking()
            .Where(c => prIds.Contains(c.PrId))
            .OrderByDescending(c => c.Id)
            .Take(10)
            .ToListAsync(ct);
        if (comments.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("## Review comments and fix decisions");
        foreach (var c in comments.OrderBy(c => c.Id))
        {
            var loc = string.IsNullOrWhiteSpace(c.FilePath) ? string.Empty : $" `{c.FilePath}:{c.Line ?? 1}`";
            sb.AppendLine($"- PR `{c.PrId}`{loc} state `{c.State}` attention={c.RequiresAttention}: {OneLine(c.Body, 500)}");
        }
    }

    private async Task AppendPromptPointersAsync(StringBuilder sb, string workItemId, CancellationToken ct)
    {
        var prompts = await this.db.IterationPrompts.AsNoTracking()
            .Where(p => p.WorkItemId == workItemId)
            .OrderByDescending(p => p.Id)
            .Take(6)
            .ToListAsync(ct);
        if (prompts.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("## Prompt audit pointers");
        foreach (var p in prompts.OrderBy(p => p.Id))
        {
            sb.AppendLine($"- Iter {p.Iter} {p.Role} attempt {p.Attempt}: iteration_prompt id {p.Id}, {p.UserPrompt.Length} chars, model `{p.Model}`.");
        }
    }

    private async Task AppendPriorNotesAsync(StringBuilder sb, string worktreePath, CancellationToken ct)
    {
        try
        {
            var notes = await CrewStoryArtifactStrategy.ReadPriorIterNotesAsync(worktreePath, ct);
            if (string.IsNullOrWhiteSpace(notes)) return;

            sb.AppendLine();
            sb.AppendLine("## Agent working notes");
            sb.AppendLine(TrimBlock(notes, 3000));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger?.LogDebug(ex, "Failed to append prior iteration notes for memory context.");
        }
    }

    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOpts)?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string SummarizePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return OneLine(json, 700);
            var fields = new[] { "reason", "error", "message", "stage", "gateStage", "command", "exitCode", "iter", "attempt", "prId" };
            var parts = new List<string>();
            foreach (var f in fields)
            {
                if (!doc.RootElement.TryGetProperty(f, out var v)) continue;
                parts.Add($"{f}={OneLine(v.ToString(), 180)}");
            }
            return parts.Count == 0 ? OneLine(json, 700) : string.Join("; ", parts);
        }
        catch
        {
            return OneLine(json, 700);
        }
    }

    private static string Indent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines.Select(l => "  " + l));
    }

    private static string TrimBlock(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var normal = text.Trim().Replace("\r\n", "\n");
        return normal.Length <= maxChars ? normal : normal[..maxChars] + "\n...(truncated)";
    }

    private static string OneLine(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var normal = text.Trim().Replace("\r", " ").Replace("\n", " ");
        return normal.Length <= maxChars ? normal : normal[..maxChars] + "...";
    }
}
