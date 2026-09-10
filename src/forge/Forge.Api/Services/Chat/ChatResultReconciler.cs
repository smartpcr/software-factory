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
    ChatDbBudget budget, TimeProvider clock, ChatRuntimeState runtime)
{
    private static readonly string[] OpenStates = ["pending", "retry", "sending", "sent", "uncertain", "failed"];
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        using var limits = ChatDbBudget.Limit(control);
        var states = OpenStates.ToList();
        var cursor = runtime.LastReconciledDelivery ?? "";
        var deliveries = await control.ChatDeliveries.Where(d => states.Contains(d.State) && string.Compare(d.Id, cursor) > 0).OrderBy(d => d.Id).Take(100).ToListAsync(ct);
        if (deliveries.Count == 0) { runtime.LastReconciledDelivery = null; return; }
        runtime.LastReconciledDelivery = deliveries.Count < 100 ? null : deliveries[^1].Id;
        foreach (var group in deliveries.GroupBy(d => d.NotificationId))
        {
            var notification = await control.ChatNotifications.SingleAsync(n => n.Id == group.Key, ct);
            long? domainRevision = null;
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
            if (notification.ScopeKind == "spawn" && notification.Kind != "open-questions")
            {
                var product = await control.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == notification.ProductId, ct);
                if (product is not null && resolver.ProductAvailable(product))
                    domainRevision = await ChatDomainRevision.ReadAsync(products.CreateFor(product.Slug), new(SpawnId: notification.SpawnId,
                        WorkstreamId: notification.WorkstreamId, ArtifactKey: notification.ArtifactKey), ct);
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
                        && (!latest.TryGetValue(part.QuestionId, out var question) || question.Id != part.ExpectedRevision || !string.IsNullOrEmpty(question.Answer));
                    var domainChanged = part.ExpiresAt <= clock.GetUtcNow().ToUnixTimeMilliseconds()
                        || part.QuestionId is null && domainRevision is { } currentRevision && currentRevision != part.ExpectedRevision;
                    if (part.ControlsRetired || !retired && !questionClosed && !domainChanged) continue;
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
            if (notification.RespondedAt is null && !await control.ChatDeliveryMessages.AnyAsync(m => !m.ControlsRetired
                && control.ChatDeliveries.Any(d => d.Id == m.DeliveryId && d.NotificationId == notification.Id), ct))
            {
                notification.RespondedAt = clock.GetUtcNow();
                if (notification.Status is "pending" or "sent") notification.Status = "resolved";
                await budget.SaveAsync(control, ct);
            }
        }
    }
}