// -----------------------------------------------------------------------
// <copyright file="ChatRetentionService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Forge.Api.Auth;
using System.Text.Json;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Hourly bounded sweeps. Unknown external operations retain their IDs and lose their content.</summary>
public sealed class ChatRetentionService(ControlDbContext control, IProductDbContextFactory products, ChatScopeResolver resolver,
    ChatDbBudget budget, TimeProvider clock, ChatSliceRetention slice, ForgeCapabilityAuthorizer authorizer, HostIdentityStore identities)
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default, bool purgeContent = false)
    {
        using var limits = ChatDbBudget.Limit(control);
        var now = clock.GetUtcNow();
        var day7 = now.AddDays(-7); var day30 = now.AddDays(-30); var day90 = now.AddDays(-90);
        var ms7 = day7.ToUnixTimeMilliseconds(); var ms30 = day30.ToUnixTimeMilliseconds(); var ms90 = day90.ToUnixTimeMilliseconds();
        var expiry = now.AddDays(-1).ToUnixTimeMilliseconds(); var nowMs = now.ToUnixTimeMilliseconds();
        var count = 0;
        var activeLinks = identities.ActiveProviderLinks.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var link in (await control.ChatOperatorLinks.Where(l => l.RevokedAt == null).ToListAsync(ct)).Where(l => !activeLinks.Contains(l.Id)).Take(100))
            link.RevokedAt = nowMs;
        if (control.ChangeTracker.HasChanges()) await budget.SaveAsync(control, ct);
        identities.PurgeExpiredChallenges();
        await budget.WriteAsync(control, async () =>
        {
            await using var tx = await control.Database.BeginTransactionAsync(ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE chat_notifications SET Title='', Body='', ActionsJson=NULL, PayloadJson=NULL, LastError=NULL,
                  Status=CASE WHEN Status IN ('pending','sent') THEN 'expired' ELSE Status END
                  WHERE Id IN (SELECT Id FROM chat_notifications WHERE Platform='plugins' AND (Title<>'' OR Body<>'' OR PayloadJson IS NOT NULL OR LastError IS NOT NULL)
                  AND ({purgeContent} OR (RouteState='unrouted' AND julianday(CreatedAt)<=julianday({day7}))
                    OR (RespondedAt IS NOT NULL AND julianday(RespondedAt)<=julianday({day30})) OR julianday(CreatedAt)<=julianday({day90})) LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE chat_delivery_messages SET RenderedJson=NULL, LastError=NULL,
                  ControlsRetired=1, DesiredRevision=DesiredRevision+CASE WHEN ControlsRetired=0 THEN 1 ELSE 0 END,
                  EditState=CASE WHEN MessageId IS NOT NULL AND ControlsRetired=0 THEN 'pending' ELSE EditState END
                  WHERE Id IN (SELECT m.Id FROM chat_delivery_messages m JOIN chat_deliveries d ON d.Id=m.DeliveryId
                  WHERE (m.RenderedJson IS NOT NULL OR m.LastError IS NOT NULL) AND ({purgeContent} OR m.TerminalAt<={ms30} OR d.CreatedAt<={ms90}) LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE chat_deliveries SET LastError=NULL WHERE Id IN (SELECT Id FROM chat_deliveries WHERE LastError IS NOT NULL
                  AND ({purgeContent} OR TerminalAt<={ms30} OR CreatedAt<={ms90}) LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE chat_inbox SET EnvelopeJson=NULL, ResponseText=NULL,
                  ResponseState=CASE WHEN ResponseState='pending' THEN 'expired' ELSE ResponseState END,
                  State=CASE WHEN State IN ('pending','processing') THEN 'expired' ELSE State END,
                  Result=CASE WHEN State IN ('pending','processing') THEN 'content-expired' ELSE Result END, TerminalAt=COALESCE(TerminalAt,{nowMs})
                  WHERE Id IN (SELECT Id FROM chat_inbox WHERE (EnvelopeJson IS NOT NULL OR ResponseText IS NOT NULL) AND ({purgeContent}
                    OR (State='processed' AND TerminalAt<={ms7}) OR ReceivedAt<={ms30}) LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE chat_action_tokens SET State='retired' WHERE Hash IN (SELECT t.Hash FROM chat_action_tokens t
                  JOIN chat_delivery_messages m ON m.Id=t.DeliveryMessageId WHERE t.State='active' AND (m.ControlsRetired=1 OR t.ExpiresAt<={nowMs}) LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM chat_action_tokens WHERE Hash IN (SELECT Hash FROM chat_action_tokens WHERE ExpiresAt<={expiry} LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM chat_link_challenges WHERE Hash IN (SELECT Hash FROM chat_link_challenges WHERE ExpiresAt<={expiry} LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM chat_operator_links WHERE Id IN (SELECT Id FROM chat_operator_links WHERE RevokedAt<={ms90} LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM chat_inbox WHERE Id IN (SELECT Id FROM chat_inbox WHERE TerminalAt<={ms90} AND State NOT IN ('pending','processing') AND ResponseState NOT IN ('sending','uncertain') LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM chat_delivery_messages WHERE Id IN (SELECT m.Id FROM chat_delivery_messages m JOIN chat_deliveries d ON d.Id=m.DeliveryId
                  WHERE m.TerminalAt<={ms90} AND m.State NOT IN ('sending','uncertain') AND d.State NOT IN ('sending','uncertain')
                  AND m.EditState NOT IN ('editing','pending') AND NOT EXISTS (SELECT 1 FROM chat_action_tokens t WHERE t.DeliveryMessageId=m.Id) LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM chat_deliveries WHERE Id IN (SELECT d.Id FROM chat_deliveries d WHERE d.TerminalAt<={ms90}
                  AND d.State NOT IN ('sending','uncertain') AND NOT EXISTS (SELECT 1 FROM chat_delivery_messages m WHERE m.DeliveryId=d.Id) LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM chat_notifications WHERE Id IN (SELECT n.Id FROM chat_notifications n WHERE n.Platform='plugins'
                  AND julianday(n.CreatedAt)<=julianday({day90}) AND n.Status NOT IN ('pending','sent')
                  AND NOT EXISTS (SELECT 1 FROM chat_deliveries d WHERE d.NotificationId=n.Id) LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM chat_connection_state WHERE ConnectionId IN (SELECT s.ConnectionId FROM chat_connection_state s
                  WHERE s.RetiredAt<={ms90} AND NOT EXISTS (SELECT 1 FROM chat_deliveries d WHERE d.ConnectionId=s.ConnectionId)
                  AND NOT EXISTS (SELECT 1 FROM chat_inbox i WHERE i.ConnectionId=s.ConnectionId) LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM chat_receiver_state WHERE rowid IN (SELECT rowid FROM chat_receiver_state WHERE RetiredAt<={ms90} AND State='stopped' LIMIT 100)
                """, ct);
            await tx.CommitAsync(ct);
        }, ct);
        foreach (var product in await control.Products.AsNoTracking().ToListAsync(ct))
        {
            if (!resolver.ProductAvailable(product)) continue;
            try
            {
                var db = products.CreateFor(product.Slug);
                await budget.WriteAsync(db, async () => { count += await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM chat_action_receipts WHERE Id IN (SELECT Id FROM chat_action_receipts WHERE State='completed' AND TerminalAt<={ms90} LIMIT 100)
                    """, ct); }, ct);
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { }
        }
        count += await slice.SweepAsync(now, ct);
        return count;
    }

    public async Task<object> PurgeSpawnContentAsync(ForgeActor? actor, string productId, string spawnId, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.SubscriptionManage, new(productId, spawnId), ct);
        var product = await authorizer.ResolveProductAsync(productId, ct) ?? throw new ForgeAccessDeniedException();
        var count = 0; var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        await budget.WriteAsync(control, async () =>
        {
            await using var tx = await control.Database.BeginTransactionAsync(ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE chat_notifications SET Title='',Body='',PayloadJson=NULL,ActionsJson=NULL,LastError=NULL,Status='expired'
                WHERE Id IN (SELECT Id FROM chat_notifications WHERE Platform='plugins' AND ProductId={product.Id} AND SpawnId={spawnId}
                AND (Title<>'' OR Body<>'' OR PayloadJson IS NOT NULL OR ActionsJson IS NOT NULL OR LastError IS NOT NULL) LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE chat_delivery_messages SET RenderedJson=NULL,LastError=NULL,ControlsRetired=1,
                  DesiredRevision=DesiredRevision+CASE WHEN ControlsRetired=0 THEN 1 ELSE 0 END,
                  EditState=CASE WHEN MessageId IS NOT NULL AND ControlsRetired=0 THEN 'pending' ELSE EditState END
                WHERE Id IN (SELECT m.Id FROM chat_delivery_messages m JOIN chat_deliveries d ON d.Id=m.DeliveryId
                  JOIN chat_notifications n ON n.Id=d.NotificationId WHERE n.Platform='plugins' AND n.ProductId={product.Id} AND n.SpawnId={spawnId}
                  AND (m.RenderedJson IS NOT NULL OR m.LastError IS NOT NULL OR m.ControlsRetired=0) LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE chat_inbox SET EnvelopeJson=NULL,ResponseText=NULL,ResponseState=CASE WHEN ResponseState='pending' THEN 'expired' ELSE ResponseState END,
                  State=CASE WHEN State IN ('pending','processing') THEN 'expired' ELSE State END,
                  TerminalAt=COALESCE(TerminalAt,{now}),Result='content-purged'
                WHERE Id IN (SELECT i.Id FROM chat_inbox i WHERE (i.EnvelopeJson IS NOT NULL OR i.ResponseText IS NOT NULL) AND (EXISTS (
                  SELECT 1 FROM json_each(COALESCE(i.ContentScopesJson,'[]')) scope
                  WHERE json_extract(scope.value,'$.ProductId')={product.Id} AND json_extract(scope.value,'$.SpawnId')={spawnId}) OR EXISTS (
                  SELECT 1 FROM chat_delivery_messages m JOIN chat_deliveries d ON d.Id=m.DeliveryId
                  JOIN chat_notifications n ON n.Id=d.NotificationId
                  WHERE n.Platform='plugins' AND n.ProductId={product.Id} AND n.SpawnId={spawnId} AND d.ConnectionId=i.ConnectionId
                  AND ((m.MessageId=json_extract(i.EnvelopeJson,'$.ReplyToMessageId') AND m.AddressJson=json_extract(i.EnvelopeJson,'$.Address'))
                    OR EXISTS (SELECT 1 FROM chat_action_tokens t WHERE t.DeliveryMessageId=m.Id AND 'sha256:'||t.Hash=json_extract(i.EnvelopeJson,'$.ActionToken'))))) LIMIT 100)
                """, ct);
            await tx.CommitAsync(ct);
        }, ct);
        var notifications = control.ChatNotifications.Where(n => n.Platform == "plugins" && n.ProductId == product.Id && n.SpawnId == spawnId);
        var copies = control.ChatDeliveries.Where(d => notifications.Any(n => n.Id == d.NotificationId));
        var inboundRemaining = await control.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS Value FROM chat_inbox i WHERE (i.EnvelopeJson IS NOT NULL OR i.ResponseText IS NOT NULL) AND (EXISTS (
              SELECT 1 FROM json_each(COALESCE(i.ContentScopesJson,'[]')) scope
              WHERE json_extract(scope.value,'$.ProductId')={product.Id} AND json_extract(scope.value,'$.SpawnId')={spawnId}) OR EXISTS (
              SELECT 1 FROM chat_delivery_messages m JOIN chat_deliveries d ON d.Id=m.DeliveryId JOIN chat_notifications n ON n.Id=d.NotificationId
              WHERE n.Platform='plugins' AND n.ProductId={product.Id} AND n.SpawnId={spawnId} AND d.ConnectionId=i.ConnectionId
              AND ((m.MessageId=json_extract(i.EnvelopeJson,'$.ReplyToMessageId') AND m.AddressJson=json_extract(i.EnvelopeJson,'$.Address'))
                OR EXISTS (SELECT 1 FROM chat_action_tokens t WHERE t.DeliveryMessageId=m.Id AND 'sha256:'||t.Hash=json_extract(i.EnvelopeJson,'$.ActionToken')))))
            """).SingleAsync(ct);
        return new { Scrubbed = count,
            ContentRemaining = await notifications.CountAsync(n => n.Body != "" || n.PayloadJson != null, ct)
                + await control.ChatDeliveryMessages.CountAsync(m => copies.Any(d => d.Id == m.DeliveryId) && m.RenderedJson != null, ct) + inboundRemaining,
            CorrelationRemaining = await copies.CountAsync(ct), UnconfirmedOperations = await copies.CountAsync(d => d.State == "uncertain" || d.State == "sending", ct) };
    }

    public async Task<object> PurgeContentAsync(ForgeActor? actor, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        var scrubbed = await this.RunOnceAsync(ct, purgeContent: true);
        var sliceResult = JsonSerializer.SerializeToElement(await slice.PurgeContentAsync(actor, ct));
        return new
        {
            Scrubbed = scrubbed + sliceResult.GetProperty("Scrubbed").GetInt32(),
            ContentRemaining = await control.ChatNotifications.CountAsync(n => n.Platform == "plugins" && (n.Body != "" || n.PayloadJson != null), ct)
                + await control.ChatInbox.CountAsync(i => i.EnvelopeJson != null, ct) + await control.ChatDeliveryMessages.CountAsync(m => m.RenderedJson != null, ct)
                + sliceResult.GetProperty("ContentRemaining").GetInt32(),
            CorrelationRemaining = await control.ChatDeliveries.CountAsync(ct) + await control.ChatInbox.CountAsync(ct) + sliceResult.GetProperty("CorrelationRemaining").GetInt32(),
            UnconfirmedOperations = await control.ChatDeliveries.CountAsync(d => d.State == "sending" || d.State == "uncertain", ct) + sliceResult.GetProperty("UnconfirmedOperations").GetInt32(),
        };
    }
}