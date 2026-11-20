// -----------------------------------------------------------------------
// <copyright file="ForgeCapabilities.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using System.Security.Claims;
using System.Collections.Frozen;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Microsoft.EntityFrameworkCore;

public static class ForgeCapabilities
{
    public const string ConnectionManage = "chat.connection.manage";
    public const string HostAlertManage = "host.alert.manage";
    public const string SubscriptionManage = "chat.subscription.manage";
    public const string LinkSelf = "chat.link.self";
    public const string SpawnRead = "spawn.read";
    public const string QuestionAnswer = "question.answer";
    public const string SpawnPause = "spawn.pause";
    public const string SpawnResume = "spawn.resume";
    /// <summary>Re-drive a halted story (the banner's Retry).</summary>
    public const string SpawnRetry = "spawn.retry";
    /// <summary>Pin a binding decision for a plan document.</summary>
    public const string ArtifactPin = "artifact.pin";
    public const string IdentityManage = "identity.manage";
    public static readonly IReadOnlySet<string> All = new[]
    {
        ConnectionManage, HostAlertManage, SubscriptionManage, LinkSelf, SpawnRead, QuestionAnswer,
        SpawnPause, SpawnResume, SpawnRetry, IdentityManage,
        "workstream.retry", "workstream.accept", "workstream.cancel", "workstream.restart",
        "artifact.retry", "artifact.accept", "artifact.cancel", "artifact.restart", ArtifactPin,
    }.ToFrozenSet(StringComparer.Ordinal);
    public static readonly IReadOnlySet<string> ArtifactKeys = PlanDocStateHelpers.PlanDocOrder.Append("qa-e2e").ToFrozenSet(StringComparer.Ordinal);
    public static readonly IReadOnlySet<string> Roles = new[]
        { "human", "eng", "rev", "qa", "arch", "tech-lead", "product-owner" }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>An actor can only be created by the resolver after checking a session or a provider link.</summary>
public sealed record ForgeActor
{
    internal ForgeActor(string userId, string revision, string source, string proof)
        => (this.UserId, this.Revision, this.Source, this.Proof) = (userId, revision, source, proof);
    public string UserId { get; }
    internal string Revision { get; }
    internal string Source { get; }
    internal string Proof { get; }
}

public sealed record ForgeActionTarget(string? ProductId = null, string? SpawnId = null,
    string? WorkstreamId = null, string? WorkItemId = null, string? ArtifactKey = null, string? StoryId = null);

public sealed class ForgeAccessDeniedException(string message = "The operator is not authorized for this target.") : Exception(message);

public sealed class ForgeActorResolver(HostIdentityStore identities, OperatorSessions sessions, ControlDbContext control)
{
    public async Task<ForgeActor?> FromBrowserAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (!sessions.IsCurrent(principal)) return null;
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var enrollment = identities.GetEnrollment(id);
        if (enrollment is not { Enabled: true } || !await control.Users.AsNoTracking().AnyAsync(u => u.Id == id, ct)) return null;
        return new(id, enrollment.Revision, "browser", principal.FindFirstValue(OperatorSessions.SessionClaim)!);
    }

    /// <summary>Called only by a verified transport adapter, with its actual provider/tenant/sender tuple.</summary>
    public async Task<ForgeActor?> FromProviderAsync(string provider, string tenantId, string providerUserId, CancellationToken ct = default)
    {
        var link = identities.FindLink(provider, tenantId, providerUserId);
        if (link is null || !await control.Users.AsNoTracking().AnyAsync(u => u.Id == link.UserId, ct)) return null;
        return new(link.UserId, link.Revision, "provider", link.Id);
    }

    public bool IsCurrent(ForgeActor actor)
    {
        var enrollment = identities.GetEnrollment(actor.UserId);
        if (enrollment is not { Enabled: true } || enrollment.Revision != actor.Revision) return false;
        return actor.Source switch
        {
            "browser" => sessions.IsCurrent(actor.Proof, actor.UserId),
            "provider" => identities.IsCurrentLink(actor.Proof, actor.UserId, actor.Revision),
            _ => false,
        };
    }
}

