// -----------------------------------------------------------------------
// <copyright file="CrewsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 10.6 / 10.7 / 10.9 — crew CRUD with role gating.
/// Reads are open to any signed-in role (so the UI can render crew chips
/// across the board); writes require <c>product-owner</c> per Phase 10.9.
/// </summary>
public static class CrewsEndpoints
{
    public static WebApplication MapCrewsEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/crews").WithTags("Crews");
        grp.MapGet("/", List).WithName("ListCrews");
        grp.MapGet("/{id}", Get).WithName("GetCrew");
        grp.MapPost("/", Create).WithName("CreateCrew");
        grp.MapPatch("/{id}", Patch).WithName("PatchCrew");
        grp.MapDelete("/{id}", Delete).WithName("DeleteCrew");
        grp.MapGet("/{id}/budget", GetBudget).WithName("GetCrewBudget");
        grp.MapGet("/{id}/salary", GetSalary).WithName("GetCrewSalary");
        grp.MapGet("/salary/today", GetSalaryTodayTotal).WithName("GetSalaryTodayTotal");

        // Phase 13.3 — lead-routing (pair-aware).
        grp.MapPost("/{id}/assign", Assign).WithName("AssignCrewTask");
        // Phase 13b — roster CRUD.
        grp.MapPost("/{id}/members", AddMember).WithName("AddCrewMember");
        grp.MapPatch("/{id}/members/{agentId}", PatchMember).WithName("PatchCrewMember");
        grp.MapDelete("/{id}/members/{agentId}", RemoveMember).WithName("RemoveCrewMember");
        // Phase 13.8 — capability ingestion.
        grp.MapPost("/{id}/capabilities", AddCapability).WithName("AddCrewCapability");
        grp.MapDelete("/{id}/capabilities", RemoveCapability).WithName("RemoveCrewCapability");
        // Phase 16.2 — pair management.
        grp.MapPost("/{id}/pair", PairCrew).WithName("PairCrew");
        grp.MapDelete("/{id}/pair", UnpairCrew).WithName("UnpairCrew");
        // Phase 13.1 — history readout.
        grp.MapGet("/{id}/history", GetHistory).WithName("GetCrewHistory");
        // Phase 13.4 / 13.5 / 13.7 — pipelines (server stubs; behaviour TBD).
        grp.MapPost("/{id}/plan", PlanStub).WithName("RunCrewPlan");
        grp.MapPost("/{id}/code", CodeStub).WithName("RunCrewCode");
        // Phase 13c — auto-assign unassigned agents to their team's crew.
        grp.MapPost("/auto-assign", AutoAssign).WithName("AutoAssignAgents");
        // Phase 13d — domain-expert index (also reachable as a top-level route).
        app.MapGet("/api/domain-experts", ListDomainExperts).WithTags("Crews").WithName("ListDomainExperts");
        return app;
    }

    /// <summary>
    /// Phase 13d — list every <see cref="DomainExpertRow"/> the server knows
    /// about. Used by the capability picker in the UI to resolve content-ids
    /// (sha256:… or team:…) into human-readable topic labels. Optionally
    /// filtered by repo or team-prefix; default returns everything.
    /// </summary>
    private static async Task<IResult> ListDomainExperts(
        ForgeDbContext db,
        string? repoId,
        string? prefix,
        CancellationToken ct)
    {
        var q = db.DomainExperts.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(repoId)) q = q.Where(e => e.RepoId == repoId);
        if (!string.IsNullOrWhiteSpace(prefix)) q = q.Where(e => e.ContentId.StartsWith(prefix));
        var rows = await q.OrderBy(e => e.Topic).ToListAsync(ct);
        return Results.Ok(rows.Select(e => new Forge.Api.Contracts.DomainExpert(
            e.ContentId, e.RepoId, e.Path, e.Topic, e.Summary, e.IndexedAt)).ToList());
    }

    private static async Task<IResult> GetSalary(string id, ForgeDbContext db, CancellationToken ct)
    {
        var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);

        var rows = await db.AgentUsage.AsNoTracking()
            .Where(r => r.CrewId == id)
            .Select(r => new { r.StartedAt, r.CostUsd })
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var startOfDay = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var startOfMonth = new DateTimeOffset(new DateTime(now.UtcDateTime.Year, now.UtcDateTime.Month, 1), TimeSpan.Zero);

        // ISO sprint = the current Monday (0-indexed week start).
        var dayOfWeek = (int)now.UtcDateTime.DayOfWeek;
        var daysSinceMonday = (dayOfWeek + 6) % 7; // Sunday=0 → 6, Monday=1 → 0
        var startOfSprint = startOfDay.AddDays(-daysSinceMonday);

        decimal Sum(DateTimeOffset bound) => rows.Where(r => r.StartedAt >= bound).Sum(r => r.CostUsd);

        // 14-day sparkline, oldest → today.
        var sparkline = new decimal[14];
        for (var i = 0; i < 14; i++)
        {
            var dayStart = startOfDay.AddDays(-(13 - i));
            var dayEnd = dayStart.AddDays(1);
            sparkline[i] = rows.Where(r => r.StartedAt >= dayStart && r.StartedAt < dayEnd).Sum(r => r.CostUsd);
        }

        return Results.Ok(new Forge.Api.Contracts.CrewSalary(
            CrewId: id,
            Today: Round(Sum(startOfDay)),
            Sprint: Round(Sum(startOfSprint)),
            Month: Round(Sum(startOfMonth)),
            Sparkline: sparkline.Select(Round).ToList()));
    }

    private static async Task<IResult> GetSalaryTodayTotal(ForgeDbContext db, CancellationToken ct)
    {
        var startOfDay = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        var rows = await db.AgentUsage.AsNoTracking()
            .Select(r => new { r.StartedAt, r.CostUsd, r.Status })
            .ToListAsync(ct);
        var todays = rows.Where(r => r.StartedAt >= startOfDay).ToList();
        var total = todays.Sum(r => r.CostUsd);
        var active = todays.Count(r => r.Status == "running");
        return Results.Ok(new Forge.Api.Contracts.SalaryTodaySummary(Round(total), active));
    }

    private static decimal Round(decimal v) => Math.Round(v, 6);

    private static async Task<IResult> GetBudget(string id, ForgeDbContext db, CancellationToken ct)
    {
        var crew = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);
        var spent = await TodaysSpendAsync(db, id, ct);
        var remaining = crew.DailyBudgetUsd is decimal cap ? cap - spent : (decimal?)null;
        return Results.Ok(new Forge.Api.Contracts.CrewBudget(
            CrewId: id,
            DailyBudgetUsd: crew.DailyBudgetUsd,
            MaxTaskBudgetUsd: crew.MaxTaskBudgetUsd,
            SpentTodayUsd: Math.Round(spent, 6),
            RemainingUsd: remaining is decimal r ? Math.Round(r, 6) : null));
    }

    /// <summary>
    /// Sum today's <see cref="AgentUsageRow.CostUsd"/> for a crew. The date
    /// filter and the sum both run in memory because (a) SQLite EF cannot
    /// translate <c>DateTimeOffset</c> comparisons, and (b) <c>CostUsd</c>
    /// uses a decimal→double value converter that doesn't push through SUM.
    /// Volume is bounded (one crew × one day), so this is cheap.
    /// </summary>
    public static async Task<decimal> TodaysSpendAsync(ForgeDbContext db, string crewId, CancellationToken ct)
    {
        var startOfDay = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        var rows = await db.AgentUsage.AsNoTracking()
            .Where(r => r.CrewId == crewId)
            .Select(r => new { r.StartedAt, r.CostUsd })
            .ToListAsync(ct);
        return rows.Where(r => r.StartedAt >= startOfDay).Sum(r => r.CostUsd);
    }

    private static async Task<IResult> List(ForgeDbContext db, string? productId, CancellationToken ct)
    {
        var q = db.Crews.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(productId)) q = q.Where(c => c.ProductId == productId);
        var rows = await q.OrderBy(c => c.Sort).ThenBy(c => c.Name).ToListAsync(ct);

        // Inflate the roster in a single round-trip rather than N+1. Sort
        // client-side because SQLite EF can't translate ORDER BY DateTimeOffset.
        var ids = rows.Select(r => r.Id).ToHashSet();
        var memberRows = await db.CrewMembers.AsNoTracking()
                .Where(m => ids.Contains(m.CrewId))
                .Select(m => new { m.CrewId, m.AgentId, m.Role, m.AddedAt, m.AddedBy })
                .ToListAsync(ct);
        var memberLookup = memberRows
            .OrderBy(m => m.AddedAt)
            .GroupBy(m => m.CrewId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CrewMember>)g
                    .Select(m => new CrewMember(m.AgentId, m.Role, m.AddedAt, m.AddedBy))
                    .ToList());

        return Results.Ok(rows
            .Select(r => MapCrew(r) with { Roster = memberLookup.TryGetValue(r.Id, out var m) ? m : Array.Empty<CrewMember>() })
            .ToList());
    }

    private static async Task<IResult> Get(string id, ForgeDbContext db, CancellationToken ct)
    {
        var row = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return row is null
            ? Results.Problem(title: $"Crew {id} not found", statusCode: 404)
            : Results.Ok(await MapCrewAsync(row, db, ct));
    }

    public sealed record CreateCrewRequest(
        string Id,
        string Name,
        string? ProductId,
        string? Summary,
        string? Lead,
        string? Model,
        decimal? ModelInputPricePerMTok,
        decimal? ModelOutputPricePerMTok,
        decimal? DailyBudgetUsd = null,
        decimal? MaxTaskBudgetUsd = null,
        string? ParentCrewId = null,
        string? Backend = null,
        string? InvokeMode = null,
        int? MaxConcurrent = null,
        int? WeeklyCap = null,
        // Phase 18.4 — initial shift band ('day' default, or 'night').
        string? Shift = null);

    private static async Task<IResult> Create(
        HttpContext ctx,
        [FromBody] CreateCrewRequest req,
        ForgeDbContext db,
        CancellationToken ct)
    {
        // Phase 13.2 — creation is a product-scoped PO action. Falls back to
        // global PO when the body has no productId.
        var gate = RequireRole.CheckForProduct(ctx, db, req?.ProductId, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        if (req is null || string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Name))
        {
            return Results.Problem(title: "id and name are required", statusCode: 400);
        }
        if (await db.Crews.AnyAsync(c => c.Id == req.Id, ct))
        {
            return Results.Problem(title: $"Crew {req.Id} already exists", statusCode: 409);
        }

        // Phase 13.1 — depth = parent.Depth + 1, capped at 2 (Org → Team → Squad).
        var depth = 0;
        if (!string.IsNullOrEmpty(req.ParentCrewId))
        {
            var parent = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.ParentCrewId, ct);
            if (parent is null) return Results.Problem(title: $"Parent crew {req.ParentCrewId} not found", statusCode: 400);
            if (parent.Depth >= 2) return Results.Problem(title: "Hierarchy depth cap is 3 (Org → Team → Squad)", statusCode: 400);
            depth = parent.Depth + 1;
        }

        var backend  = NormaliseBackend(req.Backend) ?? "copilot";
        var mode     = NormaliseMode(req.InvokeMode) ?? "sdk";
        // Phase 18.4 — explicit unknown-value rejection. When Shift is
        // present but neither 'day' nor 'night', refuse 400 rather than
        // silently coercing to 'day' (the create path otherwise would).
        if (req.Shift is not null && NormaliseShift(req.Shift) is null)
        {
            return Results.Problem(
                type: "crew.unknown_shift",
                title: $"Unknown shift '{req.Shift}'",
                detail: "Allowed values: 'day' | 'night'.",
                statusCode: 400);
        }
        var shift = NormaliseShift(req.Shift) ?? "day";

        var maxSort = await db.Crews.Select(c => (int?)c.Sort).MaxAsync(ct) ?? -1;
        var row = new CrewRow
        {
            Id = req.Id,
            Sort = maxSort + 1,
            Name = req.Name,
            ProductId = req.ProductId,
            Summary = req.Summary ?? string.Empty,
            Lead = req.Lead,
            Model = string.IsNullOrWhiteSpace(req.Model) ? "claude-opus-4-7" : req.Model,
            ModelInputPricePerMTok = req.ModelInputPricePerMTok ?? 15m,
            ModelOutputPricePerMTok = req.ModelOutputPricePerMTok ?? 75m,
            DailyBudgetUsd = req.DailyBudgetUsd,
            MaxTaskBudgetUsd = req.MaxTaskBudgetUsd,
            ParentCrewId = string.IsNullOrEmpty(req.ParentCrewId) ? null : req.ParentCrewId,
            Depth = depth,
            Backend = backend,
            InvokeMode = mode,
            MaxConcurrent = req.MaxConcurrent ?? 2,
            WeeklyCap = req.WeeklyCap ?? 20,
            Shift = shift,
        };
        db.Crews.Add(row);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/crews/{row.Id}", await MapCrewAsync(row, db, ct));
    }

    public sealed record PatchCrewRequest(
        string? Name = null,
        string? Summary = null,
        string? Lead = null,
        string? Model = null,
        decimal? ModelInputPricePerMTok = null,
        decimal? ModelOutputPricePerMTok = null,
        decimal? DailyBudgetUsd = null,
        decimal? MaxTaskBudgetUsd = null,
        string? ParentCrewId = null,
        bool? ClearParentCrewId = null,
        string? Backend = null,
        string? InvokeMode = null,
        int? MaxConcurrent = null,
        int? WeeklyCap = null,
        // Phase 13b — default pair (generator + evaluator). Empty string clears.
        string? DefaultGeneratorAgentId = null,
        string? DefaultEvaluatorAgentId = null,
        string? EvaluatorModel = null,
        // Phase 18.4 — flip the crew between 'day' and 'night' bands.
        string? Shift = null);

    private static async Task<IResult> Patch(
        HttpContext ctx,
        string id,
        [FromBody] PatchCrewRequest req,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var row = await db.Crews.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);

        // Phase 13.2 — product-scoped PO. The crew's home product is the gate;
        // a PO of a different product cannot mutate even if their TopBar is on
        // this crew's product.
        var gate = RequireRole.CheckForProduct(ctx, db, row.ProductId, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();

        if (req.Name is not null) row.Name = req.Name;
        if (req.Summary is not null) row.Summary = req.Summary;
        if (req.Lead is not null)
        {
            var newLead = string.IsNullOrWhiteSpace(req.Lead) ? null : req.Lead;
            if (newLead != row.Lead)
            {
                db.CrewHistory.Add(new CrewHistoryRow { CrewId = id, Kind = "lead", FromValue = row.Lead, ToValue = newLead, ByUserId = caller });
                row.Lead = newLead;
            }
        }
        if (req.Model is not null && req.Model != row.Model)
        {
            db.CrewHistory.Add(new CrewHistoryRow { CrewId = id, Kind = "model", FromValue = row.Model, ToValue = req.Model, ByUserId = caller });
            row.Model = req.Model;
        }
        if (req.ModelInputPricePerMTok is decimal i) row.ModelInputPricePerMTok = i;
        if (req.ModelOutputPricePerMTok is decimal o) row.ModelOutputPricePerMTok = o;
        if (req.DailyBudgetUsd is decimal db1) row.DailyBudgetUsd = db1;
        if (req.MaxTaskBudgetUsd is decimal mt) row.MaxTaskBudgetUsd = mt;
        if (req.MaxConcurrent is int mc) row.MaxConcurrent = mc;
        if (req.WeeklyCap is int wc) row.WeeklyCap = wc;

        // Phase 13.4 / 13.1 — backend & invoke-mode are crew-level and audited.
        if (req.Backend is not null)
        {
            var nb = NormaliseBackend(req.Backend);
            if (nb is null) return Results.Problem(title: $"Unknown backend '{req.Backend}'", statusCode: 400);
            if (nb != row.Backend)
            {
                db.CrewHistory.Add(new CrewHistoryRow { CrewId = id, Kind = "backend", FromValue = row.Backend, ToValue = nb, ByUserId = caller });
                row.Backend = nb;
            }
        }
        if (req.InvokeMode is not null)
        {
            var nm = NormaliseMode(req.InvokeMode);
            if (nm is null) return Results.Problem(title: $"Unknown invoke mode '{req.InvokeMode}'", statusCode: 400);
            if (nm != row.InvokeMode)
            {
                db.CrewHistory.Add(new CrewHistoryRow { CrewId = id, Kind = "mode", FromValue = row.InvokeMode, ToValue = nm, ByUserId = caller });
                row.InvokeMode = nm;
            }
        }

        // Phase 18.4 — shift band ('day' | 'night'). Whitelist + audit row.
        if (req.Shift is not null)
        {
            var ns = NormaliseShift(req.Shift);
            if (ns is null)
            {
                return Results.Problem(
                    type: "crew.unknown_shift",
                    title: $"Unknown shift '{req.Shift}'",
                    detail: "Allowed values: 'day' | 'night'.",
                    statusCode: 400);
            }
            if (ns != row.Shift)
            {
                db.CrewHistory.Add(new CrewHistoryRow { CrewId = id, Kind = "shift", FromValue = row.Shift, ToValue = ns, ByUserId = caller });
                row.Shift = ns;
            }
        }

        // Phase 13b — default-pair updates. Empty string clears the field;
        // null leaves it alone (unset = "use the lead").
        if (req.DefaultGeneratorAgentId is not null)
        {
            var next = string.IsNullOrWhiteSpace(req.DefaultGeneratorAgentId) ? null : req.DefaultGeneratorAgentId;
            if (next != row.DefaultGeneratorAgentId)
            {
                db.CrewHistory.Add(new CrewHistoryRow { CrewId = id, Kind = "default-generator", FromValue = row.DefaultGeneratorAgentId, ToValue = next, ByUserId = caller });
                row.DefaultGeneratorAgentId = next;
            }
        }
        if (req.DefaultEvaluatorAgentId is not null)
        {
            var next = string.IsNullOrWhiteSpace(req.DefaultEvaluatorAgentId) ? null : req.DefaultEvaluatorAgentId;
            if (next != row.DefaultEvaluatorAgentId)
            {
                db.CrewHistory.Add(new CrewHistoryRow { CrewId = id, Kind = "default-evaluator", FromValue = row.DefaultEvaluatorAgentId, ToValue = next, ByUserId = caller });
                row.DefaultEvaluatorAgentId = next;
            }
        }
        if (req.EvaluatorModel is not null)
        {
            var next = string.IsNullOrWhiteSpace(req.EvaluatorModel) ? null : req.EvaluatorModel;
            // Diff-model rule: when both halves of the pair have a model
            // pinned, they must differ. Empty string clears.
            if (!string.IsNullOrEmpty(next) && next == row.Model)
            {
                return Results.Problem(
                    type: "crew.pair_models_must_differ",
                    title: "Generator and evaluator must use different models",
                    detail: $"evaluatorModel='{next}' equals model='{row.Model}'. Pick a different model for the evaluator (the pair-review pass must run on a different model than the generator).",
                    statusCode: 400);
            }
            if (next != row.EvaluatorModel)
            {
                db.CrewHistory.Add(new CrewHistoryRow { CrewId = id, Kind = "evaluator-model", FromValue = row.EvaluatorModel, ToValue = next, ByUserId = caller });
                row.EvaluatorModel = next;
            }
        }
        // If the generator model just changed and would now collide with the
        // evaluator model, refuse rather than silently leaving the pair invalid.
        if (req.Model is not null && !string.IsNullOrEmpty(row.EvaluatorModel) && row.Model == row.EvaluatorModel)
        {
            return Results.Problem(
                type: "crew.pair_models_must_differ",
                title: "Generator model collides with evaluator model",
                detail: $"model='{row.Model}' equals evaluatorModel='{row.EvaluatorModel}'. Update both fields together so the pair stays diverse.",
                statusCode: 400);
        }

        // Phase 13.1 — re-parenting (drag-drop in /crews). Depth re-derives.
        if (req.ClearParentCrewId == true)
        {
            if (row.ParentCrewId is not null)
            {
                db.CrewHistory.Add(new CrewHistoryRow { CrewId = id, Kind = "parent-change", FromValue = row.ParentCrewId, ToValue = null, ByUserId = caller });
            }
            row.ParentCrewId = null;
            row.Depth = 0;
        }
        else if (req.ParentCrewId is not null && req.ParentCrewId != row.ParentCrewId)
        {
            if (req.ParentCrewId == row.Id) return Results.Problem(title: "A crew cannot be its own parent", statusCode: 400);
            var parent = await db.Crews.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.ParentCrewId, ct);
            if (parent is null) return Results.Problem(title: $"Parent crew {req.ParentCrewId} not found", statusCode: 400);
            if (parent.Depth >= 2) return Results.Problem(title: "Hierarchy depth cap is 3 (Org → Team → Squad)", statusCode: 400);
            db.CrewHistory.Add(new CrewHistoryRow { CrewId = id, Kind = "parent-change", FromValue = row.ParentCrewId, ToValue = req.ParentCrewId, ByUserId = caller });
            row.ParentCrewId = req.ParentCrewId;
            row.Depth = parent.Depth + 1;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(await MapCrewAsync(row, db, ct));
    }

    private static async Task<IResult> Delete(HttpContext ctx, string id, ForgeDbContext db, CancellationToken ct)
    {
        var row = await db.Crews.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);
        var gate = RequireRole.CheckForProduct(ctx, db, row.ProductId, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        // Clear the paired crew's reference before removing so the peer
        // doesn't end up with a stale PairedWithCrewId pointing to a
        // deleted row.
        if (!string.IsNullOrEmpty(row.PairedWithCrewId))
        {
            var peer = await db.Crews.FirstOrDefaultAsync(c => c.Id == row.PairedWithCrewId, ct);
            if (peer is not null)
            {
                peer.PairRole = "standalone";
                peer.PairedWithCrewId = null;
                var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
                db.CrewHistory.Add(new CrewHistoryRow { CrewId = peer.Id, Kind = "crew-unpaired", FromValue = row.Id, ByUserId = caller });
            }
        }

        db.Crews.Remove(row);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ── Phase 13.3 — assign ────────────────────────────────────────────────

    private static async Task<IResult> Assign(
        HttpContext ctx,
        string id,
        [FromBody] AssignTaskRequest req,
        ForgeDbContext db,
        ITaskQueueService taskQueue,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var crew = await db.Crews.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);

        // Lead OR product-scoped PO (Q13.2). Self-assignment is fine.
        // Single-user-dev pass-through: when no users are signed in (fresh DB),
        // RequireRole returns null and we allow both the gate and the
        // ?force=true path — matches the convention in Check / CheckForProduct
        // so dev / contract tests can drive writes.
        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        var role = RequireRole.ResolveRoleForProduct(ctx, db, crew.ProductId);
        var isPo = role is null || string.Equals(role, RequireRole.ProductOwner, StringComparison.OrdinalIgnoreCase);
        if (role is not null)
        {
            var callerUser = string.IsNullOrEmpty(caller) ? null
                : await db.Users.AsNoTracking()
                    .Where(u => u.Email == caller || u.Login == caller || u.DisplayName == caller || u.Id == caller)
                    .Select(u => new { u.Id })
                    .FirstOrDefaultAsync(ct);
            var callerAgentId = callerUser is null ? null
                : await db.Agents.AsNoTracking().Where(a => a.Id == callerUser.Id).Select(a => (string?)a.Id).FirstOrDefaultAsync(ct);
            var isLead = !string.IsNullOrEmpty(crew.Lead) && crew.Lead == callerAgentId;
            if (!isLead && !isPo)
            {
                return Results.Problem(title: $"Forbidden — only the crew lead or product-owner of {crew.ProductId ?? "(no product)"} can assign", statusCode: 403);
            }
        }

        if (string.IsNullOrWhiteSpace(req.WorkItemId))
        {
            return Results.Problem(title: "workItemId is required", statusCode: 400);
        }

        // Resolve the pair: explicit > body MemberAgentId (legacy) > crew defaults > lead.
        var generator = req.GeneratorAgentId
            ?? req.MemberAgentId
            ?? crew.DefaultGeneratorAgentId
            ?? crew.Lead;
        var evaluator = req.EvaluatorAgentId
            ?? crew.DefaultEvaluatorAgentId;

        if (string.IsNullOrWhiteSpace(generator))
        {
            return Results.Problem(title: "No generator agent — set the lead, the crew's defaultGeneratorAgentId, or pass generatorAgentId in the body", statusCode: 400);
        }
        if (!string.IsNullOrEmpty(evaluator) && evaluator == generator)
        {
            return Results.Problem(title: "Generator and evaluator must be different agents", statusCode: 400);
        }

        // Both halves of the pair must be in the roster (or be the lead).
        var rosterIds = await db.CrewMembers.AsNoTracking()
            .Where(m => m.CrewId == id)
            .Select(m => m.AgentId)
            .ToListAsync(ct);
        var rosterSet = rosterIds.ToHashSet();
        bool InCrew(string a) => a == crew.Lead || rosterSet.Contains(a);
        if (!InCrew(generator!))
        {
            return Results.Problem(title: $"Generator {generator} is not in crew {id}'s roster", statusCode: 400);
        }
        if (!string.IsNullOrEmpty(evaluator) && !InCrew(evaluator!))
        {
            return Results.Problem(title: $"Evaluator {evaluator} is not in crew {id}'s roster", statusCode: 400);
        }

        // Phase 13.6 — concurrency cap. Override (?force=true) is product-scoped PO only.
        var active = await db.WorkItems.AsNoTracking()
            .CountAsync(w => w.AssignedToJson != null && w.AssignedToJson.Contains($"\"crewId\":\"{id}\"") && w.State == "active", ct);
        if (active >= crew.MaxConcurrent && !req.Force)
        {
            return Results.Problem(
                type: "agent.crew_concurrency_full",
                title: $"Crew {id} is at MaxConcurrent ({crew.MaxConcurrent}). Use force=true (PO only) to override.",
                statusCode: 409);
        }
        if (active >= crew.MaxConcurrent && req.Force)
        {
            if (!isPo) return Results.Problem(title: "Force-override requires product-scoped PO", statusCode: 403);
            db.CrewHistory.Add(new CrewHistoryRow { CrewId = id, Kind = "quota-override", FromValue = "concurrency", ToValue = $"+1@{active}", ByUserId = caller });
        }

        // Stamp the work item with the resolved pair. The plan/coding pipelines
        // (13.4 / 13.5) consume `generatorAgentId` and `evaluatorAgentId` when
        // they read this back; the legacy `agentId` mirror is kept for older
        // consumers that haven't migrated to the pair shape yet.
        // Phase 16.2: also include evaluatorCrewId when the crew is paired.
        var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == req.WorkItemId, ct);
        if (wi is null) return Results.Problem(title: $"Work item {req.WorkItemId} not found", statusCode: 404);
        var evalJson = string.IsNullOrEmpty(evaluator) ? "null" : $"\"{evaluator}\"";
        var evalCrewId = string.Equals(crew.PairRole, "generator", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(crew.PairedWithCrewId)
            ? crew.PairedWithCrewId
            : null;
        var evalCrewJson = evalCrewId is not null ? $",\"evaluatorCrewId\":\"{evalCrewId}\"" : "";
        wi.AssignedToJson =
            $"{{\"crewId\":\"{id}\",\"agentId\":\"{generator}\",\"generatorAgentId\":\"{generator}\",\"evaluatorAgentId\":{evalJson}{evalCrewJson},\"by\":\"{caller}\"}}";
        await db.SaveChangesAsync(ct);

        // Enqueue into the task queue so the inbox shows the assignment
        // and the queue worker can pick it up for the agent runner.
        try
        {
            var queueRow = await taskQueue.EnqueueAsync(req.WorkItemId, "crew", id, ct);
            await hub.Clients.All.SendAsync("queue.changed", new { kind = "enqueued", item = queueRow }, ct);
        }
        catch (Exception ex)
        {
            // Non-fatal — the assignment is persisted; queue entry is best-effort.
            ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Forge.Crew.Assign")
                .LogWarning(ex, "Queue enqueue failed for {WiId} → crew {CrewId}", req.WorkItemId, id);
        }

        return Results.Ok(new
        {
            workItemId = req.WorkItemId,
            crewId = id,
            generatorAgentId = generator,
            evaluatorAgentId = evaluator,
        });
    }

    // ── Phase 13b — roster CRUD ────────────────────────────────────────────

    public sealed record AddMemberRequest(string AgentId, string? Role = null);

    private static async Task<IResult> AddMember(
        HttpContext ctx,
        string id,
        [FromBody] AddMemberRequest req,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var crew = await db.Crews.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);
        var gate = RequireRole.CheckForProduct(ctx, db, crew.ProductId, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        if (string.IsNullOrWhiteSpace(req.AgentId)) return Results.Problem(title: "agentId required", statusCode: 400);
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == req.AgentId, ct);
        if (agent is null) return Results.Problem(title: $"Agent {req.AgentId} not found", statusCode: 404);

        var existing = await db.CrewMembers.FirstOrDefaultAsync(m => m.CrewId == id && m.AgentId == req.AgentId, ct);
        if (existing is not null)
        {
            return Results.Ok(await MapCrewAsync(crew, db, ct)); // idempotent
        }

        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();

        // Phase 13b — adding an agent is a *move*: an agent can be a roster
        // member of at most one crew at a time. Remove any prior membership
        // (including in other crews of the same product) and bump
        // CrewMemberRow + agents.CrewId so realtime listeners see a
        // single coherent state. Each removal records its own history row so
        // the audit trail tells the full story of the move.
        var priorMemberships = await db.CrewMembers.Where(m => m.AgentId == req.AgentId).ToListAsync(ct);
        foreach (var prior in priorMemberships)
        {
            db.CrewMembers.Remove(prior);
            db.CrewHistory.Add(new CrewHistoryRow
            {
                CrewId = prior.CrewId,
                Kind = "member-move-out",
                FromValue = req.AgentId,
                ToValue = id,
                ByUserId = caller,
            });
        }
        // If the agent was previously the lead of another crew, demote.
        var leadCrews = await db.Crews.Where(c => c.Lead == req.AgentId && c.Id != id).ToListAsync(ct);
        foreach (var lc in leadCrews)
        {
            lc.Lead = null;
            db.CrewHistory.Add(new CrewHistoryRow
            {
                CrewId = lc.Id,
                Kind = "lead-clear-on-move",
                FromValue = req.AgentId,
                ToValue = id,
                ByUserId = caller,
            });
        }

        db.CrewMembers.Add(new CrewMemberRow
        {
            CrewId = id,
            AgentId = req.AgentId,
            Role = string.IsNullOrWhiteSpace(req.Role) ? "coder" : req.Role!,
            AddedBy = caller,
        });

        // Pin the agent's primary CrewId to the new crew so AgentRow.CrewId
        // matches the visible roster (used by `/api/agents`, salary roll-ups,
        // and the OverviewView agent list).
        var agentRow = await db.Agents.FirstOrDefaultAsync(a => a.Id == req.AgentId, ct);
        if (agentRow is not null) agentRow.CrewId = id;

        db.CrewHistory.Add(new CrewHistoryRow
        {
            CrewId = id,
            Kind = priorMemberships.Count > 0 ? "member-move-in" : "member-add",
            FromValue = priorMemberships.Count > 0 ? string.Join(",", priorMemberships.Select(p => p.CrewId)) : null,
            ToValue = req.AgentId,
            ByUserId = caller,
        });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await MapCrewAsync(crew, db, ct));
    }

    public sealed record PatchMemberRequest(string Role);

    private static async Task<IResult> PatchMember(
        HttpContext ctx,
        string id,
        string agentId,
        [FromBody] PatchMemberRequest req,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var crew = await db.Crews.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);
        var gate = RequireRole.CheckForProduct(ctx, db, crew.ProductId, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        var member = await db.CrewMembers.FirstOrDefaultAsync(m => m.CrewId == id && m.AgentId == agentId, ct);
        if (member is null) return Results.Problem(title: $"{agentId} is not a member of {id}", statusCode: 404);

        if (string.IsNullOrWhiteSpace(req.Role)) return Results.Problem(title: "role required", statusCode: 400);
        if (member.Role == req.Role) return Results.Ok(await MapCrewAsync(crew, db, ct));

        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        db.CrewHistory.Add(new CrewHistoryRow { CrewId = id, Kind = "member-role", FromValue = $"{agentId}:{member.Role}", ToValue = $"{agentId}:{req.Role}", ByUserId = caller });
        member.Role = req.Role;
        await db.SaveChangesAsync(ct);
        return Results.Ok(await MapCrewAsync(crew, db, ct));
    }

    private static async Task<IResult> RemoveMember(
        HttpContext ctx,
        string id,
        string agentId,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var crew = await db.Crews.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);
        var gate = RequireRole.CheckForProduct(ctx, db, crew.ProductId, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        var member = await db.CrewMembers.FirstOrDefaultAsync(m => m.CrewId == id && m.AgentId == agentId, ct);
        if (member is null) return Results.Ok(await MapCrewAsync(crew, db, ct));
        db.CrewMembers.Remove(member);
        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        db.CrewHistory.Add(new CrewHistoryRow { CrewId = id, Kind = "member-remove", FromValue = agentId, ByUserId = caller });

        // If this agent was the default generator/evaluator, clear it so the
        // crew doesn't keep pointing at someone who's no longer a member.
        if (crew.DefaultGeneratorAgentId == agentId) crew.DefaultGeneratorAgentId = null;
        if (crew.DefaultEvaluatorAgentId == agentId) crew.DefaultEvaluatorAgentId = null;

        await db.SaveChangesAsync(ct);
        return Results.Ok(await MapCrewAsync(crew, db, ct));
    }

    // ── Phase 13c — auto-assign unassigned agents ──────────────────────────

    private static async Task<IResult> AutoAssign(
        HttpContext ctx,
        ForgeDbContext db,
        CancellationToken ct)
    {
        // Wide-open gate by design: this is a reconcile pass that brings the
        // graph into the expected shape (every agent on a team). Single-user
        // dev runs it without auth (RequireRole is null = pass-through);
        // multi-user prod still requires X-Forge-User per the secret middleware.
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        var actions = await ApplyAutoAssignAsync(db, caller, ct);
        return Results.Ok(new AutoAssignResult(
            Inspected: await db.Agents.CountAsync(ct),
            Added: actions.Count,
            Actions: actions));
    }

    /// <summary>
    /// Phase 13c — reconcile pass shared by the explicit endpoint and the
    /// startup hook. Walks every agent without a crew and adds them to the
    /// crew matching their <see cref="AgentRow.Role"/>:
    ///   <list type="bullet">
    ///     <item><description><c>arch</c> → Architecture crew</description></item>
    ///     <item><description><c>eng</c>  → Engineering crew</description></item>
    ///     <item><description><c>rev</c>  → Review crew</description></item>
    ///     <item><description><c>qa</c>   → QA crew</description></item>
    ///   </list>
    /// Per-crew role alternates between <c>generator</c> and <c>evaluator</c>
    /// so each team ends up with an even split. The crew lead is set when
    /// none exists yet (first added agent of that team becomes the lead).
    /// Idempotent: subsequent runs no-op.
    /// </summary>
    public static async Task<List<AutoAssignAction>> ApplyAutoAssignAsync(
        ForgeDbContext db,
        string? caller,
        CancellationToken ct)
    {
        var actions = new List<AutoAssignAction>();

        // Resolve crews-by-team. Match crew name prefix (Architecture / Engineering / etc.)
        // case-insensitively. If a team's crew doesn't exist this run skips it —
        // ForgeSeed creates the four canonical crews; this stays defensive.
        var crews = await db.Crews.ToListAsync(ct);
        var crewByTeam = new Dictionary<string, CrewRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in crews)
        {
            var team = TeamFromCrewName(c.Name);
            if (team is not null && !crewByTeam.ContainsKey(team)) crewByTeam[team] = c;
        }

        // Build the alternation seed per crew from the existing roster size.
        // If 3 members are already in, the next add is index 3 → evaluator
        // (index 0 = generator, 1 = evaluator, 2 = generator, …).
        var memberCounts = (await db.CrewMembers.AsNoTracking()
                .Select(m => new { m.CrewId })
                .ToListAsync(ct))
            .GroupBy(m => m.CrewId)
            .ToDictionary(g => g.Key, g => g.Count());

        // Walk agents without a crew (CrewMembers row OR being a lead).
        var leadIds = crews.Where(c => !string.IsNullOrEmpty(c.Lead)).Select(c => c.Lead!).ToHashSet();
        var roster = (await db.CrewMembers.AsNoTracking().Select(m => m.AgentId).ToListAsync(ct)).ToHashSet();
        var agents = await db.Agents.OrderBy(a => a.Sort).ThenBy(a => a.Id).ToListAsync(ct);

        foreach (var a in agents)
        {
            if (leadIds.Contains(a.Id)) continue;     // already a lead
            if (roster.Contains(a.Id)) continue;      // already a roster member
            // Phase 13c — also respect the seed's <see cref="AgentRow.CrewId"/>
            // direct binding. Some seeds set CrewId without writing to
            // crew_members (legacy shape), and the existing /api/agents/{id}/reassign
            // endpoint still uses that column. Treating such agents as
            // "already placed" prevents the reconcile pass from yanking them
            // out of their seeded crew.
            if (!string.IsNullOrEmpty(a.CrewId)) continue;
            var team = TeamFromAgentRole(a.Role);
            if (team is null) continue;
            if (!crewByTeam.TryGetValue(team, out var crew)) continue;

            // Step 1 — promote to lead if the crew has none yet.
            if (string.IsNullOrEmpty(crew.Lead))
            {
                crew.Lead = a.Id;
                db.CrewHistory.Add(new CrewHistoryRow { CrewId = crew.Id, Kind = "lead", FromValue = null, ToValue = a.Id, ByUserId = caller });
                actions.Add(new AutoAssignAction(a.Id, crew.Id, "lead"));
                leadIds.Add(a.Id);
                continue;
            }

            // Step 2 — alternate generator / evaluator based on current size.
            var idx = memberCounts.TryGetValue(crew.Id, out var n) ? n : 0;
            var role = idx % 2 == 0 ? "generator" : "evaluator";
            db.CrewMembers.Add(new CrewMemberRow
            {
                CrewId = crew.Id,
                AgentId = a.Id,
                Role = role,
                AddedBy = caller ?? "auto-assign",
            });
            db.CrewHistory.Add(new CrewHistoryRow { CrewId = crew.Id, Kind = "member-add", ToValue = $"{a.Id}:{role}", ByUserId = caller ?? "auto-assign" });
            roster.Add(a.Id);
            memberCounts[crew.Id] = idx + 1;
            actions.Add(new AutoAssignAction(a.Id, crew.Id, role));

            // Step 3 — once the crew has 1 generator + 1 evaluator and no
            // explicit default pair yet, set them as the default. Picks the
            // first pair so future tasks have a working pair without operator
            // intervention. Subsequent runs leave it alone.
            if (crew.DefaultGeneratorAgentId is null || crew.DefaultEvaluatorAgentId is null)
            {
                var members = await db.CrewMembers.AsNoTracking()
                    .Where(m => m.CrewId == crew.Id)
                    .Select(m => new { m.AgentId, m.Role })
                    .ToListAsync(ct);
                // Include the row we just added (not yet committed).
                if (!members.Any(x => x.AgentId == a.Id))
                {
                    members = members.Concat(new[] { new { AgentId = a.Id, Role = role } }).ToList();
                }
                var firstGen = members.FirstOrDefault(x => x.Role == "generator")?.AgentId;
                var firstEval = members.FirstOrDefault(x => x.Role == "evaluator")?.AgentId;
                if (crew.DefaultGeneratorAgentId is null && firstGen is not null)
                {
                    crew.DefaultGeneratorAgentId = firstGen;
                    db.CrewHistory.Add(new CrewHistoryRow { CrewId = crew.Id, Kind = "default-generator", ToValue = firstGen, ByUserId = caller ?? "auto-assign" });
                }
                if (crew.DefaultEvaluatorAgentId is null && firstEval is not null)
                {
                    crew.DefaultEvaluatorAgentId = firstEval;
                    db.CrewHistory.Add(new CrewHistoryRow { CrewId = crew.Id, Kind = "default-evaluator", ToValue = firstEval, ByUserId = caller ?? "auto-assign" });
                }
            }
        }

        // Step 4 — give every crew an EvaluatorModel that differs from its
        // generator model, so the diff-model rule holds out of the box. Pick
        // the first registry model whose vendor matches the crew backend
        // (Copilot crews → "copilot" vendor, Claude → "anthropic"); skip when
        // the user already configured it.
        if (actions.Count > 0)
        {
            foreach (var crew in crews)
            {
                if (!string.IsNullOrEmpty(crew.EvaluatorModel)) continue;
                var pick = PickPairedEvaluatorModel(crew.Backend, crew.Model);
                if (pick is not null && pick != crew.Model)
                {
                    crew.EvaluatorModel = pick;
                    db.CrewHistory.Add(new CrewHistoryRow { CrewId = crew.Id, Kind = "evaluator-model", ToValue = pick, ByUserId = caller ?? "auto-assign" });
                }
            }
        }

        // Step 5 — seed canonical team capabilities. Each team gets a
        // pre-defined skill list (Phase 13 spec): Architecture = plan/design,
        // Engineering = coding/tests/PR, Review = PR + spec review, QA =
        // E2E / functional / stress / benchmark / BDD. Each entry is materialised
        // as a <see cref="DomainExpertRow"/> with a stable team-scoped
        // ContentId (e.g. <c>team:engineering:coding</c>) so the existing
        // capability picker + soul.md prompt-cache prefix work unchanged.
        await EnsureTeamCapabilitiesAsync(db, caller, ct);

        // Step 6 — split each team's roster evenly between day and night crews.
        // For every day crew that has a night sibling (ParentCrewId == day.Id),
        // move the second half of the day roster into the night crew so both
        // shifts have the same member count.
        var nightCrews = crews.Where(c => c.ParentCrewId is not null).ToList();
        foreach (var nightCrew in nightCrews)
        {
            var dayCrew = crews.FirstOrDefault(c => c.Id == nightCrew.ParentCrewId);
            if (dayCrew is null) continue;

            var dayMembers = (await db.CrewMembers
                .Where(m => m.CrewId == dayCrew.Id)
                .ToListAsync(ct))
                .OrderBy(m => m.AddedAt)
                .ToList();
            var nightMembers = await db.CrewMembers
                .Where(m => m.CrewId == nightCrew.Id)
                .ToListAsync(ct);

            // Only rebalance when the night crew is empty (initial distribution)
            // or significantly unbalanced.
            if (nightMembers.Count > 0) continue;
            if (dayMembers.Count < 2) continue;

            var half = (int)Math.Ceiling(dayMembers.Count / 2.0);
            var toMove = dayMembers.Skip(half).ToList();
            foreach (var m in toMove)
            {
                db.CrewMembers.Remove(m);
                db.CrewMembers.Add(new CrewMemberRow
                {
                    CrewId = nightCrew.Id,
                    AgentId = m.AgentId,
                    Role = m.Role,
                    AddedBy = caller ?? "auto-assign",
                });
                db.CrewHistory.Add(new CrewHistoryRow { CrewId = dayCrew.Id, Kind = "member-move-out", ToValue = $"{m.AgentId} → {nightCrew.Id}", ByUserId = caller ?? "auto-assign" });
                db.CrewHistory.Add(new CrewHistoryRow { CrewId = nightCrew.Id, Kind = "member-move-in", ToValue = $"{m.AgentId} ← {dayCrew.Id}", ByUserId = caller ?? "auto-assign" });
            }
        }

        await db.SaveChangesAsync(ct);
        return actions;
    }

    /// <summary>
    /// Phase 13d — canonical capabilities per team. Each tuple is
    /// <c>(content-id-suffix, display topic, summary)</c>; the full ContentId
    /// is built as <c>team:&lt;team-lower&gt;:&lt;suffix&gt;</c>. Idempotent —
    /// writes only the rows that don't already exist (UNIQUE on ContentId).
    /// </summary>
    private static readonly Dictionary<string, (string Slug, string Topic, string Summary)[]> TeamCapabilities = new()
    {
        ["Architecture"] = new[]
        {
            ("plan",   "plan",   "Decompose features into phase → stage → step trees with explicit deliverables."),
            ("design", "design", "ADRs, system diagrams, blast-radius analysis, interface contracts."),
        },
        ["Engineering"] = new[]
        {
            ("coding", "coding", "Implement step-sized work items end-to-end against the workstream's worktree."),
            ("tests",  "tests",  "Unit + integration coverage above the human-gate policy threshold."),
            ("pr",     "PR",     "Author the PR, address review feedback, ride CI to green."),
        },
        ["Review"] = new[]
        {
            ("pr-review",   "PR review",   "Read the diff for correctness, taste, and missed cases. Block merge on changes."),
            ("spec-review", "spec review", "Read the planning MD before the WIT tree commits. Catch under-specified steps."),
        },
        ["QA"] = new[]
        {
            ("e2e-tests",        "E2E tests",                  "Drive the system end-to-end against staged environments."),
            ("functional-tests", "functional tests",           "Verify behaviour matches spec — happy path + edge cases."),
            ("stress-tests",     "stress tests",               "Sustained load + spikes — find where the system breaks."),
            ("benchmark",        "benchmark",                  "Measure latency, throughput, footprint. Track regressions across releases."),
            ("bdd",              "BDD (Reqnroll/Gherkin/k6)",  "End-user spec authored as BDD — Reqnroll feature files, Gherkin scenarios, k6 scripts."),
        },
    };

    /// <summary>
    /// Phase 13d — ensure every crew's team has its canonical capabilities
    /// seeded into <see cref="ForgeDbContext.DomainExperts"/> and appended
    /// to <see cref="CrewRow.CapabilitiesJson"/> when missing. Each call is
    /// idempotent: existing DomainExpert rows are reused (UNIQUE constraint
    /// on ContentId), and capabilities already in the JSON array are skipped.
    /// </summary>
    public static async Task EnsureTeamCapabilitiesAsync(
        ForgeDbContext db,
        string? caller,
        CancellationToken ct)
    {
        // 1) Materialise one DomainExpertRow per (team, capability).
        var existingByContentId = (await db.DomainExperts.AsNoTracking()
                .Select(e => e.ContentId)
                .ToListAsync(ct))
            .ToHashSet();
        foreach (var (team, caps) in TeamCapabilities)
        {
            foreach (var cap in caps)
            {
                var contentId = $"team:{team.ToLowerInvariant()}:{cap.Slug}";
                if (existingByContentId.Contains(contentId)) continue;
                db.DomainExperts.Add(new DomainExpertRow
                {
                    ContentId = contentId,
                    RepoId = "*",   // sentinel: built-in, not tied to a repo
                    Path = string.Empty,
                    Topic = cap.Topic,
                    Summary = cap.Summary,
                });
                existingByContentId.Add(contentId);
            }
        }
        await db.SaveChangesAsync(ct); // commit before crew-side update so the FK-by-content-id is satisfied

        // 2) Append the team's capabilities onto every matching crew's
        // CapabilitiesJson. Skip when already present (preserves operator-added
        // sha256 capabilities that may already be in the array).
        var crews = await db.Crews.ToListAsync(ct);
        var changed = false;
        foreach (var crew in crews)
        {
            var team = TeamFromCrewName(crew.Name);
            if (team is null || !TeamCapabilities.TryGetValue(team, out var caps)) continue;

            var current = ParseCapsJson(crew.CapabilitiesJson);
            var currentSet = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
            var added = false;
            foreach (var cap in caps)
            {
                var contentId = $"team:{team.ToLowerInvariant()}:{cap.Slug}";
                if (currentSet.Contains(contentId)) continue;
                current.Add(contentId);
                currentSet.Add(contentId);
                db.CrewHistory.Add(new CrewHistoryRow { CrewId = crew.Id, Kind = "capability-add", ToValue = contentId, ByUserId = caller ?? "auto-assign" });
                added = true;
            }
            if (added)
            {
                crew.CapabilitiesJson = SerialiseCaps(current);
                changed = true;
            }
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    private static string? TeamFromAgentRole(string role) => role?.ToLowerInvariant() switch
    {
        "arch" or "architect"            => "Architecture",
        "eng"  or "engineer"             => "Engineering",
        "rev"  or "reviewer"             => "Review",
        "qa"   or "qe" or "test"         => "QA",
        _ => null,
    };

    private static string? TeamFromCrewName(string name)
    {
        var n = (name ?? string.Empty).ToLowerInvariant();
        if (n.StartsWith("arch")) return "Architecture";
        if (n.StartsWith("eng"))  return "Engineering";
        if (n.StartsWith("rev"))  return "Review";
        if (n.StartsWith("qa"))   return "QA";
        return null;
    }

    /// <summary>
    /// Pick a model for the evaluator that (a) differs from the generator's
    /// model, and (b) matches the crew's backend vendor where possible.
    /// Cheap heuristic: when the generator is a "copilot" / "anthropic"
    /// model id, pick a different family entry from the same vendor (opus →
    /// sonnet for cost diversity). Returns null when no good pick exists.
    /// </summary>
    private static string? PickPairedEvaluatorModel(string backend, string generatorModel)
    {
        // Copilot ids use dotted-version (claude-opus-4.7); Anthropic ids use
        // hyphen-version (claude-opus-4-7). Pair by backend vendor.
        var b = (backend ?? "copilot").ToLowerInvariant();
        if (b == "copilot")
        {
            return generatorModel switch
            {
                "claude-opus-4.7"     => "claude-sonnet-4.6",
                "claude-opus-4.6"     => "claude-sonnet-4.6",
                "claude-opus-4.6-1m"  => "claude-sonnet-4.6",
                "claude-sonnet-4.6"   => "claude-opus-4.6",
                "claude-sonnet-4.5"   => "claude-opus-4.6",
                _ => "claude-sonnet-4.6",
            };
        }
        // Anthropic SDK path.
        return generatorModel switch
        {
            "claude-opus-4-7"            => "claude-sonnet-4-6",
            "claude-sonnet-4-6"          => "claude-haiku-4-5-20251001",
            "claude-haiku-4-5-20251001"  => "claude-sonnet-4-6",
            _ => "claude-sonnet-4-6",
        };
    }

    // ── Phase 13.8 — capability ingestion ──────────────────────────────────

    private static async Task<IResult> AddCapability(
        HttpContext ctx,
        string id,
        [FromBody] CapabilityRequest req,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var crew = await db.Crews.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);
        var gate = RequireRole.CheckForProduct(ctx, db, crew.ProductId, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        if (string.IsNullOrWhiteSpace(req.ContentId)) return Results.Problem(title: "contentId required", statusCode: 400);
        var exists = await db.DomainExperts.AsNoTracking().AnyAsync(e => e.ContentId == req.ContentId, ct);
        if (!exists) return Results.Problem(title: $"No domain expert indexed at contentId={req.ContentId}", statusCode: 404);

        var caps = ParseCapsJson(crew.CapabilitiesJson);
        if (caps.Contains(req.ContentId)) return Results.Ok(await MapCrewAsync(crew, db, ct));
        caps.Add(req.ContentId);
        crew.CapabilitiesJson = SerialiseCaps(caps);
        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        db.CrewHistory.Add(new CrewHistoryRow { CrewId = id, Kind = "capability-add", ToValue = req.ContentId, ByUserId = caller });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await MapCrewAsync(crew, db, ct));
    }

    private static async Task<IResult> RemoveCapability(
        HttpContext ctx,
        string id,
        [FromBody] CapabilityRequest req,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var crew = await db.Crews.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);
        var gate = RequireRole.CheckForProduct(ctx, db, crew.ProductId, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        var caps = ParseCapsJson(crew.CapabilitiesJson);
        if (!caps.Remove(req.ContentId)) return Results.Ok(await MapCrewAsync(crew, db, ct));
        crew.CapabilitiesJson = SerialiseCaps(caps);
        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        db.CrewHistory.Add(new CrewHistoryRow { CrewId = id, Kind = "capability-remove", FromValue = req.ContentId, ByUserId = caller });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await MapCrewAsync(crew, db, ct));
    }

    // ── Phase 16.2 — pair management ───────────────────────────────────────

    /// <summary>
    /// Phase 16.2 — pair two crews. Generator becomes the caller's crew,
    /// evaluator becomes the body's <c>OtherCrewId</c>. Symmetric write
    /// inside one transaction. Refuses with 400 on team mismatch (only arch
    /// + eng can pair); 409 if either side is already paired or if the two
    /// crews don't share a product.
    /// </summary>
    private static async Task<IResult> PairCrew(
        HttpContext ctx,
        string id,
        [FromBody] PairCrewRequest req,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var crew = await db.Crews.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);
        var gate = RequireRole.CheckForProduct(ctx, db, crew.ProductId, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        if (req is null || string.IsNullOrWhiteSpace(req.OtherCrewId))
        {
            return Results.Problem(title: "otherCrewId required", statusCode: 400);
        }
        if (req.OtherCrewId == id)
        {
            return Results.Problem(title: "A crew cannot pair with itself.", statusCode: 400);
        }
        var other = await db.Crews.FirstOrDefaultAsync(c => c.Id == req.OtherCrewId, ct);
        if (other is null) return Results.Problem(title: $"Crew {req.OtherCrewId} not found", statusCode: 404);

        // Q16.2 — only arch + eng crews may pair. Review and QA stay on the
        // Phase 14 in-crew evaluator path.
        var teamSelf  = TeamFromCrewName(crew.Name);
        var teamOther = TeamFromCrewName(other.Name);
        bool TeamAllowed(string? t) => t is "Architecture" or "Engineering";
        if (!TeamAllowed(teamSelf) || !TeamAllowed(teamOther))
        {
            return Results.Problem(
                type: "crew.pair.team_unsupported",
                title: "Pairing is only supported for Architecture / Engineering crews at v0.1.",
                statusCode: 400);
        }

        // Cross-product pairs are rejected per Q16.2.
        if (crew.ProductId != other.ProductId)
        {
            return Results.Problem(
                type: "crew.pair.cross_product",
                title: "Both crews must share the same product to enter a pair.",
                statusCode: 409);
        }

        // Already paired? Reject — the caller must DELETE first.
        if (!string.IsNullOrEmpty(crew.PairedWithCrewId) && crew.PairedWithCrewId != other.Id)
        {
            return Results.Problem(
                type: "crew.pair.already_paired",
                title: $"Crew {id} is already paired with {crew.PairedWithCrewId}.",
                statusCode: 409);
        }
        if (!string.IsNullOrEmpty(other.PairedWithCrewId) && other.PairedWithCrewId != crew.Id)
        {
            return Results.Problem(
                type: "crew.pair.already_paired",
                title: $"Crew {other.Id} is already paired with {other.PairedWithCrewId}.",
                statusCode: 409);
        }

        // Idempotent: re-pairing the same two crews is a no-op.
        if (crew.PairedWithCrewId == other.Id && other.PairedWithCrewId == crew.Id)
        {
            return Results.Ok(await MapCrewAsync(crew, db, ct));
        }

        crew.PairRole = "generator";
        crew.PairedWithCrewId = other.Id;
        other.PairRole = "evaluator";
        other.PairedWithCrewId = crew.Id;

        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        db.CrewHistory.Add(new CrewHistoryRow { CrewId = crew.Id,  Kind = "crew-paired", ToValue = other.Id, ByUserId = caller });
        db.CrewHistory.Add(new CrewHistoryRow { CrewId = other.Id, Kind = "crew-paired", ToValue = crew.Id,  ByUserId = caller });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await MapCrewAsync(crew, db, ct));
    }

    /// <summary>
    /// Phase 16.2 — break a pair. Both rows are reset to <c>standalone</c>
    /// inside a single transaction so the symmetry stays intact even if
    /// the operator only knows one side.
    /// </summary>
    private static async Task<IResult> UnpairCrew(
        HttpContext ctx,
        string id,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var crew = await db.Crews.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);
        var gate = RequireRole.CheckForProduct(ctx, db, crew.ProductId, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        if (string.IsNullOrEmpty(crew.PairedWithCrewId))
        {
            return Results.Ok(await MapCrewAsync(crew, db, ct)); // already standalone
        }

        var other = await db.Crews.FirstOrDefaultAsync(c => c.Id == crew.PairedWithCrewId, ct);
        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();

        var formerOtherId = crew.PairedWithCrewId;
        crew.PairRole = "standalone";
        crew.PairedWithCrewId = null;
        if (other is not null)
        {
            other.PairRole = "standalone";
            other.PairedWithCrewId = null;
            db.CrewHistory.Add(new CrewHistoryRow { CrewId = other.Id, Kind = "crew-unpaired", FromValue = crew.Id, ByUserId = caller });
        }
        db.CrewHistory.Add(new CrewHistoryRow { CrewId = crew.Id, Kind = "crew-unpaired", FromValue = formerOtherId, ByUserId = caller });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await MapCrewAsync(crew, db, ct));
    }

    // ── Phase 13.1 — history readout ───────────────────────────────────────

    private static async Task<IResult> GetHistory(string id, ForgeDbContext db, CancellationToken ct)
    {
        // SQLite EF can't ORDER BY DateTimeOffset — pull then sort client-side.
        var rows = await db.CrewHistory.AsNoTracking()
            .Where(h => h.CrewId == id)
            .ToListAsync(ct);
        return Results.Ok(rows
            .OrderByDescending(h => h.At)
            .Take(200)
            .Select(h => new CrewHistoryEntry(h.Id, h.At, h.CrewId, h.Kind, h.FromValue, h.ToValue, h.ByUserId))
            .ToList());
    }

    // ── Phase 13.4 / 13.5 — pipelines ──────────────────────────────────────

    public sealed record PlanRequest(string? WorkItemId = null, bool Restart = false, bool SkipSoulPrompt = false);
    public sealed record CodeRequest(string? StepWorkItemId = null);

    private static async Task<IResult> PlanStub(
        HttpContext ctx, string id,
        [FromBody] PlanRequest req,
        ForgeDbContext db, ITaskQueueService taskQueue, IHubContext<ForgeHub> hub, CancellationToken ct)
    {
        var crew = await db.Crews.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);

        // Skip the schedule gate for explicit restart requests — the user
        // manually clicked restart in the live-exec view, so blocking on
        // schedule is counter-productive and breaks the UX.
        if (!req.Restart)
        {
            var sched = await ApplyScheduleGateAsync(ctx, db, crew, kind: "plan", ct);
            if (sched is not null) return sched;
        }

        if (string.Equals(crew.InvokeMode, "cli", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                type: "crew.tools_unavailable_in_cli",
                title: "Plan tasks require invokeMode=sdk; this crew is configured for CLI.",
                statusCode: 400);
        }

        var wiId = req?.WorkItemId;
        if (string.IsNullOrWhiteSpace(wiId))
            return Results.Problem(title: "workItemId is required", statusCode: 400);

        // Phase 21 — restart semantics: clear prior iterations and reset WI
        // state so the guard in RunAgentAsync doesn't short-circuit.
        if (req.Restart)
        {
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == wiId, ct);
            if (wi is not null)
            {
                // Reset WIT state to "active" so it appears in the active column.
                if (wi.State is "in-review" or "resolved" or "closed")
                {
                    wi.State = "active";
                }
                wi.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            // Delete all deliverable rows so the iter counter resets to 0.
            var deliverables = await db.Deliverables
                .Where(d => d.WorkItemId == wiId)
                .ToListAsync(ct);
            if (deliverables.Count > 0)
            {
                db.Deliverables.RemoveRange(deliverables);
                await db.SaveChangesAsync(ct);
            }
        }

        // Enqueue the work item into the task queue so the worker can
        // route it to the agent runner when capacity is available.
        var queueRow = await taskQueue.EnqueueAsync(wiId, "crew", id, ct, skipSoulPrompt: req.SkipSoulPrompt);
        await hub.Clients.All.SendAsync("queue.changed", new { kind = "enqueued", item = queueRow }, ct);

        // Phase 19 — fan out workitem.changed so the ExecBadge transitions
        // out of "completed"/"failed" back to "queued" without a refresh.
        var planWi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == wiId, ct);
        if (planWi is not null)
        {
            await hub.Clients.All.SendAsync(
                "workitem.changed",
                new { kind = "exec-queued", workItem = WorkItemsEndpoints.MapWorkItemRow(planWi) },
                ct);
        }

        return Results.Ok(new { queued = true, queueId = queueRow.Id, workItemId = wiId, crewId = id, pipeline = "plan" });
    }

    private static async Task<IResult> CodeStub(
        HttpContext ctx, string id,
        [FromBody] CodeRequest req,
        ForgeDbContext db, ITaskQueueService taskQueue, IHubContext<ForgeHub> hub, CancellationToken ct)
    {
        var crew = await db.Crews.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crew is null) return Results.Problem(title: $"Crew {id} not found", statusCode: 404);

        var sched = await ApplyScheduleGateAsync(ctx, db, crew, kind: "code", ct);
        if (sched is not null) return sched;

        if (string.Equals(crew.InvokeMode, "cli", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                type: "crew.tools_unavailable_in_cli",
                title: "Coding tasks require invokeMode=sdk; this crew is configured for CLI.",
                statusCode: 400);
        }

        var wiId = req?.StepWorkItemId;
        if (string.IsNullOrWhiteSpace(wiId))
            return Results.Problem(title: "stepWorkItemId is required", statusCode: 400);

        var queueRow = await taskQueue.EnqueueAsync(wiId, "crew", id, ct);
        await hub.Clients.All.SendAsync("queue.changed", new { kind = "enqueued", item = queueRow }, ct);

        var codeWi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == wiId, ct);
        if (codeWi is not null)
        {
            await hub.Clients.All.SendAsync(
                "workitem.changed",
                new { kind = "exec-queued", workItem = WorkItemsEndpoints.MapWorkItemRow(codeWi) },
                ct);
        }

        return Results.Ok(new { queued = true, queueId = queueRow.Id, workItemId = wiId, crewId = id, pipeline = "code" });
    }

    /// <summary>
    /// Phase 14.2 — runs <see cref="ScheduleGate"/> against the crew and
    /// returns a 409 result if off-shift, or null if the dispatch should
    /// proceed. Honors <c>?override=true</c> when the caller is the crew
    /// lead or a product-scoped PO; the override path writes a
    /// <c>dispatch-after-hours</c> row in <c>crew_history</c>.
    /// </summary>
    /// <param name="kind">Either <c>plan</c> or <c>code</c>; recorded in the audit row.</param>
    private static async Task<IResult?> ApplyScheduleGateAsync(
        HttpContext ctx,
        ForgeDbContext db,
        CrewRow crew,
        string kind,
        CancellationToken ct)
    {
        var verdict = ScheduleGate.Evaluate(crew, DateTimeOffset.UtcNow);
        if (verdict.Verdict != ScheduleGate.Verdict.OffShift) return null;

        // Off shift. Allow override only with ?override=true AND lead/PO.
        var wantsOverride = ctx.Request.Query.TryGetValue("override", out var v)
            && (string.Equals(v.ToString(), "true", StringComparison.OrdinalIgnoreCase) || v.ToString() == "1");

        if (!wantsOverride)
        {
            return Results.Problem(
                type: "crew.off_shift",
                title: "Crew is off shift",
                detail: $"Crew '{crew.Id}' is outside its 9-9-6 window in {verdict.Tz.Id}. Local now: {verdict.LocalNow:u}; next on-shift: {verdict.NextTransitionLocal:u}. Pass ?override=true (lead or product-owner only) to dispatch anyway.",
                statusCode: 409);
        }

        // Override requested — require lead OR product-scoped PO.
        var caller = ctx.Request.Headers[ForgeUser.HeaderName].FirstOrDefault();
        var role = RequireRole.ResolveRoleForProduct(ctx, db, crew.ProductId);
        var isPo = role is null || string.Equals(role, RequireRole.ProductOwner, StringComparison.OrdinalIgnoreCase);
        if (role is not null)
        {
            var callerUser = string.IsNullOrEmpty(caller) ? null
                : await db.Users.AsNoTracking()
                    .Where(u => u.Email == caller || u.Login == caller || u.DisplayName == caller || u.Id == caller)
                    .Select(u => new { u.Id })
                    .FirstOrDefaultAsync(ct);
            var callerAgentId = callerUser is null ? null
                : await db.Agents.AsNoTracking().Where(a => a.Id == callerUser.Id).Select(a => (string?)a.Id).FirstOrDefaultAsync(ct);
            var isLead = !string.IsNullOrEmpty(crew.Lead) && crew.Lead == callerAgentId;
            if (!isLead && !isPo)
            {
                return Results.Problem(
                    type: "crew.off_shift.override_forbidden",
                    title: "Forbidden — only the crew lead or product-owner can override the schedule",
                    statusCode: 403);
            }
        }

        // Audit the bypass and let the request proceed.
        db.CrewHistory.Add(new CrewHistoryRow
        {
            At = DateTimeOffset.UtcNow,
            CrewId = crew.Id,
            Kind = "dispatch-after-hours",
            FromValue = kind,
            ToValue = $"local={verdict.LocalNow:u}; tz={verdict.Tz.Id}",
            ByUserId = string.IsNullOrEmpty(caller) ? null : caller,
        });
        await db.SaveChangesAsync(ct);
        return null;
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static async Task<Crew> MapCrewAsync(CrewRow r, ForgeDbContext db, CancellationToken ct)
    {
        // SQLite EF can't translate ORDER BY DateTimeOffset — pull then sort.
        var rows = await db.CrewMembers.AsNoTracking()
            .Where(m => m.CrewId == r.Id)
            .Select(m => new { m.AgentId, m.Role, m.AddedAt, m.AddedBy })
            .ToListAsync(ct);
        var roster = rows
            .OrderBy(m => m.AddedAt)
            .Select(m => new CrewMember(m.AgentId, m.Role, m.AddedAt, m.AddedBy))
            .ToList();
        var caps = ParseCapsJson(r.CapabilitiesJson);
        return new Crew(
            r.Id, r.Name, r.ProductId, r.Summary, r.Lead, r.Model,
            r.ModelInputPricePerMTok, r.ModelOutputPricePerMTok,
            r.DailyBudgetUsd, r.MaxTaskBudgetUsd,
            r.ParentCrewId, r.Depth, r.Backend, r.InvokeMode,
            roster, caps, r.MaxConcurrent, r.WeeklyCap,
            r.DefaultGeneratorAgentId, r.DefaultEvaluatorAgentId, r.EvaluatorModel,
            r.PairRole, r.PairedWithCrewId, r.Shift);
    }

    /// <summary>Synchronous mapper used by callers that already have the row in hand and don't need the roster inflated.</summary>
    private static Crew MapCrew(CrewRow r) => new(
        r.Id, r.Name, r.ProductId, r.Summary, r.Lead, r.Model,
        r.ModelInputPricePerMTok, r.ModelOutputPricePerMTok,
        r.DailyBudgetUsd, r.MaxTaskBudgetUsd,
        r.ParentCrewId, r.Depth, r.Backend, r.InvokeMode,
        Roster: null, Capabilities: ParseCapsJson(r.CapabilitiesJson),
        r.MaxConcurrent, r.WeeklyCap,
        r.DefaultGeneratorAgentId, r.DefaultEvaluatorAgentId, r.EvaluatorModel,
        r.PairRole, r.PairedWithCrewId, r.Shift);

    private static string? NormaliseBackend(string? s) => s?.ToLowerInvariant() switch
    {
        "copilot" => "copilot",
        "claude"  => "claude",
        null or "" => null,
        _ => null,
    };

    private static string? NormaliseMode(string? s) => s?.ToLowerInvariant() switch
    {
        "sdk" => "sdk",
        "cli" => "cli",
        null or "" => null,
        _ => null,
    };

    /// <summary>
    /// Phase 18.4 — case-insensitive whitelist for the <c>shift</c> field.
    /// Returns null for unknown values so the caller can return 400; callers
    /// that want a default should fall back via <c>?? "day"</c>.
    /// </summary>
    private static string? NormaliseShift(string? s) => s?.ToLowerInvariant() switch
    {
        "day" => "day",
        "night" => "night",
        null or "" => null,
        _ => null,
    };

    private static List<string> ParseCapsJson(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string SerialiseCaps(List<string> caps)
        => System.Text.Json.JsonSerializer.Serialize(caps);
}

