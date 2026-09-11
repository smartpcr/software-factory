// -----------------------------------------------------------------------
// <copyright file="ChatConnectionCheckService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed record ChatCheckRequest(string RequestId, long ConnectionRevision, long DestinationRevision, string? Text = null);
public sealed record ChatCheckView(string Id, string State, string? Reason, DateTimeOffset CreatedAt, DateTimeOffset? SentAt, DateTimeOffset? ReplyAt, long ConnectionRevision, long DestinationRevision);
public sealed record ChatCheckTarget(string ConnectionId, long Revision, ChatAddress Address, long DestinationRevision);

/// <summary>Explicit, idempotent transport checks. The durable intent is uncertain before network I/O and is never automatically replayed.</summary>
public sealed class ChatConnectionCheckService(ControlDbContext control, ForgeCapabilityAuthorizer authorizer,
    ChatConnectionSupervisor supervisor, ChatRuntimeState runtime, TimeProvider clock, ChatEngineCoordinator engine, ChatSecretResolver secrets)
{
    public async Task<ChatCheckView> SendAsync(ForgeActor? actor, string connectionId, string destinationId, ChatCheckRequest request, CancellationToken ct)
    {
        using var lease = await engine.EnterAsync("plugins", ct) ?? throw new ChatConfigurationException("plugin-engine-not-owner", 409);
        using var continuation = engine.ContinueLease();
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        if (!Guid.TryParse(request.RequestId, out var requestId)) throw new ChatConfigurationException("request-id-required");
        if (request.Text?.Length > 2000) throw new ChatConfigurationException("check-text-too-long");
        var id = "check-" + requestId.ToString("N");
        await runtime.CheckGate.WaitAsync(ct);
        try
        {
            var existing = await control.ChatNotifications.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id, ct);
            if (existing is not null)
            {
                if (existing.QueueId != connectionId || existing.WorkItemId != destinationId) throw new ChatConfigurationException("request-id-conflict", 409);
                return View(existing);
            }
            var connection = await control.ChatConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connectionId && c.DeletedAt == null && c.Enabled, ct)
                ?? throw new ChatConfigurationException("connection-unavailable", 409);
            var destination = await control.ChatDestinations.AsNoTracking().SingleOrDefaultAsync(d => d.Id == destinationId && d.ConnectionId == connectionId && d.DeletedAt == null, ct)
                ?? throw new ChatConfigurationException("destination-not-found", 404);
            if (connection.Revision != request.ConnectionRevision || destination.Revision != request.DestinationRevision)
                throw new ChatConfigurationException("revision-conflict", 409);
            var session = await supervisor.GetAsync(connection, ct) ?? throw new ChatConfigurationException("session-unavailable", 409);
            var address = new ChatAddress(connectionId, destination.ExternalTenantId, destination.ChannelId);
            var row = new ChatNotificationRow { Id = id, Kind = "connection-check", Platform = "plugins-check", ScopeKind = "check",
                QueueId = connectionId, WorkItemId = destinationId, DedupeKey = id, ChatId = destination.ChannelId,
                CreatedAt = clock.GetUtcNow(), ExpiresAt = clock.GetUtcNow().AddMinutes(10), Status = "uncertain", Attempts = 1,
                ActionsJson = JsonSerializer.Serialize(new ChatCheckTarget(connectionId, connection.Revision, address, destination.Revision)) };
            control.ChatNotifications.Add(row); await control.SaveChangesAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var body = "Reply to this message with check within 10 minutes to verify inbound delivery. This check does not change work or link an identity.";
                if (!string.IsNullOrWhiteSpace(request.Text)) body = ChatContentPolicy.Redact(request.Text, await secrets.ValuesAsync(connection, ct)) + "\n\n" + body;
                var result = await session.Provider.SendAsync(address, new(id, "connection-check", "Forge · Connection check", body, 1, [], ForceReply: true), timeout.Token);
                var acknowledged = result.Succeeded && result.Value?.Messages.Count == 1 && result.Value.Messages[0].Address == address;
                row.Status = acknowledged ? "sent" : result.Succeeded || result.Failure?.Kind == ChatFailureKind.UnknownOutcome ? "uncertain" : "failed";
                row.LastError = result.Succeeded ? null : ChatConnectionSupervisor.SafeProviderReason(result.Failure?.Code);
                if (acknowledged)
                {
                    row.ChatMessageId = result.Value!.Messages.Last().MessageId; row.SentAt = clock.GetUtcNow();
                    runtime.RecordHealth(connectionId, "ready", null, row.SentAt.Value.ToUnixTimeMilliseconds());
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { row.LastError = "send-outcome-unknown"; }
            await control.SaveChangesAsync(CancellationToken.None);
            return View(row);
        }
        finally { runtime.CheckGate.Release(); }
    }

    public async Task<IReadOnlyList<ChatCheckView>> ReadAsync(ForgeActor? actor, string connectionId, string destinationId, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        var rows = await control.ChatNotifications.FromSqlInterpolated($"""
            SELECT * FROM chat_notifications WHERE Platform='plugins-check' AND QueueId={connectionId} AND WorkItemId={destinationId}
            ORDER BY julianday(CreatedAt) DESC LIMIT 20
            """).AsNoTracking().ToListAsync(ct);
        return rows.OrderByDescending(n => n.CreatedAt).Select(View).ToArray();
    }

    internal static async Task<bool> AcceptReplyAsync(ControlDbContext control, ChatInboundEnvelope envelope, long revision, DateTimeOffset now, CancellationToken ct)
    {
        if (envelope.ActionToken is not null || envelope.Command is not null || !string.Equals(envelope.Text?.Trim(), "check", StringComparison.OrdinalIgnoreCase)) return false;
        var anchor = envelope.ReplyToMessageId ?? (envelope.ProviderId == "slack" ? envelope.Address.ConversationId : null);
        if (anchor is null) return false;
        var candidates = await control.ChatNotifications.Where(n => n.Platform == "plugins-check" && n.QueueId == envelope.ConnectionId
            && n.ChatId == envelope.Address.ChannelId && n.ChatMessageId == anchor && n.Status == "sent").ToListAsync(ct);
        foreach (var row in candidates)
        {
            var target = JsonSerializer.Deserialize<ChatCheckTarget>(row.ActionsJson!);
            if (target?.Revision != revision || target.Address.ExternalTenantId != envelope.Address.ExternalTenantId || row.ExpiresAt <= now) continue;
            if (!await control.ChatDestinations.AnyAsync(d => d.Id == row.WorkItemId && d.Revision == target.DestinationRevision && d.DeletedAt == null, ct)) continue;
            row.RespondedAt ??= now;
            return true;
        }
        return false;
    }

    private static ChatCheckView View(ChatNotificationRow row)
    {
        var target = JsonSerializer.Deserialize<ChatCheckTarget>(row.ActionsJson!);
        return new(row.Id, row.Status, row.LastError, row.CreatedAt, row.SentAt, row.RespondedAt, target!.Revision, target.DestinationRevision);
    }
}