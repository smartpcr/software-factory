// -----------------------------------------------------------------------
// <copyright file="ChatInboxService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed partial class ChatInboxService(ControlDbContext control, ChatRuntimeState runtime, ChatDbBudget budget,
    ChatSecretResolver secrets, ChatContentPolicy policy, TimeProvider clock, IServiceScopeFactory scopes)
{
    public async ValueTask<ChatInboundAcceptance> AcceptBatchAsync(IReadOnlyList<ChatInboundEnvelope> envelopes,
        IReadOnlyList<ChatAcceptedCursor> cursors, IReadOnlyDictionary<string, long> revisions, CancellationToken ct = default)
    {
        if (!runtime.EnsureHostOwnership() || envelopes.Count is 0 or > 20 || cursors.Count > envelopes.Count) return ChatInboundAcceptance.Retry;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var token = timeout.Token;
        try
        {
            await runtime.InboxGate.WaitAsync(token);
            try
            {
                var accepted = false;
                await budget.WriteAsync(control, async () =>
                {
                    control.ChangeTracker.Clear();
                    await using var tx = await control.Database.BeginTransactionAsync(token);
                    foreach (var envelope in envelopes)
                    {
                        if (envelope.DeliveryKey.Length is 0 or > 512 || envelope.ConnectionId != envelope.Address.ConnectionId
                            || envelope.SenderId.Length > 512 || envelope.Address.ChannelId.Length > 512) throw new InvalidOperationException("invalid-envelope");
                        var connection = await control.ChatConnections.SingleOrDefaultAsync(c => c.Id == envelope.ConnectionId && c.Enabled && c.DeletedAt == null, token);
                        if (connection is null || connection.ProviderId != envelope.ProviderId
                            || !revisions.TryGetValue(connection.Id, out var revision) || revision != connection.Revision) throw new InvalidOperationException("stale-receiver");
                        if (await control.ChatInbox.AnyAsync(i => i.ConnectionId == envelope.ConnectionId && i.DeliveryKey == envelope.DeliveryKey, token)
                            || control.ChatInbox.Local.Any(i => i.ConnectionId == envelope.ConnectionId && i.DeliveryKey == envelope.DeliveryKey)) continue;
                        var safe = envelope.Version == 1 && (envelope.Text is null || policy.AcceptsAnswer(envelope.Text, await secrets.ValuesAsync(connection, token)));
                        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
                        control.ChatInbox.Add(new() { ConnectionId = envelope.ConnectionId, ProviderId = envelope.ProviderId, DeliveryKey = envelope.DeliveryKey,
                            EnvelopeJson = safe ? JsonSerializer.Serialize(envelope) : null, ReceivedAt = now,
                            State = safe ? "pending" : "rejected", Result = safe ? null : "safe-replacement-required", TerminalAt = safe ? null : now });
                        accepted = true;
                    }
                    foreach (var cursor in cursors)
                    {
                        if (cursor.Cursor.Length > 1024 || !envelopes.Any(e => e.ConnectionId == cursor.ConnectionId)) throw new InvalidOperationException("invalid-cursor");
                        var row = await control.ChatConnectionStates.SingleOrDefaultAsync(s => s.ConnectionId == cursor.ConnectionId, token);
                        if (row is null) { row = new() { ConnectionId = cursor.ConnectionId }; control.ChatConnectionStates.Add(row); }
                        row.Cursor = cursor.Cursor; row.SettingsRevision = revisions[cursor.ConnectionId];
                    }
                    if (control.ChangeTracker.HasChanges()) await control.SaveChangesAsync(token);
                    await tx.CommitAsync(token);
                }, token);
                foreach (var envelope in envelopes) runtime.Health[envelope.ConnectionId] = ("ready", null, null, clock.GetUtcNow().ToUnixTimeMilliseconds());
                runtime.Signal();
                return accepted ? ChatInboundAcceptance.Accepted : ChatInboundAcceptance.Duplicate;
            }
            finally { runtime.InboxGate.Release(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            foreach (var connectionId in envelopes.Select(e => e.ConnectionId)) runtime.Health[connectionId] = ("degraded", "inbox-storage-unavailable", null, null);
            return ChatInboundAcceptance.Retry;
        }
    }
}
