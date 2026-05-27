// -----------------------------------------------------------------------
// <copyright file="AgentMemoryStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Text.Json;
using Forge.Api.Agents;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 55 (docs/forge/sloppy_guard.md) — read/write access to
/// <see cref="AgentMemoryRow"/>. Pure DB; no LLM calls. The runner cells
/// (Claude/CLI, Claude/SDK, Copilot/SDK) read via
/// <see cref="GetAsync"/> before <c>StartAsync</c> and write back via
/// <see cref="UpsertAsync"/> after the invocation completes;
/// <see cref="DeleteForWorkItemAsync"/> is the reaper called on terminal
/// verdicts so a completed workstream doesn't leak rows.
///
/// <para>Scoped so the service shares the caller's <see cref="ProductDbContext"/>
/// and participates in any ambient transaction. The unique index
/// <c>ux_agent_memory_wi_agent_role</c> (Phase 55 migration) makes
/// upsert deterministic — concurrent writers race only on the same
/// tuple, which is impossible under the agent-slot claim model.</para>
/// </summary>
public sealed class AgentMemoryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly ControlDbContext control;
    private readonly ProductDbContext db;
    private readonly ILogger<AgentMemoryStore>? logger;

    public AgentMemoryStore(ControlDbContext control, ProductDbContext db, ILogger<AgentMemoryStore>? logger = null)
    {
        this.control = control;
        this.db = db;
        this.logger = logger;
    }

    /// <summary>
    /// Loads the memory row for a <c>(workItemId, agentId, role)</c>
    /// tuple. Returns <c>null</c> when no prior iteration has written
    /// for this tuple — the caller treats that as "start a fresh
    /// session". Also returns null for any input with a missing piece
    /// (defensive: prevents accidental table scans on empty filters).
    /// </summary>
    public async Task<AgentMemoryRow?> GetAsync(
        string workItemId, string agentId, string role, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId)) return null;
        if (string.IsNullOrEmpty(agentId)) return null;
        if (string.IsNullOrEmpty(role)) return null;

        return await this.db.AgentMemory.AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.WorkItemId == workItemId && m.AgentId == agentId && m.Role == role,
                ct);
    }

    /// <summary>
    /// Materialises a <see cref="AgentMemoryRow"/> as an
    /// <see cref="AgentMemoryHandle"/> the runner can consume. Returns
    /// <c>null</c> when the row is missing OR when both payload fields
    /// are empty (a row that exists but has nothing to replay tells the
    /// runner the same as no row). Deserialisation failures on the
    /// JSON payload log and return null rather than throwing — a
    /// corrupted row should not block the iteration.
    /// </summary>
    public async Task<AgentMemoryHandle?> GetHandleAsync(
        string workItemId, string agentId, string role, CancellationToken ct = default)
    {
        var row = await this.GetAsync(workItemId, agentId, role, ct);
        if (row is null) return null;

        IReadOnlyList<ConversationTurn>? priorTurns = null;
        if (!string.IsNullOrEmpty(row.MessagesJson))
        {
            try
            {
                priorTurns = JsonSerializer.Deserialize<List<ConversationTurn>>(
                    row.MessagesJson, JsonOpts);
            }
            catch (JsonException ex)
            {
                this.logger?.LogWarning(ex,
                    "AgentMemoryStore: MessagesJson on row id={RowId} (wi={WI} agent={Agent} role={Role}) "
                    + "failed to deserialise; treating as fresh session.",
                    row.Id, workItemId, agentId, role);
                return null;
            }
        }

        var hasSession = !string.IsNullOrEmpty(row.SessionId);
        var hasTurns = priorTurns is { Count: > 0 };
        if (!hasSession && !hasTurns) return null;

        return new AgentMemoryHandle(
            SessionId: row.SessionId,
            PriorTurns: priorTurns,
            ResumeSession: hasSession);
    }

    /// <summary>
    /// Ensures a session-backed runner has a stable provider session id
    /// before invocation. Returns <c>ShouldResume=true</c> only when the
    /// row already carried a session id; a newly generated id should be
    /// passed to "create new session" APIs rather than resume APIs.
    /// </summary>
    public async Task<(string SessionId, bool ShouldResume)> EnsureSessionIdAsync(
        string workItemId,
        string agentId,
        string role,
        string backend,
        string invokeMode,
        string? memoryStorePath = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId)) throw new ArgumentException("workItemId required", nameof(workItemId));
        if (string.IsNullOrEmpty(agentId)) throw new ArgumentException("agentId required", nameof(agentId));
        if (string.IsNullOrEmpty(role)) throw new ArgumentException("role required", nameof(role));

        var row = await this.db.AgentMemory
            .FirstOrDefaultAsync(
                m => m.WorkItemId == workItemId && m.AgentId == agentId && m.Role == role,
                ct);
        if (!string.IsNullOrEmpty(row?.SessionId))
        {
            if (!string.IsNullOrEmpty(memoryStorePath) && !Directory.Exists(memoryStorePath))
            {
                var nowMissingStore = DateTimeOffset.UtcNow;
                var replacementSessionId = Guid.NewGuid().ToString("D");
                row.Backend = backend;
                row.InvokeMode = invokeMode;
                row.SessionId = replacementSessionId;
                row.MessagesJson = null;
                row.LastIter = 0;
                row.ApproxInputTokens = 0;
                row.UpdatedAt = nowMissingStore;
                await this.db.SaveChangesAsync(ct);
                return (replacementSessionId, false);
            }

            return (row.SessionId!, true);
        }

        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid().ToString("D");
        if (row is null)
        {
            this.db.AgentMemory.Add(new AgentMemoryRow
            {
                WorkItemId = workItemId,
                AgentId = agentId,
                Role = role,
                Backend = backend,
                InvokeMode = invokeMode,
                SessionId = sessionId,
                MessagesJson = null,
                LastIter = 0,
                ApproxInputTokens = 0,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            row.Backend = backend;
            row.InvokeMode = invokeMode;
            row.SessionId = sessionId;
            row.UpdatedAt = now;
        }

        await this.db.SaveChangesAsync(ct);
        return (sessionId, false);
    }

    /// <summary>
    /// Upsert by <c>(WorkItemId, AgentId, Role)</c>. New row is inserted
    /// when no existing row matches; otherwise the payload columns are
    /// overwritten in place and <c>UpdatedAt</c> bumped.
    /// <c>CreatedAt</c> is preserved on update so the row retains its
    /// original timestamp for telemetry.
    ///
    /// <para>The <c>sessionId</c> and <c>messagesJson</c> arguments are
    /// independently nullable — passing null on either clears that
    /// payload (the agent might switch backends mid-workstream, though
    /// that's not a supported runtime path today).</para>
    /// </summary>
    public async Task UpsertAsync(
        string workItemId,
        string agentId,
        string role,
        string backend,
        string invokeMode,
        string? sessionId,
        string? messagesJson,
        int lastIter,
        long approxInputTokens,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId)) throw new ArgumentException("workItemId required", nameof(workItemId));
        if (string.IsNullOrEmpty(agentId)) throw new ArgumentException("agentId required", nameof(agentId));
        if (string.IsNullOrEmpty(role)) throw new ArgumentException("role required", nameof(role));

        var now = DateTimeOffset.UtcNow;
        var existing = await this.db.AgentMemory
            .FirstOrDefaultAsync(
                m => m.WorkItemId == workItemId && m.AgentId == agentId && m.Role == role,
                ct);

        if (existing is null)
        {
            this.db.AgentMemory.Add(new AgentMemoryRow
            {
                WorkItemId = workItemId,
                AgentId = agentId,
                Role = role,
                Backend = backend,
                InvokeMode = invokeMode,
                SessionId = sessionId,
                MessagesJson = messagesJson,
                LastIter = lastIter,
                ApproxInputTokens = approxInputTokens,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.Backend = backend;
            existing.InvokeMode = invokeMode;
            existing.SessionId = sessionId;
            existing.MessagesJson = messagesJson;
            existing.LastIter = lastIter;
            existing.ApproxInputTokens = approxInputTokens;
            existing.UpdatedAt = now;
        }

        await this.db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Convenience overload: serialises the supplied turn list to JSON
    /// and sums <see cref="ConversationTurn.Tokens"/> for
    /// <c>ApproxInputTokens</c>. Use this from runners that maintain a
    /// turn list (the Claude SDK path); CLI / Copilot SDK paths call
    /// <see cref="UpsertAsync"/> directly with <c>messagesJson: null</c>.
    /// </summary>
    public Task UpsertWithTurnsAsync(
        string workItemId,
        string agentId,
        string role,
        string backend,
        string invokeMode,
        string? sessionId,
        IReadOnlyList<ConversationTurn> turns,
        int lastIter,
        CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(turns, JsonOpts);
        var approxTokens = 0L;
        foreach (var t in turns) approxTokens += t.Tokens;
        return this.UpsertAsync(
            workItemId, agentId, role, backend, invokeMode,
            sessionId, json, lastIter, approxTokens, ct);
    }

    /// <summary>
    /// Deletes every memory row for a work item. Called from terminal
    /// verdict paths (pass / reject / terminal-failed) so a completed
    /// workstream doesn't leak rows; idempotent — re-deletes no-op.
    /// </summary>
    public async Task<int> DeleteForWorkItemAsync(string workItemId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workItemId)) return 0;
        return await this.db.AgentMemory
            .Where(m => m.WorkItemId == workItemId)
            .ExecuteDeleteAsync(ct);
    }
}
