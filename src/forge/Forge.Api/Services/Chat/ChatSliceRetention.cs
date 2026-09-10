// -----------------------------------------------------------------------
// <copyright file="ChatSliceRetention.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Hourly, bounded cleanup of the Stage 1S subset. Telegram copies and externally managed secrets are never deleted.</summary>
public sealed class ChatSliceRetention(ControlDbContext control, ChatSliceCoordinator coordinator,
    IProductDbContextFactory products, ChatTelegramSliceService slice, ForgeCapabilityAuthorizer authorizer, IForgeDbPaths paths)
{
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await coordinator.Gate.WaitAsync(ct);
        try
        {
            // The slice has one selected parent. The full five-minute lifecycle reconciler remains Stage 2.
            foreach (var plugin in await control.SpawnPlugins.Where(p => p.PluginId == "chat" && p.RetiredAt == null).Take(100).ToListAsync(ct))
            {
                var product = await control.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == plugin.ProductId, ct);
                if (product is not null && SplitMarkerCache.IsPhase73Applied(paths) && !paths.ProductDbExists(product.Slug)) continue;
                StorySpawnRow? spawn;
                try { spawn = product is null ? null : await products.CreateFor(product.Slug).StorySpawns.AsNoTracking().SingleOrDefaultAsync(s => s.Id == plugin.SpawnId, ct); }
                catch (Microsoft.Data.Sqlite.SqliteException) { continue; } // An unavailable product DB is not evidence of deletion.
                if (plugin.Enabled && spawn is not null && plugin.RunIdentity == ChatConfigurationService.RunIdentity(spawn)) continue;
                plugin.Enabled = false; plugin.RetiredAt = now; plugin.Revision++;
                await slice.RetireRouteAsync(plugin.ProductId, plugin.SpawnId, ct);
            }
            await control.SaveChangesAsync(ct);
            // SQLite DateTimeOffset comparisons use julianday, not unsupported EF date ordering or an unbounded client scan.
            var day7 = now.AddDays(-7).ToString("O");
            var day30 = now.AddDays(-30).ToString("O");
            var day90 = now.AddDays(-90).ToString("O");
            var stamp = now.ToString("O");
            var count = await control.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE chat_notifications SET Title = '', Body = '', LastError = NULL,
                  Status = CASE WHEN Status = 'pending' THEN 'expired' ELSE Status END,
                  RespondedAt = CASE WHEN Status = 'pending' THEN {stamp} ELSE RespondedAt END
                WHERE Id IN (SELECT Id FROM chat_notifications WHERE Platform = 'telegram-1s'
                  AND (Title <> '' OR Body <> '' OR LastError IS NOT NULL)
                  AND ((Status = 'pending' AND julianday(CreatedAt) <= julianday({day7}))
                    OR (Status IN ('sending','uncertain') AND julianday(CreatedAt) <= julianday({day90}))
                    OR (Status NOT IN ('pending','sending','uncertain') AND julianday(COALESCE(RespondedAt,SentAt,CreatedAt)) <= julianday({day30})))
                  LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM chat_notifications WHERE Id IN (SELECT Id FROM chat_notifications
                  WHERE Platform = 'telegram-1s' AND Status NOT IN ('pending','sending','uncertain')
                  AND julianday(COALESCE(RespondedAt,SentAt,CreatedAt)) <= julianday({day90}) LIMIT 100)
                """, ct);
            // ActionsJson in this slice is a versioned, content-free target snapshot, never button or question text.
            // Correlation holds bindings until all receipts (including uncertain sends) have gone.
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM chat_conversation_bindings WHERE Id IN (SELECT b.Id FROM chat_conversation_bindings b
                  WHERE julianday(b.RetiredAt) <= julianday({day90})
                  AND NOT EXISTS (SELECT 1 FROM chat_deliveries d WHERE d.BindingId = b.Id)
                  AND NOT EXISTS (SELECT 1 FROM chat_notifications n WHERE n.Platform = 'telegram-1s'
                    AND n.DedupeKey LIKE 'slice1:' || b.Id || ':%') LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM chat_subscriptions WHERE Id IN (SELECT s.Id FROM chat_subscriptions s
                  WHERE julianday(s.RetiredAt) <= julianday({day90})
                  AND NOT EXISTS (SELECT 1 FROM chat_conversation_bindings b WHERE b.SubscriptionId = s.Id) LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM spawn_plugins WHERE rowid IN (SELECT p.rowid FROM spawn_plugins p
                  WHERE p.PluginId = 'chat' AND p.Enabled = 0 AND julianday(p.RetiredAt) <= julianday({day90})
                  AND NOT EXISTS (SELECT 1 FROM chat_subscriptions s WHERE s.ProductId = p.ProductId AND s.SpawnId = p.SpawnId)
                  AND NOT EXISTS (SELECT 1 FROM chat_notifications n WHERE n.Platform = 'telegram-1s' AND n.ProductId = p.ProductId AND n.SpawnId = p.SpawnId) LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM chat_destinations WHERE Id IN (SELECT d.Id FROM chat_destinations d
                  WHERE julianday(d.DeletedAt) <= julianday({day90})
                  AND NOT EXISTS (SELECT 1 FROM chat_subscriptions s WHERE s.DestinationId = d.Id) LIMIT 100)
                """, ct);
            // Connection retirement revokes grants immediately, while metadata observes the 90-day window.
            count += await control.Database.ExecuteSqlRawAsync("""
                DELETE FROM chat_connection_products WHERE rowid IN (SELECT g.rowid FROM chat_connection_products g
                  JOIN chat_connections c ON c.Id = g.ConnectionId WHERE c.DeletedAt IS NOT NULL LIMIT 100)
                """, ct);
            count += await control.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM chat_connections WHERE Id IN (SELECT c.Id FROM chat_connections c
                  WHERE c.Enabled = 0 AND julianday(c.DeletedAt) <= julianday({day90})
                  AND NOT EXISTS (SELECT 1 FROM chat_destinations d WHERE d.ConnectionId = c.Id)
                  AND NOT EXISTS (SELECT 1 FROM chat_conversation_bindings b WHERE b.ConnectionId = c.Id)
                  AND NOT EXISTS (SELECT 1 FROM chat_connection_products g WHERE g.ConnectionId = c.Id) LIMIT 100)
                """, ct);
            return count;
        }
        finally { coordinator.Gate.Release(); }
    }

    public async Task<object> PurgeContentAsync(ForgeActor? actor, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        await coordinator.Gate.WaitAsync(ct);
        try
        {
            var stamp = DateTimeOffset.UtcNow.ToString("O");
            var scrubbed = await control.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE chat_notifications SET Title = '', Body = '', LastError = NULL,
                  Status = CASE WHEN Status = 'pending' THEN 'expired' ELSE Status END,
                  RespondedAt = CASE WHEN Status = 'pending' THEN {stamp} ELSE RespondedAt END
                WHERE Id IN (SELECT Id FROM chat_notifications WHERE Platform = 'telegram-1s'
                  AND (Title <> '' OR Body <> '' OR LastError IS NOT NULL) LIMIT 100)
                """, ct);
            return new
            {
                Scrubbed = scrubbed,
                ContentRemaining = await control.ChatNotifications.CountAsync(n => n.Platform == ChatTelegramSliceService.Platform && (n.Title != "" || n.Body != "" || n.LastError != null), ct),
                CorrelationRemaining = await control.ChatNotifications.CountAsync(n => n.Platform == ChatTelegramSliceService.Platform, ct),
                UnconfirmedOperations = await control.ChatNotifications.CountAsync(n => n.Platform == ChatTelegramSliceService.Platform && (n.Status == "sending" || n.Status == "uncertain"), ct),
            };
        }
        finally { coordinator.Gate.Release(); }
    }
}