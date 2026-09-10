// -----------------------------------------------------------------------
// <copyright file="ForgeEndpointCapabilities.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Auth;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed record ForgeEndpointCapability(string Capability, string TargetKind);

/// <summary>Auditable inventory of chat-equivalent web routes, including historical aliases.</summary>
public static class ForgeEndpointCapabilities
{
    public static readonly IReadOnlyDictionary<string, ForgeEndpointCapability> Supported = new Dictionary<string, ForgeEndpointCapability>(StringComparer.Ordinal)
    {
        ["RetryWorkstream"] = new("workstream.retry", "workstream"),
        ["AcceptWorkstream"] = new("workstream.accept", "workstream"),
        ["CancelWorkstream"] = new("workstream.cancel", "workstream"),
        ["RestartWorkstream"] = new("workstream.restart", "workstream"),
        ["PauseStorySpawn"] = new(ForgeCapabilities.SpawnPause, "spawn"),
        ["ResumeStorySpawn"] = new(ForgeCapabilities.SpawnResume, "spawn"),
        ["RetryStorySpawn"] = new(ForgeCapabilities.SpawnResume, "spawn"),
        ["AdvanceStoryPhase"] = new(ForgeCapabilities.SpawnResume, "spawn"),
        ["ResumeStoryArtifact"] = new("artifact.retry", "artifact"),
        ["RetryStoryArtifact"] = new("artifact.retry", "artifact"),
        ["AcceptStoryArtifact"] = new("artifact.accept", "artifact"),
        ["CancelStoryArtifact"] = new("artifact.cancel", "artifact"),
        ["RestartStoryArtifact"] = new("artifact.restart", "artifact"),
        ["AnswerWorkItemQuestions"] = new(ForgeCapabilities.QuestionAnswer, "question"),
        ["DismissWorkItemQuestions"] = new(ForgeCapabilities.QuestionAnswer, "question"),
        ["ResumeWorkItem"] = new("workstream.retry", "execution-alias"),
        ["PatchWorkItemExecution"] = new("workstream.retry", "execution-alias"),
        ["ReconcileWorkItemExecutionStatus"] = new("workstream.retry", "execution-alias"),
        ["ChatBridgeTestSend"] = new(ForgeCapabilities.ConnectionManage, "host"),
        ["ChatBridgeLinkToken"] = new(ForgeCapabilities.LinkSelf, "self"),
        ["ChatBridgeRevokeLink"] = new(ForgeCapabilities.LinkSelf, "self"),
        ["PutMyRole"] = new(ForgeCapabilities.IdentityManage, "host"),
    };

    public static (string Capability, ForgeActionTarget Target) Resolve(ForgeEndpointCapability entry, HttpContext ctx, string? productId)
    {
        string? Route(string key) => ctx.Request.RouteValues.GetValueOrDefault(key)?.ToString();
        var id = Route("id");
        if (entry.TargetKind == "execution-alias" && id?.StartsWith("plan-doc:", StringComparison.Ordinal) == true)
        {
            var colon = id.LastIndexOf(':');
            if (colon > "plan-doc:".Length && id.EndsWith(".md", StringComparison.Ordinal))
                return ("artifact.retry", new(productId, SpawnId: id["plan-doc:".Length..colon], ArtifactKey: id[(colon + 1)..^3]));
        }
        return (entry.Capability, entry.TargetKind switch
        {
            "host" or "self" => new(),
            "workstream" or "execution-alias" => new(productId, WorkstreamId: id),
            "spawn" => new(productId, StoryId: Route("storyId")),
            "artifact" => new(productId, StoryId: Route("storyId"), ArtifactKey: Route("key")?.ToLowerInvariant()),
            "question" => new(productId, WorkItemId: id),
            _ => throw new ForgeAccessDeniedException(),
        });
    }
}

/// <summary>Checks before idempotency/audit so replay and alias routes cannot bypass current authorization.</summary>
public sealed class ForgeEndpointCapabilityMiddleware(RequestDelegate next)
{
    public const string ReadableProductsKey = "Forge.Auth.ReadableProducts";

