// -----------------------------------------------------------------------
// <copyright file="ChatTelegramSliceService.Outbox.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed partial class ChatTelegramSliceService
{
    public async Task<bool> EnqueueAsync(OpenQuestionsAlert alert, CancellationToken ct)
    {
        await coordinator.Gate.WaitAsync(ct);
        try
        {
            if (await this.GuardAsync(ct) is not null) return false;
            // Producers currently carry the product slug. Resolve it, then verify the real child and parent in that DB.
            var product = await authorizer.ResolveProductAsync(alert.ProductId, ct);
            if (product is null) return false;
            if (SplitMarkerCache.IsPhase73Applied(paths) && !paths.ProductDbExists(product.Slug)) return false;
            var db = products.CreateFor(product.Slug);
            var child = await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(w => w.Id == alert.WorkItemId, ct);
            if (child?.StorySpawnId is not { } parent || alert.SpawnId is not null && alert.SpawnId != parent) return false;
            var route = await this.RouteAsync(product.Id, parent, ct);
            if (route is null || child.RepoId != route.Spawn.RepoId || alert.RepoId is not null && alert.RepoId != child.RepoId) return false;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(alert.WorkItemId + "\n" + alert.Body)));
            var key = "question:" + hash;
            var dedupe = "slice1:" + route.Binding.Id + ":" + key;
            if (await control.ChatNotifications.AnyAsync(n => n.DedupeKey == dedupe, ct)) return false;
            // Supersession cancels unsent metadata only. Previously delivered copies stay with Telegram.
            foreach (var old in await control.ChatNotifications.Where(n => n.Platform == Platform && n.ProductId == product.Id
                && n.SpawnId == parent && n.WorkItemId == child.Id && n.Status == "pending").ToListAsync(ct))
            { old.Status = ChatNotificationStatuses.Superseded; old.RespondedAt = DateTimeOffset.UtcNow; }
            await this.AddNotificationAsync(route, "open-questions", child.Id, key, ct);
            await control.SaveChangesAsync(ct);
            return true;
        }
        finally { coordinator.Gate.Release(); }
    }

    public async Task<int> RunOutboundOnceAsync(CancellationToken ct)
    {
        await coordinator.Gate.WaitAsync(ct);
        try
        {
            if (await this.GuardAsync(ct) is not null) return 0;
            // A previous process/tick may have sent without checkpointing. Preserve it for inspection; never retry blindly.
            foreach (var lost in await control.ChatNotifications.Where(n => n.Platform == Platform && n.Status == "sending").Take(100).ToListAsync(ct))
            {
                lost.Status = "uncertain"; lost.LastError = "telegram-send-unconfirmed";
                await this.SetHealthAsync(lost, "telegram-send-unconfirmed", ct);
            }
            await control.SaveChangesAsync(ct);
            var rows = await control.ChatNotifications.Where(n => n.Platform == Platform && n.Status == "pending")
                .OrderBy(n => n.Kind == "setup" ? 0 : 1).ThenBy(n => n.Id).Take(100).ToListAsync(ct);
            var sent = 0;
            foreach (var row in rows)
            {
                var route = await this.RouteAsync(row.ProductId, row.SpawnId ?? "", ct);
                var target = ReadTarget(row);
                if (route is null || target != route.Snapshot || row.ChatId != route.Destination.ChannelId
                    || row.ExpiresAt <= DateTimeOffset.UtcNow || row.Kind is not ("setup" or "open-questions")
                    || row.WorkItemId is not null && !await products.CreateFor(route.Product.Slug).Workstreams
                        .AnyAsync(w => w.Id == row.WorkItemId && w.StorySpawnId == route.Spawn.Id && w.RepoId == route.Spawn.RepoId, ct))
                {
                    row.Status = ChatNotificationStatuses.Expired; row.RespondedAt = DateTimeOffset.UtcNow;
                    await this.SetHealthAsync(row, "route-needs-setup", ct);
                    await control.SaveChangesAsync(ct);
                    continue;
                }
                if (row.Kind != "setup" && route.Binding.ProvisioningState != "ready") continue;
                try
                {
                    await using var session = await this.OpenAsync(route.Connection, ct);
                    var destination = await session.ValidateDestinationAsync(route.Address, ct);
                    if (!destination.Succeeded) throw new ChatConfigurationException(destination.Failure!.Code, 409);
                    // Regenerate metadata and redact current credentials on EVERY send. Never render stored agent/provider text.
                    var content = policy.Create(row.Id, row.Kind, route.Spawn.StoryId, await this.CredentialsAsync(route.Connection, ct));
                    row.Title = content.Title; row.Body = content.Body;
                    await control.SaveChangesAsync(ct);
                    var claimed = await control.ChatNotifications.Where(n => n.Id == row.Id && n.Status == "pending")
                        .ExecuteUpdateAsync(set => set.SetProperty(n => n.Status, "sending").SetProperty(n => n.Attempts, n => n.Attempts + 1), ct);
                    if (claimed != 1) continue;
                    row.Status = "sending"; row.Attempts++;
                    var result = await session.SendAsync(route.Address, content, ct);
                    if (result.Succeeded)
                    {
                        row.ChatMessageId = result.Value!.Messages.Single().MessageId;
                        row.SentAt = DateTimeOffset.UtcNow; row.Status = ChatNotificationStatuses.Sent; row.LastError = null;
                        route.Binding.ProvisioningState = "ready"; sent++;
                    }
                    else
                    {
                        row.Status = "uncertain"; row.LastError = "telegram-send-unconfirmed";
                        route.Binding.ProvisioningState = "telegram-send-unconfirmed";
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var uncertain = row.Status == "sending";
                    row.Status = uncertain ? "uncertain" : ChatNotificationStatuses.Failed;
                    row.RespondedAt = uncertain ? null : DateTimeOffset.UtcNow;
                    row.LastError = uncertain ? "telegram-send-unconfirmed" : ex is ChatConfigurationException config ? config.Code : "notification-send-failed";
                    route.Binding.ProvisioningState = row.LastError;
                }
                // Checkpoint failures intentionally escape. The persisted 'sending' row is recovered as uncertain, without another call.
                await control.SaveChangesAsync(ct);
            }
            return sent;
        }
        finally { coordinator.Gate.Release(); }
    }

    private async Task SetHealthAsync(ChatNotificationRow row, string state, CancellationToken ct)
    {
        if (ReadTarget(row) is { } target
            && await control.ChatConversationBindings.SingleOrDefaultAsync(b => b.Id == target.BindingId && b.RetiredAt == null, ct) is { } binding)
            binding.ProvisioningState = state;
    }

    internal static Target? ReadTarget(ChatNotificationRow row)
    {
        try { return row.ActionsJson is null ? null : JsonSerializer.Deserialize<Target>(row.ActionsJson); }
        catch (JsonException) { return null; }
    }
}