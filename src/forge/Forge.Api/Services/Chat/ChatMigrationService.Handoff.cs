// -----------------------------------------------------------------------
// <copyright file="ChatMigrationService.Handoff.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed partial class ChatMigrationService
{
    public async Task<ChatMigrationPreview> RetireCompatibilityAsync(ForgeActor? actor, long expectedRevision, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        await engine.InitializeAsync(ct);
        await engine.Commands.WaitAsync(ct);
        try
        {
            var state = await control.ChatMigrationState.SingleOrDefaultAsync(ct);
            Revision(state, expectedRevision);
            if (state?.Owner != "plugins") throw new ChatConfigurationException("migration-not-active", 409);
            if (state.Phase != "compatibility-retired")
            {
                using var quiesced = await engine.QuiesceAsync(ct);
                if (!await supervisor.StopAllAsync(ct)) throw new ChatConfigurationException("chat-receiver-stop-pending", 409);
                await using var tx = await control.Database.BeginTransactionAsync(ct);
                var imported = await control.ChatMigrationItems.Where(i => i.Kind == "notification").Select(i => i.TargetId).ToListAsync(ct);
                foreach (var part in await control.ChatDeliveryMessages.Where(m => imported.Contains(m.DeliveryId) && m.PartKey.StartsWith("legacy:") && !m.ControlsRetired).ToListAsync(ct))
                {
                    part.ControlsRetired = true; part.DesiredRevision++;
                    if (part.MessageId is not null) part.EditState = "pending";
                }
                state.Phase = "compatibility-retired"; state.Revision++; state.UpdatedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
                await control.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            }
            // Host subscriptions and their destinations are permanent configuration, independent of the compatibility endpoints.
            return await this.PreviewAsync(actor, ct);
        }
        finally { engine.Commands.Release(); }
    }

    public async Task<ChatMigrationPreview> CutoverAsync(ForgeActor? actor, ChatMigrationCutover request, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        await engine.InitializeAsync(ct);
        if (request.Activate is null || request.Activate.Count > 128 || request.Activate.Any(s => s is null || string.IsNullOrWhiteSpace(s.ProductId) || string.IsNullOrWhiteSpace(s.SpawnId) || s.ContentMode is not ("metadata" or "redacted"))
            || request.Activate.Select(s => ScopeKey(s.ProductId, s.SpawnId)).Distinct().Count() != request.Activate.Count)
            throw new ChatConfigurationException("invalid-migration-selection");
        await engine.Commands.WaitAsync(ct);
        try
        {
            var state = await control.ChatMigrationState.SingleOrDefaultAsync(ct) ?? throw new ChatConfigurationException("migration-import-required", 409);
            Revision(state, request.ExpectedRevision);
            if (state.Owner != "legacy" || state.Phase is not ("imported" or "legacy") || options.Value.TelegramSlice.Enabled)
                throw new ChatConfigurationException("migration-not-ready", 409);
            if (options.Value.Enabled == false) throw new ChatConfigurationException("host-chat-disabled", 409);
            if (state.SourceHash != this.SourceHash(this.Source())) throw new ChatConfigurationException("migration-source-changed", 409);
            var connection = await control.ChatConnections.AsNoTracking().SingleAsync(c => c.Id == ConnectionId, ct);
            await this.CredentialAsync(connection, ct);
            foreach (var selection in request.Activate)
            {
                var owned = await resolver.ResolveAsync(new("open-questions", "selection", selection.ProductId, selection.SpawnId), ct);
                if (owned.Reason is not null || owned.Product?.Id != selection.ProductId) throw new ChatConfigurationException("migration-selection-unresolved", 409);
                await authorizer.RequireAsync(actor, ForgeCapabilities.SubscriptionManage, new(selection.ProductId, selection.SpawnId), ct);
            }
            using var quiesced = await engine.QuiesceAsync(ct);
            if (!await supervisor.StopAllAsync(ct)) throw new ChatConfigurationException("chat-receiver-stop-pending", 409);
            await engine.Routing.WaitAsync(ct);
            try
            {
                // A producer either commits to the old outbox before this transaction or routes to the new engine after it.
                await using var tx = await control.Database.BeginTransactionAsync(ct);
                await this.ImportCoreAsync(state, connection.InstallationId, request.Activate, activate: true, ct);
                var legacyCursor = await control.ChatBridgeState.AsNoTracking().Select(s => s.LastUpdateId).SingleOrDefaultAsync(ct);
                var current = await control.ChatConnectionStates.FindAsync([ConnectionId], ct);
                if (current is null) { current = new() { ConnectionId = ConnectionId }; control.ChatConnectionStates.Add(current); }
                var cursor = Math.Max(legacyCursor, long.TryParse(current.Cursor, out var prior) ? prior : 0);
                current.Cursor = cursor.ToString(); current.Health = "starting"; current.RetiredAt = null;
                state.Cursor = cursor; state.Owner = "plugins"; state.Phase = "plugins"; state.Revision++;
                state.SelectedScopesJson = JsonSerializer.Serialize(request.Activate); state.UpdatedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
                await control.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                engine.Apply("plugins");
            }
            finally { engine.Routing.Release(); }
            return await this.PreviewAsync(actor, ct);
        }
        finally { engine.Commands.Release(); }
    }

    private async Task<string> CredentialAsync(ChatConnectionRow connection, CancellationToken ct)
    {
        var references = JsonSerializer.Deserialize<Dictionary<string, ChatSecretReference>>(connection.SecretReferencesJson);
        using var scope = scopes.CreateScope();
        var token = references?.GetValueOrDefault("bot-token") is { } reference
            ? await scope.ServiceProvider.GetRequiredService<ChatSecretResolver>().ReadAsync(reference, ct) : null;
        if (token is null || !token.StartsWith(connection.InstallationId + ":", StringComparison.Ordinal))
            throw new ChatConfigurationException("legacy-credential-unavailable-or-mismatched", 409);
        return token;
    }

    public async Task<ChatRollbackPreview> RollbackPreviewAsync(ForgeActor? actor, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        var state = await control.ChatMigrationState.AsNoTracking().SingleOrDefaultAsync(ct);
        var blockers = new List<ChatMigrationIssue>();
        if (state?.Owner != "plugins" || state.Phase == "compatibility-retired") blockers.Add(new("migration", ConnectionId, "rollback-not-available"));
        var importedItems = await control.ChatMigrationItems.Where(i => i.Kind == "subscription").ToListAsync(ct);
        var imported = importedItems.Select(i => i.TargetId).ToList();
        var unsupported = await control.ChatSubscriptions.Where(s => !imported.Contains(s.Id)
            && (s.RetiredAt == null || control.ChatDeliveries.Any(d => d.SubscriptionId == s.Id))).Select(s => s.Id).ToListAsync(ct);
        foreach (var sub in await control.ChatSubscriptions.Where(s => imported.Contains(s.Id)).ToListAsync(ct))
            if (importedItems.Single(i => i.TargetId == sub.Id).SourceId != sub.ScopeKey + ":" + sub.DestinationId) unsupported.Add(sub.Id);
        foreach (var inbox in await control.ChatInbox.AsNoTracking().Where(i => i.State == "pending" || i.State == "processing"
            || i.ResponseState == "sending" || i.ResponseState == "uncertain" || i.ResponseState == "pending").ToListAsync(ct))
            blockers.Add(new("inbox", inbox.Id, "inbound-or-response-not-settled"));
        var legacyKeys = new List<(string Key, string Subscription)>();
        foreach (var delivery in await control.ChatDeliveries.AsNoTracking().Where(d => imported.Contains(d.SubscriptionId)).ToListAsync(ct))
        {
            var parts = await control.ChatDeliveryMessages.AsNoTracking().Where(m => m.DeliveryId == delivery.Id).ToListAsync(ct);
            if (delivery.State is "sending" or "uncertain" || parts.Any(p => p.State is "sending" or "uncertain" || p.EditState is "editing" or "pending"))
                blockers.Add(new("delivery", delivery.Id, "outcome-or-edit-not-settled"));
            var original = await control.ChatMigrationItems.AnyAsync(i => i.Kind == "notification" && i.TargetId == delivery.Id, ct);
            var producerKey = await control.ChatMigrationItems.Where(i => i.Kind == "producer-dedupe" && i.SourceId == delivery.NotificationId).Select(i => i.TargetId).SingleOrDefaultAsync(ct);
            if (!original && producerKey is null) unsupported.Add(delivery.SubscriptionId);
            if (delivery.State is "pending" or "retry" or "sent")
            {
                var snapshotJson = await control.ChatMigrationItems.Where(i => i.Kind == "notification" && i.TargetId == delivery.Id).Select(i => i.SnapshotJson).SingleOrDefaultAsync(ct);
                var key = snapshotJson is null ? producerKey : JsonSerializer.Deserialize<ChatLegacyNotificationSnapshot>(snapshotJson)?.DedupeKey;
                if (key is not null) legacyKeys.Add((key, delivery.SubscriptionId));
            }
            if (parts.Count(p => p.State != "cancelled") > 1 && parts.Any(p => p.MessageId is not null) || !original && delivery.Actionable && parts.Any(p => !p.ControlsRetired)
                || parts.Any(p => p.MessageId is not null && !p.PartKey.StartsWith("legacy:") && !p.ControlsRetired)
                && await control.ChatActionTokens.AnyAsync(t => parts.Select(p => p.Id).Contains(t.DeliveryMessageId) && t.State == "active", ct))
                unsupported.Add(delivery.SubscriptionId);
        }
        foreach (var duplicate in legacyKeys.GroupBy(k => k.Key).Where(g => g.Count() > 1)) unsupported.AddRange(duplicate.Select(k => k.Subscription));
        var cursor = await control.ChatConnectionStates.AsNoTracking().Where(c => c.ConnectionId == ConnectionId).Select(c => c.Cursor).SingleOrDefaultAsync(ct);
        var legacyCursor = await control.ChatBridgeState.AsNoTracking().Select(s => s.LastUpdateId).SingleOrDefaultAsync(ct);
        return new(state?.Revision ?? 0, Math.Max(legacyCursor, long.TryParse(cursor, out var parsed) ? parsed : 0), blockers.Count == 0 && unsupported.Count == 0, blockers, unsupported.Distinct().ToArray());
    }

    public async Task<ChatMigrationPreview> RollbackAsync(ForgeActor? actor, ChatMigrationRollback request, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        await engine.InitializeAsync(ct);
        await engine.Commands.WaitAsync(ct);
        try
        {
            var state = await control.ChatMigrationState.SingleOrDefaultAsync(ct) ?? throw new ChatConfigurationException("migration-import-required", 409);
            Revision(state, request.ExpectedRevision);
            if (state.Owner != "plugins" || state.Phase == "compatibility-retired") throw new ChatConfigurationException("rollback-not-available", 409);
            var connection = await control.ChatConnections.AsNoTracking().SingleAsync(c => c.Id == ConnectionId, ct);
            var token = await this.CredentialAsync(connection, ct);
            using var quiesced = await engine.QuiesceAsync(ct);
            if (!await supervisor.StopAllAsync(ct)) throw new ChatConfigurationException("chat-receiver-stop-pending", 409);
            // Do not consume a new provider update after stopping reception. Durable accepted inbox work must finish before ownership changes.
            await engine.DuringTransitionAsync(async () =>
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ChatInboxService>().DispatchOnceAsync(ct);
                await scope.ServiceProvider.GetRequiredService<ChatInboxResponseService>().RunOnceAsync(ct);
            });
            if (!await supervisor.StopAllAsync(ct)) throw new ChatConfigurationException("chat-receiver-stop-pending", 409);
            await engine.Routing.WaitAsync(ct);
            try
            {
                await using var tx = await control.Database.BeginTransactionAsync(ct);
                var preview = await this.RollbackPreviewAsync(actor, ct);
                if (preview.Blockers.Count != 0 || preview.UnsupportedSubscriptions.Count != 0 && !request.PauseUnsupported)
                    throw new ChatConfigurationException("rollback-not-lossless-see-preview", 409);
                foreach (var sub in await control.ChatSubscriptions.Where(s => preview.UnsupportedSubscriptions.Contains(s.Id)).ToListAsync(ct))
                { sub.Paused = true; sub.Revision++; }
                await control.SaveChangesAsync(ct);
                await this.RestoreLegacyAsync(ct);
                var cursor = await control.ChatBridgeState.SingleOrDefaultAsync(ct);
                if (cursor is null) { cursor = new() { Id = ChatBridgeStateRow.SingletonId }; control.ChatBridgeState.Add(cursor); }
                cursor.LastUpdateId = preview.LatestCursor; cursor.UpdatedAt = clock.GetUtcNow();
                state.Cursor = preview.LatestCursor; state.Owner = "legacy"; state.Phase = "legacy"; state.Revision++;
                state.UpdatedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
                await control.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                using var scope = scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<IOptionsMonitor<ChatTransportOptions>>().CurrentValue.Telegram.BotToken = token;
                engine.Apply("legacy");
            }
            finally { engine.Routing.Release(); }
            return await this.PreviewAsync(actor, ct);
        }
        finally { engine.Commands.Release(); }
    }

    private async Task RestoreLegacyAsync(CancellationToken ct)
    {
        var imported = await control.ChatMigrationItems.Where(i => i.Kind == "subscription"
            && control.ChatSubscriptions.Any(s => s.Id == i.TargetId && !s.Paused && s.RetiredAt == null)).Select(i => i.TargetId).ToListAsync(ct);
        foreach (var delivery in await control.ChatDeliveries.Where(d => imported.Contains(d.SubscriptionId)).ToListAsync(ct))
        {
            var notification = await control.ChatNotifications.SingleAsync(n => n.Id == delivery.NotificationId, ct);
            var item = await control.ChatMigrationItems.AsNoTracking().SingleOrDefaultAsync(i => i.Kind == "notification" && i.SourceId == notification.Id, ct);
            var snapshot = item is null ? null : JsonSerializer.Deserialize<ChatLegacyNotificationSnapshot>(item.SnapshotJson);
            var parts = await control.ChatDeliveryMessages.Where(m => m.DeliveryId == delivery.Id).ToListAsync(ct);
            var part = parts.FirstOrDefault(p => p.MessageId is not null) ?? parts.FirstOrDefault();
            notification.Platform = "telegram"; notification.ScopeKind = "legacy";
            notification.DedupeKey = snapshot?.DedupeKey ?? await control.ChatMigrationItems.Where(i => i.Kind == "producer-dedupe" && i.SourceId == notification.Id)
                .Select(i => i.TargetId).SingleAsync(ct);
            notification.ChatId = delivery.ChannelId; notification.ChatMessageId = part?.MessageId;
            notification.Attempts = Math.Max(notification.Attempts, delivery.Attempts);
            notification.Status = delivery.State switch
            {
                "pending" or "retry" => "pending", "sent" => "sent", "failed" => "failed",
                "resolved" => snapshot?.Status is "answered" or "acted" ? snapshot.Status : notification.Kind == "open-questions" ? "answered" : "acted",
                "cancelled" => snapshot?.Status is "expired" or "superseded" ? snapshot.Status : "superseded",
                "blocked" => snapshot?.Status ?? "failed", _ => delivery.State,
            };
            if (notification.ChatMessageId is not null && notification.Status == "pending") notification.Status = "sent";
            notification.RespondedAt ??= delivery.TerminalAt is { } terminal ? DateTimeOffset.FromUnixTimeMilliseconds(terminal) : null;
            notification.SentAt ??= part?.MessageId is not null ? DateTimeOffset.FromUnixTimeMilliseconds(delivery.CreatedAt) : null;
            notification.RouteState = snapshot?.RouteState ?? "legacy"; notification.RouteReason = snapshot?.RouteReason;
            if (snapshot is not null)
            {
                notification.ProductId = snapshot.ProductId; notification.SpawnId = snapshot.SpawnId;
                notification.WorkstreamId = snapshot.WorkstreamId; notification.ArtifactKey = snapshot.ArtifactKey;
            }
            // Leave imported delivery/message records and topic bindings intact for review and another cutover.
        }
    }
}