    public async Task InvokeAsync(HttpContext ctx, ForgeActionContext action, ForgeCapabilityAuthorizer authorizer, ControlDbContext control, RepoLookup repos)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.Ordinal) || path.StartsWith("/api/operator/", StringComparison.Ordinal)
            || path == "/api/health") { await next(ctx); return; }
        var name = ctx.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        var actor = await action.ActorAsync(ctx.RequestAborted);
        var routeProduct = path.StartsWith("/api/products/", StringComparison.Ordinal)
            ? ctx.Request.RouteValues.GetValueOrDefault("id")?.ToString() : null;
        if (routeProduct is not null)
            await authorizer.RequireAsync(actor, ForgeCapabilities.SpawnRead, new(routeProduct), ctx.RequestAborted);
        if (path.StartsWith("/api/repos/", StringComparison.Ordinal) && ctx.Request.RouteValues.GetValueOrDefault("id") is { } repoId)
        {
            var repo = await repos.GetByIdAsync(repoId.ToString()!);
            if (repo?.ProductId is null) throw new ForgeAccessDeniedException();
            await authorizer.RequireAsync(actor, ForgeCapabilities.SpawnRead, new(repo.ProductId), ctx.RequestAborted);
            if (ctx.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && !await authorizer.IsProductOwnerAsync(actor, repo.ProductId, ctx.RequestAborted))
                throw new ForgeAccessDeniedException();
        }
        var readableIds = (await authorizer.ReadableProductIdsAsync(actor, ctx.RequestAborted)).ToList();
        ctx.Items[ReadableProductsKey] = readableIds.Count == 0 ? new HashSet<string>(StringComparer.Ordinal) : (await control.Products.AsNoTracking()
            .Where(p => readableIds.Contains(p.Id)).Select(p => p.Slug).ToListAsync(ctx.RequestAborted)).ToHashSet(StringComparer.Ordinal);
        if (name is not null && ForgeEndpointCapabilities.Supported.TryGetValue(name, out var entry))
        {
            var (capability, target) = ForgeEndpointCapabilities.Resolve(entry, ctx, action.ProductId);
            await authorizer.RequireAsync(actor, capability, target, ctx.RequestAborted);
        }
        else if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method))
        {
            // Self-profile/settings writes cannot change authority. All other
            // unscoped mutations (including future connection/grant APIs) fail
            // closed to host admins, and product writes require an explicit PO.
            var target = new ForgeActionTarget(action.ProductId);
            if (ctx.Request.RouteValues.TryGetValue("storyId", out var storyId) && name != "SpawnStory")
                target = target with { StoryId = storyId?.ToString() };
            else if (ctx.Request.RouteValues.TryGetValue("id", out var itemId))
            {
                if (path.StartsWith("/api/workstreams/", StringComparison.Ordinal)) target = target with { WorkstreamId = itemId?.ToString() };
                if (path.StartsWith("/api/work-items/", StringComparison.Ordinal)) target = target with { WorkItemId = itemId?.ToString() };
            }
            if (target.WorkstreamId is not null || target.WorkItemId is not null || target.StoryId is not null)
                await authorizer.RequireAsync(actor, ForgeCapabilities.SpawnRead, target, ctx.RequestAborted);
            var selfProfile = path is "/api/me/avatar" or "/api/me/display-name";
            var identity = path.StartsWith("/api/operators/", StringComparison.Ordinal) || path is "/api/me/role" or "/api/me/settings"
                || path.StartsWith("/api/admin/", StringComparison.Ordinal) || path.StartsWith("/api/auth/", StringComparison.Ordinal)
                || path.StartsWith("/api/settings/", StringComparison.Ordinal) || path.StartsWith("/api/chat/", StringComparison.Ordinal);
            if (!selfProfile)
            {
                if (identity || string.IsNullOrEmpty(action.ProductId))
                    await authorizer.RequireAsync(actor, ForgeCapabilities.IdentityManage, new(), ctx.RequestAborted);
                else if (!await authorizer.IsProductOwnerAsync(actor, action.ProductId, ctx.RequestAborted))
                    throw new ForgeAccessDeniedException();
            }
        }
        else if (path is not ("/api/me" or "/api/me/settings" or "/api/chat/availability") && name is not ("ListProducts" or "ListAllRepos")
            && action.ProductId is { Length: > 0 } productId && productId != "all")
        {
            // Read membership is explicit; header selection cannot expose a
            // product absent from this operator's current grants.
            var target = new ForgeActionTarget(productId);
            if (ctx.Request.RouteValues.TryGetValue("storyId", out var story)) target = target with { StoryId = story?.ToString() };
            else if (ctx.Request.RouteValues.TryGetValue("id", out var id))
            {
                if (path.StartsWith("/api/workstreams/", StringComparison.Ordinal)) target = target with { WorkstreamId = id?.ToString() };
                else if (path.StartsWith("/api/work-items/", StringComparison.Ordinal)) target = target with { WorkItemId = id?.ToString() };
            }
            await authorizer.RequireAsync(actor, ForgeCapabilities.SpawnRead, target, ctx.RequestAborted);
        }
        // Unresolved detail targets never fall back to the shared control DB.
        if ((action.ProductId is null or "all") &&
            (ctx.Request.RouteValues.ContainsKey("storyId") ||
             ((path.StartsWith("/api/workstreams/", StringComparison.Ordinal) || path.StartsWith("/api/work-items/", StringComparison.Ordinal))
              && ctx.Request.RouteValues.ContainsKey("id")))) throw new ForgeAccessDeniedException();
        await next(ctx);
    }
}
