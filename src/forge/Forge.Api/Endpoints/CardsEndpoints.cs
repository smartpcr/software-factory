// -----------------------------------------------------------------------
// <copyright file="CardsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Domain;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Phase 1 — CRUD + move endpoints for cards.
/// All write paths fan out a <c>card.changed</c> SignalR event so other
/// connected clients can refresh their boards.
/// </summary>
public static class CardsEndpoints
{
    public static WebApplication MapCardsEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/cards").WithTags("Cards");

        grp.MapGet("/", GetAll).WithName("GetCards");
        grp.MapGet("/{id}", Get).WithName("GetCard");
        grp.MapPost("/", Create).WithName("CreateCard");
        grp.MapPatch("/{id}", Patch).WithName("PatchCard");
        grp.MapPost("/{id}/move", Move).WithName("MoveCard");
        grp.MapPost("/{id}/comment", Comment).WithName("CommentOnCard");
        grp.MapDelete("/{id}", Delete).WithName("DeleteCard");

        return app;
    }

    // GET /api/cards
    private static async Task<IResult> GetAll(HttpContext ctx, ControlDbContext control, ProductDbContext db)
    {
        var scope = await ProductScope.ResolveAsync(ctx, control);
        var q = db.Cards.AsNoTracking().AsQueryable();
        if (!scope.IsAll)
        {
            var ids = scope.RepoIds!;
            q = q.Where(c => c.RepoId != null && ids.Contains(c.RepoId));
        }
        var rows = await q.OrderBy(c => c.Sort).ToListAsync();
        return Results.Ok(rows.Select(CardMapper.ToContract).ToList());
    }

    // GET /api/cards/{id}
    private static async Task<IResult> Get(string id, ControlDbContext control, ProductDbContext db)
    {
        var row = await db.Cards.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        return row is null ? NotFound(id) : Results.Ok(CardMapper.ToContract(row));
    }

    // POST /api/cards
    //
    // Phase 6.1: when `PostTo` is set, the server creates the work item /
    // issue on the named provider FIRST, takes its returned id (e.g. "#42"),
    // and uses that as the row id. Failing-creates-on-provider abort with 502
    // so the caller can decide whether to retry.
    public sealed record CreateCardRequest(Card Card, string? PostTo = null, string? Description = null);

    private static async Task<IResult> Create(
        HttpContext ctx,
        [FromBody] CreateCardRequest req,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        IUserContext user,
        IEnumerable<IForgeProvider> providers)
    {
        if (req?.Card is null)
        {
            return Results.Problem(title: "Card is required", statusCode: 400);
        }

        var card = req.Card;
        if (!string.IsNullOrEmpty(req.PostTo))
        {
            var auth = ForgeUser.RequireWriter(ctx);
            if (auth is not null) return auth;

            var provider = providers.FirstOrDefault(p => p.Kind == req.PostTo);
            if (provider is null) return Results.Problem(title: $"Unknown provider {req.PostTo}", statusCode: 400);
            if (!provider.IsConfigured) return Results.Problem(title: $"Provider {req.PostTo} not configured", statusCode: 400);
            try
            {
                var created = await provider.CreateWorkItemAsync(
                    new NewWorkItemRequest(
                        Title: card.Title,
                        Type: card.Type,
                        AreaPath: card.Epic,
                        StoryPoints: card.Pts == 0 ? null : card.Pts,
                        Description: req.Description,
                        Tags: card.Tags),
                    user.ForProvider(provider.Kind));
                // Use the provider-assigned id so future PATCH calls round-trip.
                card = card with { Id = created.Id, Epic = created.Epic, Type = created.Type };
            }
            catch (Exception ex)
            {
                return Results.Problem(title: $"Provider {req.PostTo} create failed", detail: ex.Message, statusCode: 502);
            }
        }

        if (await db.Cards.AnyAsync(c => c.Id == card.Id))
        {
            return Results.Problem(title: $"Card {card.Id} already exists", statusCode: 409);
        }

        var maxSort = await db.Cards.Select(c => (int?)c.Sort).MaxAsync() ?? -1;
        var row = CardMapper.ApplyContract(new CardRow { Id = card.Id, Sort = maxSort + 1 }, card);
        db.Cards.Add(row);
        await db.SaveChangesAsync();

        var resultCard = CardMapper.ToContract(row);
        await hub.Clients.All.SendAsync("card.changed", new { kind = "created", card = resultCard });
        return Results.Created($"/api/cards/{row.Id}", resultCard);
    }

    // POST /api/cards/{id}/comment — posts to the linked provider's WI/issue.
    public sealed record CommentRequest(string Body, string Provider = "github");

    private static async Task<IResult> Comment(
        HttpContext ctx,
        string id,
        [FromBody] CommentRequest req,
        IUserContext user,
        IEnumerable<IForgeProvider> providers,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        if (string.IsNullOrWhiteSpace(req?.Body))
        {
            return Results.Problem(title: "Body is required", statusCode: 400);
        }
        if (!await db.Cards.AnyAsync(c => c.Id == id))
        {
            return NotFound(id);
        }

        var provider = providers.FirstOrDefault(p => p.Kind == req.Provider);
        if (provider is null) return Results.Problem(title: $"Unknown provider {req.Provider}", statusCode: 400);
        if (!provider.IsConfigured) return Results.Problem(title: $"Provider {req.Provider} not configured", statusCode: 400);
        try
        {
            await provider.CommentOnWorkItemAsync(id, req.Body, user.ForProvider(provider.Kind));
        }
        catch (Exception ex)
        {
            return Results.Problem(title: "Provider comment failed", detail: ex.Message, statusCode: 502);
        }

        await hub.Clients.All.SendAsync("card.commented", new { id, by = user.UserId, provider = req.Provider });
        return Results.Ok(new { ok = true });
    }

    // PATCH /api/cards/{id}.
    //
    // Phase 5.9: callers may pass `IfMatchRemoteRevision` (or an `If-Match`
    // header). When it differs from the row's current `RemoteRevision`, the
    // server returns 409 + the current row so the UI can surface the
    // "remote changed since" badge + discard / overwrite choice. Pass
    // `X-Forge-Overwrite: true` to clobber.
    public sealed record PatchCardRequest(
        string? Title = null,
        int? Pts = null,
        string? Priority = null,
        Agent? Agent = null,
        bool? AgentNull = null,
        IReadOnlyList<string>? Tags = null,
        string? Type = null,
        string? AutoNext = null,
        string? Col = null,
        string? Epic = null,
        string? PostTo = null,
        string? AssigneeUniqueName = null,
        string? IfMatchRemoteRevision = null);

    private static async Task<IResult> Patch(
        string id,
        [FromBody] PatchCardRequest req,
        HttpContext ctx,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        IUserContext user,
        IEnumerable<IForgeProvider> providers)
    {
        var row = await db.Cards.FirstOrDefaultAsync(c => c.Id == id);
        if (row is null) return NotFound(id);

        // Conflict policy. Header beats body so curl callers can use If-Match.
        var ifMatch = ctx.Request.Headers["If-Match"].FirstOrDefault() ?? req.IfMatchRemoteRevision;
        var force = string.Equals(ctx.Request.Headers["X-Forge-Overwrite"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
        if (!force && !string.IsNullOrEmpty(ifMatch) && !string.Equals(ifMatch, row.RemoteRevision, StringComparison.Ordinal))
        {
            return Results.Json(new
            {
                requireConfirm = true,
                title = "Remote changed since",
                detail = $"Card {row.Id} was updated by a provider sync. Re-fetch and merge, or re-send with X-Forge-Overwrite: true to clobber.",
                currentRevision = row.RemoteRevision,
                current = CardMapper.ToContract(row),
            }, statusCode: 409);
        }

        // Phase 6.2 — optional provider propagation. The local row is the source
        // of truth; provider failure aborts so the caller can decide to retry.
        if (!string.IsNullOrEmpty(req.PostTo))
        {
            var auth = ForgeUser.RequireWriter(ctx);
            if (auth is not null) return auth;

            var provider = providers.FirstOrDefault(p => p.Kind == req.PostTo);
            if (provider is null) return Results.Problem(title: $"Unknown provider {req.PostTo}", statusCode: 400);
            if (!provider.IsConfigured) return Results.Problem(title: $"Provider {req.PostTo} not configured", statusCode: 400);

            var update = new UpdateWorkItemRequest(
                Title: req.Title,
                State: req.Col,
                AssigneeUniqueName: req.AssigneeUniqueName,
                StoryPoints: req.Pts,
                Tags: req.Tags);
            try
            {
                await provider.UpdateWorkItemAsync(id, update, user.ForProvider(provider.Kind));
            }
            catch (Exception ex)
            {
                return Results.Problem(title: $"Provider {req.PostTo} update failed", detail: ex.Message, statusCode: 502);
            }
        }

        if (req.Title != null)    row.Title = req.Title;
        if (req.Pts.HasValue)     row.Pts = req.Pts.Value;
        if (req.Priority != null) row.Priority = req.Priority;
        if (req.Type != null)     row.Type = req.Type;
        if (req.Tags != null)     row.TagsJson = CardMapper.Serialize(req.Tags);
        if (req.AutoNext != null) row.AutoNext = req.AutoNext;
        if (req.Col != null)      row.Col = req.Col;
        if (req.Epic != null)     row.Epic = req.Epic;
        if (req.AgentNull == true) row.AgentJson = null;
        else if (req.Agent != null) row.AgentJson = CardMapper.Serialize(req.Agent);

        await db.SaveChangesAsync();
        var card = CardMapper.ToContract(row);
        await hub.Clients.All.SendAsync("card.changed", new { kind = "updated", card });
        return Results.Ok(card);
    }

    // POST /api/cards/{id}/move
    public sealed record MoveCardRequest(string ToCol, bool Force = false);

    public sealed record MoveCardResponse(Card Card, DragPolicyResult Policy);

    private static async Task<IResult> Move(
        string id,
        [FromBody] MoveCardRequest req,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Forge.Action.Card");
        var row = await db.Cards.FirstOrDefaultAsync(c => c.Id == id);
        if (row is null) return NotFound(id);
        if (string.IsNullOrEmpty(req?.ToCol))
        {
            return Results.Problem(title: "toCol is required", statusCode: 400);
        }

        var card = CardMapper.ToContract(row);
        var policy = DragPolicy.Check(row.Col, req.ToCol, card);

        if (!policy.Allow)
        {
            logger.LogInformation("forge.action.card.move.blocked id={Id} from={From} to={To}", id, row.Col, req.ToCol);
            return Results.Problem(
                title: "Move blocked by policy",
                detail: policy.Title ?? "Move not allowed",
                statusCode: 409);
        }
        if (policy.Confirm && !req.Force)
        {
            return Results.Json(
                new MoveCardResponse(card, policy),
                statusCode: 422);
        }

        var fromCol = row.Col;
        row.Col = req.ToCol;
        await db.SaveChangesAsync();

        var moved = CardMapper.ToContract(row);
        logger.LogInformation("forge.action.card.move id={Id} from={From} to={To}", id, fromCol, req.ToCol);
        await hub.Clients.All.SendAsync("card.changed", new { kind = "moved", card = moved });
        return Results.Ok(new MoveCardResponse(moved, policy));
    }

    // DELETE /api/cards/{id}
    private static async Task<IResult> Delete(
        HttpContext ctx,
        string id,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Cards.FirstOrDefaultAsync(c => c.Id == id);
        if (row is null) return NotFound(id);

        db.Cards.Remove(row);
        await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("card.changed", new { kind = "deleted", id });
        return Results.Ok(new { deleted = true, id });
    }

    private static IResult NotFound(string id) =>
        Results.Problem(title: $"Card {id} not found", statusCode: 404);
}
