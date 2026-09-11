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
    ChatSecretResolver secrets, ChatContentPolicy policy, TimeProvider clock, IServiceScopeFactory scopes, ChatEngineCoordinator engine)
{
    public async ValueTask<ChatInboundAcceptance> AcceptBatchAsync(IReadOnlyList<ChatInboundEnvelope> envelopes,
        IReadOnlyList<ChatAcceptedCursor> cursors, IReadOnlyDictionary<string, long> revisions, CancellationToken ct = default)
    {
        using var limits = ChatDbBudget.Limit(control);
        if (envelopes.Count == 0) return cursors.Count == 0 ? ChatInboundAcceptance.Accepted : ChatInboundAcceptance.Retry;
        if (!runtime.EnsureHostOwnership() || envelopes.Count > 20 || cursors.Count > envelopes.Count) return ChatInboundAcceptance.Retry;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var token = timeout.Token;
        try
        {
            await runtime.InboxGate.WaitAsync(token);
            try
            {
                var accepted = false;
                var safeEnvelopes = new Dictionary<(string Connection, string Key), bool>();
                foreach (var envelope in envelopes)
                {
                    var connection = await control.ChatConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == envelope.ConnectionId, token)
                        ?? throw new InvalidOperationException("connection-unavailable");
                    safeEnvelopes[(envelope.ConnectionId, envelope.DeliveryKey)] = envelope.Version == 1
                        && (envelope.Text is null || policy.AcceptsAnswer(envelope.Text, await secrets.ValuesAsync(connection, token).WaitAsync(token)));
                }
                await budget.WriteAsync(control, async () =>
                {
                    accepted = false;
                    var acceptedConnections = new HashSet<string>(StringComparer.Ordinal);
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
                        var safe = safeEnvelopes[(envelope.ConnectionId, envelope.DeliveryKey)];
                        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
                        var checkReply = safe && await ChatConnectionCheckService.AcceptReplyAsync(control, envelope, revision, clock.GetUtcNow(), token);
                        control.ChatInbox.Add(new() { ConnectionId = envelope.ConnectionId, ProviderId = envelope.ProviderId, DeliveryKey = envelope.DeliveryKey,
                            EnvelopeJson = checkReply ? null : JsonSerializer.Serialize(NormalizeStored(envelope, safe)), ReceivedAt = now,
                            State = checkReply ? "processed" : "pending", TerminalAt = checkReply ? now : null,
                            Result = checkReply ? "connection-check" : safe ? null : "safe-replacement-required" });
                        accepted = true;
                        acceptedConnections.Add(envelope.ConnectionId);
                    }
                    foreach (var cursor in cursors)
                    {
                        if (!acceptedConnections.Contains(cursor.ConnectionId)) continue; // Other connections cannot advance a duplicate's cursor.
                        if (cursor.Cursor.Length > 1024 || !envelopes.Any(e => e.ConnectionId == cursor.ConnectionId)) throw new InvalidOperationException("invalid-cursor");
                        var row = await control.ChatConnectionStates.SingleOrDefaultAsync(s => s.ConnectionId == cursor.ConnectionId, token);
                        if (row is null) { row = new() { ConnectionId = cursor.ConnectionId }; control.ChatConnectionStates.Add(row); }
                        row.Cursor = cursor.Cursor; row.SettingsRevision = revisions[cursor.ConnectionId];
                    }
                    if (control.ChangeTracker.HasChanges()) await control.SaveChangesAsync(token);
                    await tx.CommitAsync(token);
                }, token);
                foreach (var envelope in envelopes) runtime.RecordHealth(envelope.ConnectionId, "ready", null, null, clock.GetUtcNow().ToUnixTimeMilliseconds());
                runtime.Signal();
                return accepted ? ChatInboundAcceptance.Accepted : ChatInboundAcceptance.Duplicate;
            }
            finally { runtime.InboxGate.Release(); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            foreach (var connectionId in envelopes.Select(e => e.ConnectionId)) runtime.RecordHealth(connectionId, "degraded", "inbox-storage-unavailable", null, null);
            return ChatInboundAcceptance.Retry;
        }
    }

    private static ChatInboundEnvelope NormalizeStored(ChatInboundEnvelope envelope, bool safe)
    {
        var command = envelope.Command; var argument = envelope.CommandArgument ?? "";
        if (command is not (null or "start" or "link" or "unlink" or "status" or "pause" or "resume" or "questions" or "answer")) command = "unknown";
        if (command is "start" or "link") argument = argument is { Length: 64 } ? "sha256:" + ChatEventCatalog.Hash(argument) : "";
        else if (command is "status" or "pause" or "resume" or "questions")
            argument = argument is { Length: <= 128 } && argument.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_' or '.') ? argument : "invalid-scope";
        else if (command == "answer") argument = argument is { Length: > 0 and <= 128 }
            && (envelope.ProviderId == "slack" ? argument.All(c => char.IsAsciiDigit(c) || c == '.') && argument.Count(c => c == '.') == 1 && argument[0] != '.' && argument[^1] != '.'
                : argument.All(char.IsAsciiDigit)) ? argument : "invalid-message";
        else argument = null;
        var action = envelope.ActionToken;
        if (envelope.ProviderId == "telegram" && action is { Length: <= 512 })
        {
            var fields = action.Split('|');
            static bool SafeId(string value) => value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
            if (fields.Length is 2 or 3 && fields[0] == "act" && fields[1] is "retry" or "accept" or "cancel" or "restart" && (fields.Length == 2 || SafeId(fields[2])))
            { command = "legacy-" + fields[1]; argument = fields.Length == 3 ? fields[2] : null; action = null; }
            else if (fields.Length == 3 && fields[0] == "oq" && SafeId(fields[1]) && int.TryParse(fields[2], out var index) && index is >= 0 and < 10)
            { command = "legacy-question"; argument = fields[1] + "|" + index; action = null; }
            else if (fields.Length >= 2 && fields[0] is "hi" or "strain")
            { command = "legacy-host"; argument = null; action = null; }
        }
        return envelope with { Text = safe ? envelope.Text : null, Kind = safe ? envelope.Kind : "rejected",
            ActionToken = action is null ? null : "sha256:" + ChatEventCatalog.Hash(action), Command = command, CommandArgument = argument };
    }
}