// -----------------------------------------------------------------------
// <copyright file="PrCommentsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 24 — PR comment thread endpoints with authority enforcement.
/// Agents may not resolve/reopen comments (Q24.5).
/// </summary>
public static class PrCommentsEndpoints
{
    public static WebApplication MapPrCommentsEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/prs/{prId}/comments").WithTags("PrComments");

        grp.MapGet("/", ListComments).WithName("ListPrComments");
        grp.MapGet("/{commentId:long}", GetComment).WithName("GetPrComment");
        grp.MapPost("/", CreateComment).WithName("CreatePrComment");
        grp.MapPatch("/{commentId:long}", EditComment).WithName("EditPrComment");
        grp.MapPost("/{commentId:long}/resolve", ResolveComment).WithName("ResolvePrComment");
        grp.MapPost("/{commentId:long}/reopen", ReopenComment).WithName("ReopenPrComment");

        return app;
    }

    // GET /api/prs/{prId}/comments?state=active&requiresAttention=true&skip=0&take=50
    private static async Task<IResult> ListComments(
        string prId,
        ForgeDbContext db,
        [FromQuery] string? state,
        [FromQuery] bool? requiresAttention,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        if (!await db.Prs.AnyAsync(p => p.Id == prId))
            return Results.Problem(title: $"PR {prId} not found", statusCode: 404);

        var q = db.PrComments.AsNoTracking().Where(c => c.PrId == prId);

        if (!string.IsNullOrEmpty(state))
            q = q.Where(c => c.State == state);

        if (requiresAttention.HasValue)
            q = q.Where(c => c.RequiresAttention == requiresAttention.Value);

        var total = await q.CountAsync();
        var items = await q
            .OrderByDescending(c => c.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

        return Results.Ok(new { total, items });
    }

    // GET /api/prs/{prId}/comments/{commentId}
    private static async Task<IResult> GetComment(
        string prId,
        long commentId,
        ForgeDbContext db)
    {
        var comment = await db.PrComments.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == commentId && c.PrId == prId);
        if (comment is null)
            return Results.Problem(title: $"Comment {commentId} not found on PR {prId}", statusCode: 404);

        var history = await db.PrCommentHistory.AsNoTracking()
            .Where(h => h.CommentId == commentId)
            .OrderBy(h => h.Id)
            .ToListAsync();

        return Results.Ok(new { comment, history });
    }

    public sealed record CreateCommentRequest(string Body, long? ParentCommentId, string? FilePath, int? Line);

    // POST /api/prs/{prId}/comments
    private static async Task<IResult> CreateComment(
        HttpContext ctx,
        string prId,
        [FromBody] CreateCommentRequest req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (string.IsNullOrWhiteSpace(req?.Body))
            return Results.Problem(title: "Body is required", statusCode: 400);

        if (!await db.Prs.AnyAsync(p => p.Id == prId))
            return Results.Problem(title: $"PR {prId} not found", statusCode: 404);

        var userId = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault() ?? "unknown";
        var now = DateTimeOffset.UtcNow;
        var threadId = req.ParentCommentId.HasValue
            ? (await db.PrComments.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.ParentCommentId.Value))?.ThreadId ?? Guid.NewGuid().ToString()
            : Guid.NewGuid().ToString();

        var comment = new PrCommentRow
        {
            PrId = prId,
            ProviderCommentId = $"local-{Guid.NewGuid():N}",
            ThreadId = threadId,
            ParentCommentId = req.ParentCommentId,
            AuthorId = userId,
            AuthorKind = IsAgent(userId) ? "agent" : "human",
            Body = req.Body,
            FilePath = req.FilePath,
            Line = req.Line,
            State = "active",
            CreatedAt = now,
            UpdatedAt = now,
            RequiresAttention = false
        };

        db.PrComments.Add(comment);

        db.PrCommentHistory.Add(new PrCommentHistoryRow
        {
            CommentId = 0, // will fixup below after SaveChanges
            Kind = "opened",
            ToValue = "active",
            ByUserId = userId,
            At = now
        });

        await db.SaveChangesAsync();

        // Fix up history row with the actual comment id
        var historyRow = await db.PrCommentHistory
            .Where(h => h.CommentId == 0 && h.ByUserId == userId && h.Kind == "opened")
            .OrderByDescending(h => h.Id)
            .FirstOrDefaultAsync();
        if (historyRow is not null)
        {
            historyRow.CommentId = comment.Id;
            await db.SaveChangesAsync();
        }

        await hub.Clients.All.SendAsync("pr.comment.created", new { prId, commentId = comment.Id });
        return Results.Created($"/api/prs/{prId}/comments/{comment.Id}", comment);
    }

    public sealed record EditCommentRequest(string Body);

    // PATCH /api/prs/{prId}/comments/{commentId}
    private static async Task<IResult> EditComment(
        HttpContext ctx,
        string prId,
        long commentId,
        [FromBody] EditCommentRequest req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (string.IsNullOrWhiteSpace(req?.Body))
            return Results.Problem(title: "Body is required", statusCode: 400);

        var comment = await db.PrComments.FirstOrDefaultAsync(c => c.Id == commentId && c.PrId == prId);
        if (comment is null)
            return Results.Problem(title: $"Comment {commentId} not found on PR {prId}", statusCode: 404);

        var userId = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault() ?? "unknown";

        var oldBody = comment.Body;
        comment.Body = req.Body;
        comment.UpdatedAt = DateTimeOffset.UtcNow;

        db.PrCommentHistory.Add(new PrCommentHistoryRow
        {
            CommentId = commentId,
            Kind = "edited",
            FromValue = oldBody.Length > 200 ? oldBody[..200] : oldBody,
            ToValue = req.Body.Length > 200 ? req.Body[..200] : req.Body,
            ByUserId = userId,
            At = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
        await hub.Clients.All.SendAsync("pr.comment.edited", new { prId, commentId });
        return Results.Ok(comment);
    }

    // POST /api/prs/{prId}/comments/{commentId}/resolve — 403 when caller is agent (Q24.5)
    private static async Task<IResult> ResolveComment(
        HttpContext ctx,
        string prId,
        long commentId,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var userId = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault() ?? "unknown";
        if (IsAgent(userId))
            return Results.Problem(title: "Agents may not resolve comments", statusCode: 403);

        var comment = await db.PrComments.FirstOrDefaultAsync(c => c.Id == commentId && c.PrId == prId);
        if (comment is null)
            return Results.Problem(title: $"Comment {commentId} not found on PR {prId}", statusCode: 404);

        if (comment.State == "resolved")
            return Results.Ok(comment);

        var oldState = comment.State;
        comment.State = "resolved";
        comment.ResolvedByUserId = userId;
        comment.ResolvedAt = DateTimeOffset.UtcNow;
        comment.UpdatedAt = DateTimeOffset.UtcNow;
        comment.RequiresAttention = false;

        db.PrCommentHistory.Add(new PrCommentHistoryRow
        {
            CommentId = commentId,
            Kind = "resolved",
            FromValue = oldState,
            ToValue = "resolved",
            ByUserId = userId,
            At = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
        await hub.Clients.All.SendAsync("pr.comment.resolved", new { prId, commentId });
        return Results.Ok(comment);
    }

    // POST /api/prs/{prId}/comments/{commentId}/reopen — 403 when caller is agent (Q24.5)
    private static async Task<IResult> ReopenComment(
        HttpContext ctx,
        string prId,
        long commentId,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var userId = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault() ?? "unknown";
        if (IsAgent(userId))
            return Results.Problem(title: "Agents may not reopen comments", statusCode: 403);

        var comment = await db.PrComments.FirstOrDefaultAsync(c => c.Id == commentId && c.PrId == prId);
        if (comment is null)
            return Results.Problem(title: $"Comment {commentId} not found on PR {prId}", statusCode: 404);

        if (comment.State == "active")
            return Results.Ok(comment);

        var oldState = comment.State;
        comment.State = "active";
        comment.ResolvedByUserId = null;
        comment.ResolvedAt = null;
        comment.UpdatedAt = DateTimeOffset.UtcNow;
        comment.RequiresAttention = true;

        db.PrCommentHistory.Add(new PrCommentHistoryRow
        {
            CommentId = commentId,
            Kind = "reopened",
            FromValue = oldState,
            ToValue = "active",
            ByUserId = userId,
            At = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
        await hub.Clients.All.SendAsync("pr.comment.reopened", new { prId, commentId });
        return Results.Ok(comment);
    }

    /// <summary>
    /// Convention: agent user ids are prefixed with "agent:" or match a
    /// known agent id pattern (E1..E8, A1..A8, etc.). This is a lightweight
    /// heuristic — Phase 25 tightens it with identity claims.
    /// </summary>
    private static bool IsAgent(string userId) =>
        userId.StartsWith("agent:", StringComparison.OrdinalIgnoreCase) ||
        userId.StartsWith("E", StringComparison.Ordinal) && userId.Length <= 3 && char.IsDigit(userId[^1]);
}
