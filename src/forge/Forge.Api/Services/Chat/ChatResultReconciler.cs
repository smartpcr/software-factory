// -----------------------------------------------------------------------
// <copyright file="ChatResultReconciler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Domain state is authoritative, including web actions whose control-DB checkpoint was missed.</summary>
public sealed class ChatResultReconciler(ControlDbContext control, IProductDbContextFactory products, ChatScopeResolver resolver,
    ChatDbBudget budget, TimeProvider clock)
{
    private static readonly string[] OpenStates = ["pending", "retry", "sending", "sent", "uncertain", "failed"];
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var states = OpenStates.ToList();
        var deliveries = await control.ChatDeliveries.Where(d => states.Contains(d.State)).OrderBy(d => d.CreatedAt).Take(500).ToListAsync(ct);
        foreach (var group in deliveries.GroupBy(d => d.NotificationId))
        {
            var notification = await control.ChatNotifications.SingleAsync(n => n.Id == group.Key, ct);
            var payload = ChatPayloadConverter.Read(notification);
            Dictionary<string, DeliverableQuestionRow>? latest = null;
            if (notification.ScopeKind == "spawn" && notification.WorkItemId is not null)
            {
                var product = await control.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == notification.ProductId, ct);
                if (product is not null && resolver.ProductAvailable(product))
                {
                    try
                    {
                        latest = (await products.CreateFor(product.Slug).DeliverableQuestions.AsNoTracking().Where(q => q.WorkItemId == notification.WorkItemId).ToListAsync(ct))
                            .GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,
                                g => g.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First(), StringComparer.OrdinalIgnoreCase);
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException) { continue; }
                }
            }
            foreach (var delivery in group)
            {
                var target = JsonSerializer.Deserialize<ChatDeliveryTarget>(delivery.TargetJson);
                var reason = target is null ? "invalid-target" : await resolver.ValidateAsync(target, notification, ct);
                if (reason == "ownership-unverified") continue;
                var expired = notification.ExpiresAt <= clock.GetUtcNow();
                var retired = reason is not null || expired || notification.Status is "superseded" or "expired" or "resolved";
                var messages = await control.ChatDeliveryMessages.Where(m => m.DeliveryId == delivery.Id).ToListAsync(ct);
                foreach (var part in messages)
                {
                    var questionClosed = part.QuestionId is not null && latest is not null
                        && (!latest.TryGetValue(part.QuestionId, out var question) || question.Id != part.ExpectedRevision || question.Answer is not null);
                    if (part.ControlsRetired || !retired && !questionClosed) continue;
                    part.ControlsRetired = true; part.DesiredRevision++; part.TerminalAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
                    if (part.State == "sent") part.EditState = "pending";
                    else if (part.State == "pending") part.State = "cancelled";
                    foreach (var token in await control.ChatActionTokens.Where(t => t.DeliveryMessageId == part.Id && t.State == "active").ToListAsync(ct)) token.State = "retired";
                }
                if (retired || messages.Count > 0 && messages.All(m => m.ControlsRetired))
                {
                    // An uncertain external operation remains visible for repair even after its controls expire.
                    if (delivery.State is not ("uncertain" or "sending")) delivery.State = retired ? "cancelled" : "resolved";
                    delivery.TerminalAt = clock.GetUtcNow().ToUnixTimeMilliseconds(); delivery.LastError = reason;
                }
            }
            if (control.ChangeTracker.HasChanges()) await budget.SaveAsync(control, ct);
        }
    }
}