/// <summary>Fixed policy shared by HTTP and provider invocations, including current target membership.</summary>
public sealed class ForgeCapabilityAuthorizer(
    HostIdentityStore identities, ForgeActorResolver actors, ControlDbContext control, IProductDbContextFactory products)
{
    public async Task<bool> IsAllowedAsync(ForgeActor? actor, string capability, ForgeActionTarget target, CancellationToken ct = default)
    {
        if (actor is null || !ForgeCapabilities.All.Contains(capability) || !actors.IsCurrent(actor)
            || !await control.Users.AsNoTracking().AnyAsync(u => u.Id == actor.UserId, ct)
            || string.Equals(target.ProductId, "all", StringComparison.OrdinalIgnoreCase)) return false;

        if (capability is ForgeCapabilities.ConnectionManage or ForgeCapabilities.HostAlertManage or ForgeCapabilities.IdentityManage)
            return target == new ForgeActionTarget() && identities.GetEnrollment(actor.UserId) is { HostAdmin: true };
        if (capability == ForgeCapabilities.LinkSelf) return target == new ForgeActionTarget();

        var product = await this.ResolveProductAsync(target.ProductId, ct);
        if (product is null) return false;
        var role = actor.UserId == RequireRole.BuiltInProductOwnerId ? RequireRole.ProductOwner : await control.UserProductRoles.AsNoTracking()
            .Where(r => r.UserId == actor.UserId && r.ProductId == product.Id).Select(r => r.Role).SingleOrDefaultAsync(ct);
        if (role is null || !ForgeCapabilities.Roles.Contains(role)
            || (capability != ForgeCapabilities.SpawnRead && role != RequireRole.ProductOwner)) return false;

        // Same DB the endpoints read: the per-product file when it exists, else
        // the control DB. Products created after the split have no file and
        // live in the control DB, so a missing file is not a reason to deny.
        // Every row below is still scoped to this product by OwnsRepoAsync.
        var db = products.CreateFor(product.Slug);
        StorySpawnRow? spawn = null;
        if (target.SpawnId is not null || target.StoryId is not null)
        {
            spawn = target.SpawnId is not null
                ? await db.StorySpawns.AsNoTracking().SingleOrDefaultAsync(s => s.Id == target.SpawnId, ct)
                : await db.StorySpawns.AsNoTracking().SingleOrDefaultAsync(s => s.StoryId == target.StoryId, ct);
            if (spawn is null || (target.StoryId is not null && spawn.StoryId != target.StoryId)
                || !await OwnsRepoAsync(db, product.Id, spawn.RepoId, ct)) return false;
        }

        if (target.WorkstreamId is not null)
        {
            var child = await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(w => w.Id == target.WorkstreamId, ct);
            if (child is null || !await OwnsRepoAsync(db, product.Id, child.RepoId, ct)
                || (spawn is not null && child.StorySpawnId != spawn.Id)) return false;
            if (child.StorySpawnId is not null)
            {
                var parent = await db.StorySpawns.AsNoTracking().SingleOrDefaultAsync(s => s.Id == child.StorySpawnId, ct);
                if (parent is null || !await OwnsRepoAsync(db, product.Id, parent.RepoId, ct)) return false;
            }
        }

        if (target.ArtifactKey is not null && (spawn is null || !ForgeCapabilities.ArtifactKeys.Contains(target.ArtifactKey))) return false;
        if (target.WorkItemId is not null && !await this.OwnsWorkItemAsync(db, product.Id, target.WorkItemId, spawn?.Id, ct)) return false;

        return capability switch
        {
            ForgeCapabilities.SpawnRead => true,
            ForgeCapabilities.SubscriptionManage or ForgeCapabilities.SpawnPause or ForgeCapabilities.SpawnResume or ForgeCapabilities.SpawnRetry => spawn is not null,
            ForgeCapabilities.QuestionAnswer => target.WorkItemId is not null,
            _ when capability.StartsWith("workstream.", StringComparison.Ordinal) => target.WorkstreamId is not null,
            _ when capability.StartsWith("artifact.", StringComparison.Ordinal) => spawn is not null && target.ArtifactKey is not null,
            _ => false,
        };
    }

    public async Task RequireAsync(ForgeActor? actor, string capability, ForgeActionTarget target, CancellationToken ct = default)
    {
        if (!await this.IsAllowedAsync(actor, capability, target, ct)) throw new ForgeAccessDeniedException();
    }

    public Task<ProductRow?> ResolveProductAsync(string? id, CancellationToken ct = default) => string.IsNullOrWhiteSpace(id) || id == "all"
        ? Task.FromResult<ProductRow?>(null)
        : control.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id || p.Slug == id, ct);

    public async Task<IReadOnlyList<string>> ReadableProductIdsAsync(ForgeActor? actor, CancellationToken ct = default)
    {
        if (actor is null || !actors.IsCurrent(actor) || !await control.Users.AnyAsync(u => u.Id == actor.UserId, ct)) return Array.Empty<string>();
        var userId = actor.UserId;
        if (userId == RequireRole.BuiltInProductOwnerId)
            return await control.Products.AsNoTracking().Select(p => p.Id).ToListAsync(ct);
        var grants = await control.UserProductRoles.AsNoTracking().Where(r => r.UserId == userId).ToListAsync(ct);
        var ids = grants.Where(r => ForgeCapabilities.Roles.Contains(r.Role)).Select(r => r.ProductId).ToList();
        if (ids.Count == 0) return Array.Empty<string>();
        return await control.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
    }

    /// <summary>Other existing product writes retain their PO requirement, without a global-role fallback.</summary>
    public async Task<bool> IsProductOwnerAsync(ForgeActor? actor, string? productId, CancellationToken ct = default)
    {
        if (actor is null || !actors.IsCurrent(actor) || !await control.Users.AnyAsync(u => u.Id == actor.UserId, ct)) return false;
        var product = await this.ResolveProductAsync(productId, ct);
        return product is not null && (actor.UserId == RequireRole.BuiltInProductOwnerId || await control.UserProductRoles.AsNoTracking()
            .AnyAsync(r => r.UserId == actor.UserId && r.ProductId == product.Id && r.Role == RequireRole.ProductOwner, ct));
    }

    private static Task<bool> OwnsRepoAsync(ProductDbContext db, string productId, string? repoId, CancellationToken ct) => repoId is null
        ? Task.FromResult(false) : db.Repos.AsNoTracking().AnyAsync(r => r.Id == repoId && r.ProductId == productId, ct);

    private async Task<bool> OwnsWorkItemAsync(ProductDbContext db, string productId, string id, string? spawnId, CancellationToken ct)
    {
        if (id.StartsWith("plan-doc:", StringComparison.Ordinal))
        {
            var lastColon = id.LastIndexOf(':');
            if (lastColon <= "plan-doc:".Length || !id.EndsWith(".md", StringComparison.Ordinal)) return false;
            var parentId = id["plan-doc:".Length..lastColon];
            var key = id[(lastColon + 1)..^3];
            if (!ForgeCapabilities.ArtifactKeys.Contains(key) || (spawnId is not null && spawnId != parentId)) return false;
            var parent = await db.StorySpawns.AsNoTracking().SingleOrDefaultAsync(s => s.Id == parentId, ct);
            return parent is not null && await OwnsRepoAsync(db, productId, parent.RepoId, ct);
        }
        var ws = await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id, ct);
        if (ws is not null) return (spawnId is null || ws.StorySpawnId == spawnId) && await OwnsRepoAsync(db, productId, ws.RepoId, ct);
        var spawn = await db.StorySpawns.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id || s.StoryId == id, ct);
        if (spawn is not null) return (spawnId is null || spawn.Id == spawnId) && await OwnsRepoAsync(db, productId, spawn.RepoId, ct);
        var workItem = await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id, ct);
        return spawnId is null && workItem is not null && await OwnsRepoAsync(db, productId, workItem.RepoId, ct);
    }
}

