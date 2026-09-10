// -----------------------------------------------------------------------
// <copyright file="ChatConfigurationService.Subscriptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed partial class ChatConfigurationService
{
    public async Task<ChatSubscriptionRow> PutSubscriptionAsync(ForgeActor? actor, ChatConfigurationScope scope, string id,
        ChatSubscriptionWrite write, CancellationToken ct = default)
    {
        var parent = await this.RequireScopeAsync(actor, scope, write.ParentRevision, ct);
        RequireText(id);
        var row = await control.ChatSubscriptions.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (row is not null && (row.ScopeKey != scope.Key || row.RetiredAt is not null)) throw new ChatConfigurationException("subscription-not-found", 404);
        CheckRevision(row?.Revision, write.ExpectedRevision);
        var destination = await control.ChatDestinations.SingleOrDefaultAsync(d => d.Id == write.DestinationId && d.DeletedAt == null, ct);
        if (destination is null) throw new ChatConfigurationException("destination-not-found", 404);
        if (scope.ProductId is not null) await this.RequireConnectionGrantAsync(destination.ConnectionId, scope.ProductId, ct);
        var connection = await this.ConnectionAsync(destination.ConnectionId, ct);
        var provider = providers.Resolve(connection.ProviderId) ?? throw new ChatConfigurationException("provider-unavailable", 409);
        if (write.ConversationPolicy is not ("flat" or "topic" or "thread")
            || write.ConversationPolicy == "topic" && !provider.Descriptor.Capabilities.Topics
            || write.ConversationPolicy == "thread" && !provider.Descriptor.Capabilities.Threads)
            throw new ChatConfigurationException("unsupported-conversation-policy");
        if (write.Interactive && !ChatPluginInteractiveGate.IsAvailable)
            throw new ChatConfigurationException("interactive-mode-unavailable", 409);
        if (write.CatalogVersion != 1) throw new ChatConfigurationException("unsupported-event-catalog");
        if (write.ContentMode is not ("metadata" or "redacted")) throw new ChatConfigurationException("unsupported-content-mode");
        var kinds = write.EventKinds ?? (scope.Kind == "host" ? HostKinds : SpawnKinds);
        var allowed = scope.Kind == "host" ? HostKinds.Concat(SpawnKinds).ToArray() : SpawnKinds;
        if (kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Count || kinds.Any(k => !allowed.Contains(k, StringComparer.Ordinal)))
            throw new ChatConfigurationException("invalid-event-kinds");
        // Retargeting an existing subscription would silently move a conversation. Create a new one.
        if (row is not null && (row.DestinationId != write.DestinationId || row.ConversationPolicy != write.ConversationPolicy))
            throw new ChatConfigurationException("subscription-target-immutable", 409);

        await using var transaction = await control.Database.BeginTransactionAsync(ct);
        row ??= new ChatSubscriptionRow { Id = id, ScopeKind = scope.Kind, ScopeKey = scope.Key, ProductId = scope.ProductId,
            SpawnId = scope.SpawnId, PluginId = scope.Kind == "spawn" ? "chat" : null, HostId = scope.HostId, Revision = 0 };
        if (row.Revision == 0) control.ChatSubscriptions.Add(row);
        row.DestinationId = write.DestinationId; row.ConversationPolicy = write.ConversationPolicy;
        row.Interactive = write.Interactive; row.Paused = write.Paused; row.CatalogVersion = write.CatalogVersion;
        row.ContentMode = write.ContentMode;
        row.EventKindsJson = JsonSerializer.Serialize(kinds); row.ActivationGeneration = parent?.ActivationGeneration ?? 0; row.Revision++;
        if (parent is not null) parent.Revision++;
        foreach (var binding in await control.ChatConversationBindings.Where(b => b.SubscriptionId == id && b.RetiredAt == null).ToListAsync(ct))
        { binding.RetiredAt = DateTimeOffset.UtcNow; binding.Revision++; }
        await this.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return row;
    }

    public async Task DeleteSubscriptionAsync(ForgeActor? actor, ChatConfigurationScope scope, string id, long revision, long parentRevision, CancellationToken ct = default)
    {
        var parent = await this.RequireScopeAsync(actor, scope, parentRevision, ct);
        var row = await control.ChatSubscriptions.SingleOrDefaultAsync(s => s.Id == id && s.ScopeKey == scope.Key && s.RetiredAt == null, ct)
            ?? throw new ChatConfigurationException("subscription-not-found", 404);
        CheckRevision(row.Revision, revision);
        await using var transaction = await control.Database.BeginTransactionAsync(ct);
        await this.RetireAsync([row], ct);
        if (parent is not null) parent.Revision++;
        await this.SaveAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<SpawnPluginRow?> RequireScopeAsync(ForgeActor? actor, ChatConfigurationScope scope, long parentRevision, CancellationToken ct)
    {
        if (scope.Kind == "host")
        {
            await authorizer.RequireAsync(actor, ForgeCapabilities.HostAlertManage, new(), ct);
            if (scope.HostId != options.Value.HostId) throw new ForgeAccessDeniedException();
            return null;
        }
        var product = await this.ProductAsync(actor, scope.ProductId!, scope.SpawnId, true, ct);
        if (product.Id != scope.ProductId) throw new ChatConfigurationException("canonical-product-id-required");
        var spawn = await products.CreateFor(product.Slug).StorySpawns.AsNoTracking().SingleAsync(s => s.Id == scope.SpawnId, ct);
        var parent = await control.SpawnPlugins.SingleOrDefaultAsync(p => p.ProductId == product.Id && p.SpawnId == scope.SpawnId && p.PluginId == "chat", ct)
            ?? throw new ChatConfigurationException("configure-parent-first", 409);
        CheckRevision(parent.Revision, parentRevision);
        if (parent.RunIdentity != RunIdentity(spawn)) throw new ChatConfigurationException("new-run-needs-activation", 409);
        if (this.PluginReason(parent) is not null) throw new ChatConfigurationException("plugin-settings-unavailable", 409);
        return parent;
    }
}
