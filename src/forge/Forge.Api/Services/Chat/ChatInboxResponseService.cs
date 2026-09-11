// -----------------------------------------------------------------------
// <copyright file="ChatInboxResponseService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Replies have independent claims; a failed acknowledgement never redispatches a domain action.</summary>
public sealed class ChatInboxResponseService(ControlDbContext control, ChatRuntimeState runtime, ChatDbBudget budget,
    ChatConnectionSupervisor supervisor, TimeProvider clock, ChatEngineCoordinator engine)
{
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        using var lease = await engine.EnterAsync("plugins", ct);
        if (lease is null) return;
        using var continuation = engine.ContinueLease();
        using var limits = ChatDbBudget.Limit(control);
        if (!runtime.EnsureHostOwnership()) return;
        await runtime.ResponseGate.WaitAsync(ct);
        try
        {
            foreach (var checkpoint in runtime.ResponseCheckpoints.ToArray())
            {
                var accepted = await control.ChatInbox.SingleOrDefaultAsync(i => i.Id == checkpoint.Key, ct);
                if (accepted is not null)
                {
                    accepted.ResponseMessageId = JsonSerializer.Serialize(checkpoint.Value.Receipt.Messages);
                    accepted.ResponseState = checkpoint.Value.Uncertain ? "uncertain" : "sent"; accepted.ResponseOwner = null; accepted.ResponseDeadline = null;
                    await budget.SaveAsync(control, ct);
                }
                runtime.ResponseCheckpoints.TryRemove(checkpoint.Key, out _);
            }
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            foreach (var abandoned in await control.ChatInbox.Where(i => i.ResponseState == "sending" && (i.ResponseOwner != runtime.ProcessId || i.ResponseDeadline <= now)).Take(100).ToListAsync(ct))
                abandoned.ResponseState = "uncertain";
            if (control.ChangeTracker.HasChanges()) await budget.SaveAsync(control, ct);
            foreach (var row in await control.ChatInbox.Where(i => i.ResponseState == "pending" && i.ResponseText != null && i.NextResponseAt <= now).OrderBy(i => i.ReceivedAt).Take(20).ToListAsync(ct))
            {
                var envelope = JsonSerializer.Deserialize<ChatInboundEnvelope>(row.EnvelopeJson ?? "null");
                var connection = await control.ChatConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == row.ConnectionId && c.Enabled && c.DeletedAt == null, ct);
                if (envelope is null || connection is null) { row.ResponseState = "unavailable"; await budget.SaveAsync(control, ct); continue; }
                var session = await supervisor.GetAsync(connection, ct);
                if (session is null) { row.NextResponseAt = now + 30000; await budget.SaveAsync(control, ct); continue; }
                row.ResponseState = "sending"; row.ResponseOwner = runtime.ProcessId; row.ResponseDeadline = now + 60000; row.ResponseAttempts++;
                await budget.SaveAsync(control, ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                ChatProviderResult<ChatSendReceipt> result;
                try { result = await session.Provider.SendAsync(envelope.Address, new(row.Id, "interaction-result", "Forge", row.ResponseText!, 1, []), timeout.Token); }
                catch { result = ChatProviderResult<ChatSendReceipt>.Fail(ChatFailureKind.UnknownOutcome, "reply-outcome-unknown"); }
                if (result.Value is { Messages.Count: > 0 } receipt && receipt.Messages.All(m => m.Address == envelope.Address && !string.IsNullOrEmpty(m.MessageId)))
                {
                    // Keep a successful provider result in memory until its SQLite checkpoint commits.
                    runtime.ResponseCheckpoints[row.Id] = (receipt, !result.Succeeded);
                    row.ResponseMessageId = JsonSerializer.Serialize(receipt.Messages); row.ResponseState = result.Succeeded ? "sent" : "uncertain";
                    await budget.SaveAsync(control, ct); runtime.ResponseCheckpoints.TryRemove(row.Id, out _);
                }
                else
                {
                    row.ResponseState = result.Failure?.Kind is ChatFailureKind.RateLimited or ChatFailureKind.Unavailable
                        ? row.ResponseAttempts < 10 ? "pending" : "failed" : result.Failure?.Kind == ChatFailureKind.UnknownOutcome || result.Succeeded ? "uncertain" : "failed";
                    var seconds = Math.Clamp(result.Failure?.RetryAfter?.TotalSeconds ?? 30, 1, 86400);
                    row.NextResponseAt = clock.GetUtcNow().AddSeconds(seconds).ToUnixTimeMilliseconds();
                    await budget.SaveAsync(control, ct);
                }
            }
        }
        finally { runtime.ResponseGate.Release(); }
    }
}