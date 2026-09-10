// -----------------------------------------------------------------------
// <copyright file="ChatStatusService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed class ChatStatusService(ControlDbContext control, IProductDbContextFactory products, ForgeCapabilityAuthorizer authorizer,
    ChatScopeResolver resolver, ChatRuntimeState runtime, TimeProvider clock, IOptions<ChatPluginOptions> options, ChatDbBudget budget,
    ChatEventService events, ChatDeliveryService deliveries)
{
    public async Task<object> SpawnAsync(ForgeActor? actor, string productId, string spawnId, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.SpawnRead, new(productId, spawnId), ct);
        var product = await authorizer.ResolveProductAsync(productId, ct) ?? throw new ForgeAccessDeniedException();
        if (!resolver.ProductAvailable(product)) return new { State = "degraded", Reason = "ownership-unverified" };
        var db = products.CreateFor(product.Slug);
        var spawn = await db.StorySpawns.AsNoTracking().SingleAsync(s => s.Id == spawnId, ct);
        var children = await db.Workstreams.AsNoTracking().Where(w => w.StorySpawnId == spawnId).ToListAsync(ct);
        var childTargets = new List<object>();
        foreach (var child in children) childTargets.Add(new { child.Id, child.Status,
            ExpectedRevision = await ChatDomainRevision.ReadAsync(db, new(WorkstreamId: child.Id), ct) });
        var artifacts = new List<object>();
        var workItems = children.Select(c => c.Id).Concat(new[] { spawn.Id, spawn.StoryId }).ToList();
        var operationTargets = workItems.ToList();
        foreach (var artifact in PlanDocStateHelpers.GetPlanDocStates(spawn))
        {
            var workItemId = $"plan-doc:{spawn.Id}:{artifact.Key}.md";
            workItems.Add(workItemId);
            operationTargets.Add(spawn.Id + ":" + artifact.Key); operationTargets.Add(spawn.StoryId + ":" + artifact.Key);
            artifacts.Add(new { artifact.Key, WorkItemId = workItemId,
                ExpectedRevision = await ChatDomainRevision.ReadAsync(db, new(SpawnId: spawn.Id, ArtifactKey: artifact.Key), ct) });
        }
        var questions = await db.DeliverableQuestions.AsNoTracking().Where(q => workItems.Contains(q.WorkItemId)).ToListAsync(ct);
        var pendingQuestions = questions.GroupBy(q => (q.WorkItemId, ExternalId: q.ExternalId.ToUpperInvariant()))
            .Select(g => g.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First()).Where(q => string.IsNullOrEmpty(q.Answer))
            .Select(q => new { q.WorkItemId, q.ExternalId, ExpectedRevision = q.Id }).ToArray();
        return new
        {
            ProductId = product.Id, SpawnId = spawn.Id, spawn.Phase,
            ExpectedRevision = await ChatDomainRevision.ReadAsync(db, new(SpawnId: spawnId), ct),
            Children = childTargets, ChildCounts = children.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Count()),
            Artifacts = artifacts, PendingQuestions = pendingQuestions, PendingQuestionCount = pendingQuestions.Length,
            UnresolvedOperations = await db.ChatActionReceipts.AsNoTracking().Where(r => r.ProductId == product.Id && operationTargets.Contains(r.TargetId) && r.State != "completed")
                .Select(r => new { r.Id, r.TargetId, r.Action, r.OperationId, r.State, r.ExpectedRevision }).Take(100).ToListAsync(ct),
            Chat = await this.HistoryAsync(product.Id, spawnId, null, ct),
        };
    }

    public async Task<object> HostAsync(ForgeActor? actor, CancellationToken ct)
    {
        await authorizer.RequireAsync(actor, ForgeCapabilities.HostAlertManage, new(), ct);
        var samples = runtime.WriteMilliseconds.OrderBy(x => x).ToArray();
        return new
        {
            options.Value.HostId, Chat = await this.HistoryAsync(null, null, options.Value.HostId, ct),
            Health = await control.ChatConnectionStates.AsNoTracking().ToListAsync(ct),
            Orphaned = await control.ChatConversationBindings.CountAsync(b => b.ProvisioningState == "orphaned", ct),
            Degraded = await control.ChatConversationBindings.CountAsync(b => b.ProvisioningState == "degraded", ct),
            ExpiredInbox = await control.ChatInbox.CountAsync(i => i.State == "expired", ct),
            runtime.BusyWrites, runtime.CompletedWrites,
            WriteP95Milliseconds = samples.Length == 0 ? 0 : samples[(int)Math.Floor((samples.Length - 1) * .95)],
        };
    }

    private async Task<object> HistoryAsync(string? productId, string? spawnId, string? hostId, CancellationToken ct)
    {
        var events = await control.ChatNotifications.AsNoTracking().Where(n => n.Platform == "plugins" && (hostId != null
            ? n.ScopeKind == "host" && n.HostId == hostId : n.ScopeKind == "spawn" && n.ProductId == productId && n.SpawnId == spawnId))
            .Select(n => new { n.Id, n.Kind, n.RouteState, n.RouteReason, n.Status, n.CreatedAt }).ToListAsync(ct);
        var latest = events.OrderByDescending(n => n.CreatedAt).Take(100).ToArray(); var ids = latest.Select(n => n.Id).ToList();
        return new { Unrouted = events.Where(n => n.RouteState == "unrouted").GroupBy(n => n.RouteReason ?? "unknown").ToDictionary(g => g.Key, g => g.Count()),
            Events = latest, Deliveries = await control.ChatDeliveries.AsNoTracking().Where(d => ids.Contains(d.NotificationId))
                .Select(d => new { d.Id, d.NotificationId, d.ConnectionId, d.State, d.Attempts, d.LastError, d.NextAttemptAt, d.CreatedAt }).ToListAsync(ct) };
    }

    public async Task<object> RetryAsync(ForgeActor? actor, string id, CancellationToken ct)
    {
        await runtime.DeliveryGate.WaitAsync(ct);
        try { return await this.RetryCoreAsync(actor, id, ct); }
        finally { runtime.DeliveryGate.Release(); }
    }

    private async Task<object> RetryCoreAsync(ForgeActor? actor, string id, CancellationToken ct)
    {
        var delivery = await control.ChatDeliveries.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new ChatConfigurationException("delivery-not-found", 404);
        var target = JsonSerializer.Deserialize<ChatDeliveryTarget>(delivery.TargetJson)!;
        await authorizer.RequireAsync(actor, target.ScopeKind == "host" ? ForgeCapabilities.HostAlertManage : ForgeCapabilities.SubscriptionManage,
            target.ScopeKind == "host" ? new() : new(target.ProductId, target.SpawnId), ct);
        var notification = await control.ChatNotifications.SingleAsync(n => n.Id == delivery.NotificationId, ct);
        if (delivery.State != "failed" || notification.Status is "superseded" or "expired" || notification.ExpiresAt <= clock.GetUtcNow())
            throw new ChatConfigurationException("delivery-not-retryable", 409);
        if (await resolver.ValidateAsync(target, notification, ct) is { } reason) throw new ChatConfigurationException(reason, 409);
        delivery.State = "retry"; delivery.NextAttemptAt = clock.GetUtcNow().ToUnixTimeMilliseconds(); delivery.LastError = null;
        await budget.SaveAsync(control, ct); runtime.Signal(); return new { delivery.Id, delivery.State };
    }

    public async Task<object> SkipAsync(ForgeActor? actor, string id, CancellationToken ct)
    {
        await runtime.DeliveryGate.WaitAsync(ct);
        try
        {
            var delivery = await control.ChatDeliveries.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new ChatConfigurationException("delivery-not-found", 404);
            var target = JsonSerializer.Deserialize<ChatDeliveryTarget>(delivery.TargetJson)!;
            await authorizer.RequireAsync(actor, target.ScopeKind == "host" ? ForgeCapabilities.HostAlertManage : ForgeCapabilities.SubscriptionManage,
                target.ScopeKind == "host" ? new() : new(target.ProductId, target.SpawnId), ct);
            if (delivery.State is not ("pending" or "retry" or "failed")) throw new ChatConfigurationException("delivery-not-skippable", 409);
            await deliveries.CancelAsync(delivery, "operator-skipped", ct); runtime.Signal();
            return new { delivery.Id, delivery.State };
        }
        finally { runtime.DeliveryGate.Release(); }
    }

    public async Task<object> ResendAsync(ForgeActor? actor, string id, string requestId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128) throw new ChatConfigurationException("request-id-required");
        var row = await control.ChatNotifications.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id && n.Platform == "plugins", ct)
            ?? throw new ChatConfigurationException("notification-not-found", 404);
        await authorizer.RequireAsync(actor, row.ScopeKind == "host" ? ForgeCapabilities.HostAlertManage : ForgeCapabilities.SubscriptionManage,
            row.ScopeKind == "host" ? new() : new(row.ProductId, row.SpawnId), ct);
        // Account/queue alerts must be regenerated by their producer; old host state is not evidence it still applies.
        if (row.ScopeKind != "spawn" || !ChatEventCatalog.SpawnDefaults.Contains(row.Kind)) throw new ChatConfigurationException("fresh-event-required", 409);
        var input = new ChatEventInput(row.Kind, "resend:" + id + ":" + requestId, row.ProductId, row.SpawnId, row.WorkItemId,
            row.WorkstreamId, row.RepoId, row.DomainRevision, row.ArtifactKey);
        var owned = await resolver.ResolveAsync(input, ct);
        if (owned.Spawn is null || owned.Product is null) throw new ChatConfigurationException(owned.Reason ?? "unresolved-scope", 409);
        var db = products.CreateFor(owned.Product.Slug);
        if (row.Kind == "open-questions")
        {
            var questions = await db.DeliverableQuestions.AsNoTracking().Where(q => q.WorkItemId == row.WorkItemId).ToListAsync(ct);
            if (!questions.GroupBy(q => q.ExternalId, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(q => q.Iter).ThenByDescending(q => q.Id).First())
                .Any(q => string.IsNullOrEmpty(q.Answer))) throw new ChatConfigurationException("event-already-resolved", 409);
        }
        else if (!await db.Workstreams.AnyAsync(w => w.Id == row.WorkstreamId && w.Status == "stalled", ct))
            throw new ChatConfigurationException("event-already-resolved", 409);
        return new { Created = await events.EnqueueAsync(input, ct) };
    }
}

public sealed record ChatResendRequest(string RequestId);