/// <summary>Provider dispatch propagates a verified actor across product DI scopes, never HTTP headers.</summary>
public static class ForgeActorScope
{
    private static readonly AsyncLocal<(ForgeActor Actor, string ProductId)?> current = new();
    internal static (ForgeActor Actor, string ProductId)? Current => current.Value;
    public static IDisposable Push(ForgeActor actor, string productId)
    {
        var previous = current.Value;
        current.Value = (actor, productId);
        return new Restore(() => current.Value = previous);
    }
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
}

public sealed class ForgeActionContext(IHttpContextAccessor http, ForgeActorResolver resolver, ForgeCapabilityAuthorizer authorizer, ProductDbContext db)
{
    public Task<ForgeActor?> ActorAsync(CancellationToken ct = default) => ForgeActorScope.Current is { } provider
        ? Task.FromResult<ForgeActor?>(provider.Actor)
        : resolver.FromBrowserAsync(http.HttpContext?.User ?? new ClaimsPrincipal(), ct);

    public string? ProductId => ForgeActorScope.Current?.ProductId
        ?? (http.HttpContext is { } ctx ? ProductScope.Read(ctx) ?? ctx.Items[ProductDbContextFactory.SlugItemKey] as string : null);

    public async Task<ForgeActor> RequireAsync(string capability, ForgeActionTarget target, CancellationToken ct = default)
    {
        var actor = await this.ActorAsync(ct);
        var host = capability is ForgeCapabilities.ConnectionManage or ForgeCapabilities.HostAlertManage or ForgeCapabilities.IdentityManage or ForgeCapabilities.LinkSelf;
        if (!host)
        {
            target = target with { ProductId = target.ProductId ?? this.ProductId };
            var product = await authorizer.ResolveProductAsync(target.ProductId, ct);
            if (product is null || db.ProductSlug != product.Slug) throw new ForgeAccessDeniedException();
        }
        await authorizer.RequireAsync(actor, capability, target, ct);
        return actor!;
    }
}
