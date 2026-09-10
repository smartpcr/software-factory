// -----------------------------------------------------------------------
// <copyright file="ChatRetentionService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Hourly bounded sweeps. Unknown external operations retain their IDs and lose their content.</summary>
public sealed class ChatRetentionService(ControlDbContext control, IProductDbContextFactory products, ChatScopeResolver resolver,
    ChatDbBudget budget, TimeProvider clock, ChatSliceRetention slice, ForgeCapabilityAuthorizer authorizer)
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default, bool purgeContent = false)
    {
        var now = clock.GetUtcNow();
        var day7 = now.AddDays(-7); var day30 = now.AddDays(-30); var day90 = now.AddDays(-90);
        var ms7 = day7.ToUnixTimeMilliseconds(); var ms30 = day30.ToUnixTimeMilliseconds(); var ms90 = day90.ToUnixTimeMilliseconds();
        var expiry = now.AddDays(-1).ToUnixTimeMilliseconds(); var nowMs = now.ToUnixTimeMilliseconds();
        var count = 0;
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
                UPDATE chat_inbox SET EnvelopeJson=NULL, State=CASE WHEN State IN ('pending','processing') THEN 'expired' ELSE State END,
                  Result=CASE WHEN State IN ('pending','processing') THEN 'content-expired' ELSE Result END, TerminalAt=COALESCE(TerminalAt,{nowMs})
                  WHERE Id IN (SELECT Id FROM chat_inbox WHERE EnvelopeJson IS NOT NULL AND ({purgeContent}
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
                DELETE FROM chat_inbox WHERE Id IN (SELECT Id FROM chat_inbox WHERE TerminalAt<={ms90} AND State NOT IN ('pending','processing') LIMIT 100)
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
                count += await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM chat_action_receipts WHERE Id IN (SELECT Id FROM chat_action_receipts WHERE State='completed' AND TerminalAt<={ms90} LIMIT 100)
                    """, ct);
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { }
        }
        count += await slice.RunOnceAsync(ct);
        return count;
    }

    public async Task<object> PurgeContentAsync(ForgeActor? actor, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        await this.RunOnceAsync(ct, purgeContent: true);
        await slice.PurgeContentAsync(actor, ct);
        return new
        {
            ContentRemaining = await control.ChatNotifications.CountAsync(n => n.Platform == "plugins" && (n.Body != "" || n.PayloadJson != null), ct)
                + await control.ChatInbox.CountAsync(i => i.EnvelopeJson != null, ct) + await control.ChatDeliveryMessages.CountAsync(m => m.RenderedJson != null, ct),
            CorrelationRemaining = await control.ChatDeliveries.CountAsync(ct) + await control.ChatInbox.CountAsync(ct),
            UnresolvedOperations = await control.ChatDeliveries.CountAsync(d => d.State == "sending" || d.State == "uncertain", ct),
        };
    }
}
