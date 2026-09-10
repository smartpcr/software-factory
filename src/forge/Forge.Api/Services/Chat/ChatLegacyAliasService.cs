// -----------------------------------------------------------------------
// <copyright file="ChatLegacyAliasService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>The migration entry point preserves the original qualified message before adding new question parts.</summary>
public sealed class ChatLegacyAliasService(ControlDbContext control, ForgeCapabilityAuthorizer authorizer,
    ChatScopeResolver resolver, IProductDbContextFactory products, ChatRuntimeState runtime, ChatDbBudget budget)
{
    public async Task<string> PreserveAsync(ForgeActor? actor, string deliveryId, ChatMessageReference original, long expectedRevision, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.ConnectionManage, new(), ct);
        await runtime.DeliveryGate.WaitAsync(ct);
        try
        {
            var delivery = await control.ChatDeliveries.AsNoTracking().SingleAsync(d => d.Id == deliveryId, ct);
            var notification = await control.ChatNotifications.AsNoTracking().SingleAsync(n => n.Id == delivery.NotificationId, ct);
            var target = JsonSerializer.Deserialize<ChatDeliveryTarget>(delivery.TargetJson)!;
            var binding = await control.ChatConversationBindings.AsNoTracking().SingleAsync(b => b.Id == delivery.BindingId, ct);
            var address = new ChatAddress(target.ConnectionId, target.ExternalTenantId, target.ChannelId, binding.ConversationId);
            if (original.Address != address || string.IsNullOrEmpty(original.MessageId) || original.MessageId.Length > 512 || target.ScopeKind != "spawn"
                || await resolver.ValidateAsync(target, notification, ct, action: true) is not null) throw new ChatConfigurationException("legacy-alias-target-invalid", 409);
            var product = await control.Products.AsNoTracking().SingleAsync(p => p.Id == target.ProductId, ct);
            if (expectedRevision != await ChatDomainRevision.ReadAsync(products.CreateFor(product.Slug),
                new(SpawnId: target.SpawnId, WorkstreamId: notification.WorkstreamId, ArtifactKey: notification.ArtifactKey), ct))
                throw new ChatConfigurationException("legacy-alias-stale-revision", 409);
            var partKey = "legacy:" + ChatEventCatalog.Hash(JsonSerializer.Serialize(original));
            var existing = await control.ChatDeliveryMessages.SingleOrDefaultAsync(m => m.DeliveryId == deliveryId && m.PartKey == partKey, ct);
            if (existing is not null) return existing.Id;
            var addressJson = JsonSerializer.Serialize(address);
            if (await control.ChatDeliveryMessages.AnyAsync(m => m.DeliveryId != deliveryId && m.PartKey.StartsWith("legacy:") && m.MessageId == original.MessageId && m.AddressJson == addressJson, ct))
                throw new ChatConfigurationException("legacy-alias-ambiguous", 409);
            var message = new ChatDeliveryMessageRow { DeliveryId = deliveryId, PartKey = partKey, MessageId = original.MessageId,
                AddressJson = JsonSerializer.Serialize(address), ExpectedRevision = expectedRevision, State = "sent" };
            control.ChatDeliveryMessages.Add(message); await budget.SaveAsync(control, ct); return message.Id;
        }
        finally { runtime.DeliveryGate.Release(); }
    }
}