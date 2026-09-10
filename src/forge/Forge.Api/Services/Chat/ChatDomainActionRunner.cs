// -----------------------------------------------------------------------
// <copyright file="ChatDomainActionRunner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

public static class ChatActionIntent
{
    private static readonly AsyncLocal<long?> expected = new();
    public static long? ExpectedRevision => expected.Value;
    public static IDisposable Push(long revision)
    { var previous = expected.Value; expected.Value = revision; return new Restore(() => expected.Value = previous); }
    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }
}

public static class ChatDomainRevision
{
    public static async Task<long> ReadAsync(ProductDbContext db, ForgeActionTarget target, CancellationToken ct)
    {
        object? value;
        if (target.WorkstreamId is not null)
        {
            var row = await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(w => w.Id == target.WorkstreamId, ct);
            value = row is null ? null : new { row.Id, row.StorySpawnId, row.Status, row.LastActive, row.StallReason };
        }
        else
        {
            var row = target.SpawnId is not null ? await db.StorySpawns.AsNoTracking().SingleOrDefaultAsync(s => s.Id == target.SpawnId, ct)
                : await db.StorySpawns.AsNoTracking().SingleOrDefaultAsync(s => s.StoryId == target.StoryId, ct);
            value = row is null ? null : new { row.Id, row.Phase, row.UpdatedAt, row.PlanDocPrsJson, target.ArtifactKey };
        }
        return value is null ? 0 : Convert.ToInt64(ChatEventCatalog.Hash(JsonSerializer.Serialize(value))[..15], 16);
    }
}

/// <summary>Records worktree/process intents before effects. Unknown outcomes require repair; they are never replayed.</summary>
public sealed class ChatDomainActionRunner(ProductDbContext db, ControlDbContext control, ForgeActionContext actions,
    ChatRuntimeState runtime, ChatDbBudget budget, TimeProvider clock)
{
    public async Task<T> RunAsync<T>(string action, ForgeActionTarget target, Func<Task<T>> execute,
        Func<string, T> repeat, Func<T, bool> succeeded, CancellationToken ct)
    {
        using var limits = ChatDbBudget.Limit(db);
        var actor = await actions.RequireAsync(action, target, ct);
        await runtime.DomainGate.WaitAsync(ct);
        try
        {
            var productId = await control.Products.Where(p => p.Slug == db.ProductSlug).Select(p => p.Id).SingleAsync(ct);
            var targetId = target.WorkstreamId ?? target.SpawnId ?? target.StoryId!;
            targetId += target.ArtifactKey is null ? "" : ":" + target.ArtifactKey;
            var revision = await ChatDomainRevision.ReadAsync(db, target, ct);
            var expected = ChatActionIntent.ExpectedRevision ?? revision;
            var key = ChatEventCatalog.Hash(JsonSerializer.Serialize(new { action, targetId, expected }));
            var previous = await db.ChatActionReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.ProductId == productId && r.ActionKey == key, ct);
            if (previous is not null) return repeat(previous.State == "completed" ? "already-resolved" : "operation-needs-repair");
            if (revision != expected) return repeat("stale-revision");
            if (await db.ChatActionReceipts.AnyAsync(r => r.ProductId == productId && r.TargetId == targetId && (r.State == "executing" || r.State == "uncertain"), ct))
                return repeat("operation-needs-repair");
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var receipt = new ChatActionReceiptRow { ProductId = productId, ActionKey = key, Action = action, TargetId = targetId,
                ExpectedRevision = expected, ActorId = actor.UserId, State = "executing", Result = "pending", OperationId = Guid.NewGuid().ToString("N"),
                OwnerProcessInstanceId = runtime.ProcessId, CreatedAt = now };
            db.ChatActionReceipts.Add(receipt); await budget.SaveAsync(db, ct);
            T result;
            try { result = await execute(); }
            catch
            {
                receipt.State = "uncertain"; receipt.Result = "operation-outcome-unknown";
                await budget.SaveAsync(db, CancellationToken.None); runtime.Signal();
                throw;
            }
            receipt.State = "completed"; receipt.Result = succeeded(result) ? "accepted" : "rejected";
            receipt.TerminalAt = clock.GetUtcNow().ToUnixTimeMilliseconds(); await budget.SaveAsync(db, ct); runtime.Signal();
            return result;
        }
        finally { runtime.DomainGate.Release(); }
    }
}