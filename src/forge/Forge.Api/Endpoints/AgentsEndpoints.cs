// -----------------------------------------------------------------------
// <copyright file="AgentsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Agents;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Phase 11.7 — start and observe agent invocations. Events fan out to two
/// destinations: the SignalR group <c>agent.{agentId}</c> for live UI streams,
/// and the <c>agent_usage</c> cost ledger on completion.
/// </summary>
public static class AgentsEndpoints
{
    public static WebApplication MapAgentsEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/agents").WithTags("Agents");
        grp.MapGet("/", ListAgents).WithName("ListAgents");
        grp.MapGet("/{agentId}", GetAgent).WithName("GetAgent");
        grp.MapPatch("/{agentId}", PatchAgent).WithName("PatchAgent");
        grp.MapPost("/{agentId}/invocations", StartInvocation).WithName("StartAgentInvocation");
        grp.MapGet("/{agentId}/invocations", ListInvocations).WithName("ListAgentInvocations");
        grp.MapPost("/{agentId}/invocations/{invocationId}/cancel", CancelInvocation).WithName("CancelAgentInvocation");
        grp.MapPost("/{agentId}/pause", Pause).WithName("PauseAgent");
        grp.MapPost("/{agentId}/resume", Resume).WithName("ResumeAgent");
        grp.MapGet("/{agentId}/state", GetState).WithName("GetAgentState");
        // Phase 11.3 — soul.md.
        grp.MapGet("/{agentId}/soul", GetSoul).WithName("GetAgentSoul");
        grp.MapPut("/{agentId}/soul", PutSoul).WithName("PutAgentSoul");
        // Phase 11.8 — reassign + history.
        grp.MapPost("/{agentId}/reassign", Reassign).WithName("ReassignAgent");
        grp.MapGet("/{agentId}/history", GetHistory).WithName("GetAgentHistory");
        return app;
    }

    private static async Task<IResult> ListAgents(ForgeDbContext db, CancellationToken ct)
    {
        var rows = await db.Agents.AsNoTracking().OrderBy(a => a.Sort).ThenBy(a => a.Id).ToListAsync(ct);

        // Compute distinct work-item counts per agent from the usage ledger.
        var taskCounts = await db.AgentUsage.AsNoTracking()
            .Where(u => u.TaskId != null && u.TaskId != "")
            .GroupBy(u => u.AgentId)
            .Select(g => new { AgentId = g.Key, Count = g.Select(u => u.TaskId).Distinct().Count() })
            .ToListAsync(ct);
        var countMap = taskCounts.ToDictionary(x => x.AgentId, x => x.Count);

        return Results.Ok(rows.Select(r =>
        {
            var detail = MapAgent(r);
            countMap.TryGetValue(r.Id, out var wic);
            return new
            {
                detail.Id, detail.Name, detail.Role, detail.CrewId, detail.SoulPath,
                detail.Capabilities, detail.State, detail.Backend, detail.InvokeMode,
                detail.GeneratorModel, detail.EvaluatorModel,
                WorkItemCount = wic,
            };
        }).ToList());
    }

    private static async Task<IResult> GetAgent(string agentId, ForgeDbContext db, CancellationToken ct)
    {
        var row = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct);
        return row is null
            ? Results.Problem(title: $"Agent {agentId} not found", statusCode: 404)
            : Results.Ok(MapAgent(row));
    }

    /// <summary>Phase 13b/e — edit name / role / soulPath + per-agent runtime overrides.</summary>
    private static async Task<IResult> PatchAgent(
        HttpContext ctx,
        string agentId,
        [FromBody] Forge.Api.Contracts.PatchAgentRequest req,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (row is null) return Results.Problem(title: $"Agent {agentId} not found", statusCode: 404);

        if (!string.IsNullOrWhiteSpace(req.Name)) row.Name = req.Name.Trim();
        if (!string.IsNullOrWhiteSpace(req.Role)) row.Role = req.Role.Trim();
        // Empty string clears the soulPath override (revert to %LOCALAPPDATA% default).
        if (req.SoulPath is not null) row.SoulPath = string.IsNullOrWhiteSpace(req.SoulPath) ? null : req.SoulPath.Trim();

        // Phase 13e — per-agent overrides. null = leave alone, "" = clear (inherit crew),
        // non-empty = pin this agent to the override regardless of crew default.
        if (req.Backend is not null)
        {
            var v = NormaliseBackend(req.Backend);
            if (req.Backend != string.Empty && v is null) return Results.Problem(title: $"Unknown backend '{req.Backend}'", statusCode: 400);
            row.Backend = req.Backend == string.Empty ? null : v;
        }
        if (req.InvokeMode is not null)
        {
            var v = NormaliseMode(req.InvokeMode);
            if (req.InvokeMode != string.Empty && v is null) return Results.Problem(title: $"Unknown invoke mode '{req.InvokeMode}'", statusCode: 400);
            row.InvokeMode = req.InvokeMode == string.Empty ? null : v;
        }
        if (req.GeneratorModel is not null)
        {
            row.GeneratorModel = string.IsNullOrWhiteSpace(req.GeneratorModel) ? null : req.GeneratorModel.Trim();
        }
        if (req.EvaluatorModel is not null)
        {
            row.EvaluatorModel = string.IsNullOrWhiteSpace(req.EvaluatorModel) ? null : req.EvaluatorModel.Trim();
        }

        // Same diff-model rule as on Crew: when an agent pins both halves,
        // they must differ. Helps catch typos before they reach a runner.
        if (!string.IsNullOrEmpty(row.GeneratorModel)
            && !string.IsNullOrEmpty(row.EvaluatorModel)
            && row.GeneratorModel == row.EvaluatorModel)
        {
            return Results.Problem(
                type: "agent.pair_models_must_differ",
                title: "Generator and evaluator model overrides must differ",
                detail: $"generatorModel='{row.GeneratorModel}' equals evaluatorModel='{row.EvaluatorModel}'.",
                statusCode: 400);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(MapAgent(row));
    }

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

    private static async Task<IResult> GetSoul(
        string agentId,
        ForgeDbContext db,
        ISoulService soul,
        CancellationToken ct)
    {
        var row = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (row is null) return Results.Problem(title: $"Agent {agentId} not found", statusCode: 404);

        var doc = await soul.ReadAsync(agentId, row.SoulPath, ct);
        var payload = new Forge.Api.Contracts.SoulPayload(
            AgentId: agentId,
            Path: doc?.Path ?? soul.ResolvePath(agentId, row.SoulPath),
            Contents: doc?.Raw ?? string.Empty,
            Capabilities: doc?.Capabilities ?? Array.Empty<string>(),
            Exists: doc is not null);
        return Results.Ok(payload);
    }

    private static async Task<IResult> PutSoul(
        HttpContext ctx,
        string agentId,
        [FromBody] Forge.Api.Contracts.SoulWriteRequest req,
        ForgeDbContext db,
        ISoulService soul,
        CancellationToken ct)
    {
        var gate = RequireRole.Check(ctx, db, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        if (req is null) return Results.Problem(title: "body is required", statusCode: 400);

        var row = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (row is null) return Results.Problem(title: $"Agent {agentId} not found", statusCode: 404);

        await soul.WriteAsync(agentId, row.SoulPath, req.Contents ?? string.Empty, ct);

        // Re-read so the round-trip exposes the parsed capabilities + reflect into the row.
        var doc = await soul.ReadAsync(agentId, row.SoulPath, ct);
        if (doc is not null)
        {
            row.CapabilitiesJson = System.Text.Json.JsonSerializer.Serialize(doc.Capabilities);
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new Forge.Api.Contracts.SoulPayload(
            AgentId: agentId,
            Path: doc?.Path ?? soul.ResolvePath(agentId, row.SoulPath),
            Contents: doc?.Raw ?? req.Contents ?? string.Empty,
            Capabilities: doc?.Capabilities ?? Array.Empty<string>(),
            Exists: true));
    }

    private static async Task<IResult> Reassign(
        HttpContext ctx,
        string agentId,
        [FromBody] Forge.Api.Contracts.ReassignAgentRequest req,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var gate = RequireRole.Check(ctx, db, RequireRole.ProductOwner);
        if (gate is not null) return gate;

        if (req is null || string.IsNullOrWhiteSpace(req.CrewId))
        {
            return Results.Problem(title: "crewId is required", statusCode: 400);
        }

        var row = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (row is null) return Results.Problem(title: $"Agent {agentId} not found", statusCode: 404);

        var newCrew = await db.Crews.FirstOrDefaultAsync(c => c.Id == req.CrewId, ct);
        if (newCrew is null) return Results.Problem(title: $"Crew {req.CrewId} not found", statusCode: 404);

        // Look up old crew's model so the history row captures the diff.
        string? fromModel = null;
        if (!string.IsNullOrEmpty(row.CrewId))
        {
            fromModel = await db.Crews.Where(c => c.Id == row.CrewId).Select(c => c.Model).FirstOrDefaultAsync(ct);
        }

        var fromCrewId = row.CrewId;
        row.CrewId = newCrew.Id;

        var actor = ctx.Request.Headers[Forge.Api.Auth.ForgeUser.HeaderName].FirstOrDefault();
        db.AgentHistory.Add(new AgentHistoryRow
        {
            At = DateTimeOffset.UtcNow,
            AgentId = agentId,
            FromCrewId = fromCrewId,
            ToCrewId = newCrew.Id,
            FromModel = fromModel,
            ToModel = newCrew.Model,
            Actor = actor,
        });
        await db.SaveChangesAsync(ct);

        await hub.Clients.All.SendAsync(
            "agent.reassigned",
            new { agentId, fromCrewId, toCrewId = newCrew.Id, fromModel, toModel = newCrew.Model },
            ct);

        return Results.Ok(MapAgent(row));
    }

    private static async Task<IResult> GetHistory(string agentId, ForgeDbContext db, CancellationToken ct)
    {
        // Id is auto-increment monotonic; ordering by Id gives the same
        // newest-first result as ordering by At, and SQLite can translate it
        // (DateTimeOffset is stored as TEXT and can't be ORDER BY'd in SQL).
        var rows = await db.AgentHistory.AsNoTracking()
            .Where(r => r.AgentId == agentId)
            .OrderByDescending(r => r.Id)
            .Take(200)
            .ToListAsync(ct);
        var mapped = rows.Select(r => new Forge.Api.Contracts.AgentHistoryEntry(
            r.Id, r.At, r.AgentId, r.FromCrewId, r.ToCrewId, r.FromModel, r.ToModel, r.Actor)).ToList();
        return Results.Ok(mapped);
    }

    private static Forge.Api.Contracts.AgentDetail MapAgent(AgentRow r)
    {
        var caps = string.IsNullOrWhiteSpace(r.CapabilitiesJson)
            ? new List<string>()
            : (System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.CapabilitiesJson) ?? new List<string>());
        return new Forge.Api.Contracts.AgentDetail(
            r.Id, r.Name, r.Role, r.CrewId, r.SoulPath, caps, r.State,
            r.Backend, r.InvokeMode, r.GeneratorModel, r.EvaluatorModel);
    }

    public sealed record StartInvocationRequest(
        string TaskId,
        string Prompt,
        string? Model = null,
        string? CrewId = null,
        string? WorktreePath = null,
        string? SoulMd = null,
        IReadOnlyList<string>? AllowedTools = null,
        decimal? MaxTaskBudgetUsd = null);

    private static async Task<IResult> StartInvocation(
        HttpContext ctx,
        string agentId,
        [FromBody] StartInvocationRequest req,
        IAgentRunner runner,
        IModelRegistry models,
        ForgeDbContext db,
        IHubContext<ForgeHub> hub,
        IServiceScopeFactory scopes,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (req is null || string.IsNullOrWhiteSpace(req.Prompt))
        {
            return Results.Problem(title: "prompt is required", statusCode: 400);
        }

        // Resolve crew + model: the crew's denormalised pricing is the
        // ledger's source of truth (per Q3) regardless of whether the caller
        // overrode the model id, so always fetch the crew row when crewId is
        // known. The model id falls back to the crew's default when omitted.
        var crewId = req.CrewId ?? "engineering";
        var crewLookup = await db.Crews
            .Where(c => c.Id == crewId)
            .Select(c => new { c.Model, c.ModelInputPricePerMTok, c.ModelOutputPricePerMTok })
            .FirstOrDefaultAsync(ct);

        var modelId = req.Model ?? crewLookup?.Model ?? "local-stub";

        var model = models.Find(modelId);
        if (model is null)
        {
            return Results.Problem(title: $"Model '{modelId}' not in registry", statusCode: 400);
        }

        var inputPrice = crewLookup?.ModelInputPricePerMTok ?? 0m;
        var outputPrice = crewLookup?.ModelOutputPricePerMTok ?? 0m;
        // If the crew didn't carry pricing, fall back to the registry default.
        if (inputPrice == 0) inputPrice = model.InputPricePerMTok;
        if (outputPrice == 0) outputPrice = model.OutputPricePerMTok;

        // Phase 11.10 — budget pre-flight. Refuse the invocation if today's
        // crew spend already crosses DailyBudgetUsd; we don't try to predict
        // this call's cost beyond a guard band, since output tokens aren't
        // known until streaming finishes.
        var crewBudget = await db.Crews
            .Where(c => c.Id == crewId)
            .Select(c => new { c.DailyBudgetUsd, c.MaxTaskBudgetUsd })
            .FirstOrDefaultAsync(ct);
        if (crewBudget?.DailyBudgetUsd is decimal dailyCap)
        {
            var spent = await CrewsEndpoints.TodaysSpendAsync(db, crewId, ct);
            if (spent >= dailyCap)
            {
                await hub.Clients.All.SendAsync(
                    "agent.budget_exceeded",
                    new { crewId, dailyBudgetUsd = dailyCap, spentTodayUsd = spent, agentId },
                    ct);
                return Results.Problem(
                    title: "Crew daily budget exceeded",
                    detail: $"Crew {crewId} has spent {spent:0.####} USD today, cap is {dailyCap:0.####}.",
                    statusCode: 402);
            }
        }
        var taskCap = req.MaxTaskBudgetUsd ?? crewBudget?.MaxTaskBudgetUsd;

        var context = new AgentContext(
            CrewId: crewId,
            Model: modelId,
            SoulMd: req.SoulMd ?? string.Empty,
            // Empty when the caller didn't supply a working directory — never
            // %TEMP%, which the SDK would auto-index and overflow context.
            WorktreePath: req.WorktreePath ?? string.Empty,
            AllowedTools: req.AllowedTools ?? Array.Empty<string>(),
            MaxTaskBudgetUsd: taskCap);

        var inv = await runner.StartAsync(req.TaskId ?? string.Empty, agentId, req.Prompt, context, CancellationToken.None);

        // Pre-write a "running" ledger row so cost queries see in-flight work.
        var ledger = new AgentUsageRow
        {
            AgentId = agentId,
            CrewId = crewId,
            TaskId = req.TaskId ?? string.Empty,
            Model = modelId,
            StartedAt = DateTimeOffset.UtcNow,
            Status = "running",
        };
        db.AgentUsage.Add(ledger);
        await db.SaveChangesAsync(ct);

        var pumpLogger = loggers.CreateLogger("Forge.Api.Agents.InvocationPump");
        var taskChannel = ctx.RequestServices.GetService<TaskChannel>();
        _ = Task.Run(() => PumpEvents(inv, ledger.Id, hub, scopes, inputPrice, outputPrice, pumpLogger, taskChannel, req.TaskId, crewId), CancellationToken.None);

        return Results.Accepted(
            $"/api/agents/{agentId}/invocations/{inv.InvocationId}",
            new
            {
                invocationId = inv.InvocationId,
                agentId,
                crewId,
                model = modelId,
                taskId = req.TaskId ?? string.Empty,
                ledgerId = ledger.Id,
            });
    }

    private static async Task PumpEvents(
        AgentInvocation inv,
        long ledgerId,
        IHubContext<ForgeHub> hub,
        IServiceScopeFactory scopes,
        decimal inputPrice,
        decimal outputPrice,
        ILogger log,
        TaskChannel? taskChannel = null,
        string? taskId = null,
        string? crewId = null)
    {
        var groupName = $"agent.{inv.AgentId}";
        long inputTokens = 0;
        long outputTokens = 0;
        var status = "finished";
        string? error = null;
        try
        {
            await foreach (var e in inv.Events)
            {
                await hub.Clients.Group(groupName).SendAsync(
                    "agent.event",
                    new
                    {
                        invocationId = inv.InvocationId,
                        agentId = inv.AgentId,
                        kind = e.Kind.ToString().ToLowerInvariant(),
                        at = e.At,
                        text = e.Text,
                        toolName = e.ToolName,
                        toolPayload = e.ToolPayload,
                        error = e.Error,
                    });

                // Also emit to the per-work-item live channel so the Live
                // Execution view picks up events from direct invocations.
                if (taskChannel is not null && !string.IsNullOrEmpty(taskId))
                {
                    var kind = e.Kind switch
                    {
                        AgentEventKind.Token => "task.token",
                        AgentEventKind.ToolCall => "task.tool-call",
                        AgentEventKind.Finished => "task.finished",
                        AgentEventKind.Failed => "task.failed",
                        _ => $"task.{e.Kind.ToString().ToLowerInvariant()}",
                    };
                    await taskChannel.EmitAsync(taskId, kind, new
                    {
                        invocationId = inv.InvocationId,
                        agentId = inv.AgentId,
                        crewId,
                        text = e.Text,
                        toolName = e.ToolName,
                        inputTokens = e.InputTokens,
                        outputTokens = e.OutputTokens,
                        error = e.Error,
                    }, crewId: crewId, ct: default);
                }

                if (e.Kind == AgentEventKind.Finished)
                {
                    inputTokens = e.InputTokens ?? 0;
                    outputTokens = e.OutputTokens ?? 0;
                }
                else if (e.Kind == AgentEventKind.Failed)
                {
                    status = "failed";
                    error = e.Error;
                }
            }
        }
        catch (OperationCanceledException)
        {
            status = "cancelled";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Agent invocation {Id} pump crashed", inv.InvocationId);
            status = "failed";
            error = ex.Message;
        }

        var cost = inputPrice * inputTokens / 1_000_000m + outputPrice * outputTokens / 1_000_000m;

        // The original DbContext was scoped to the inbound request; that scope
        // is gone by the time the pump finishes. Resolve a fresh one.
        using (var scope = scopes.CreateScope())
        {
            var pumpDb = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var row = await pumpDb.AgentUsage.FirstOrDefaultAsync(r => r.Id == ledgerId);
            if (row is not null)
            {
                row.InputTokens = inputTokens;
                row.OutputTokens = outputTokens;
                row.CostUsd = Math.Round(cost, 6);
                row.EndedAt = DateTimeOffset.UtcNow;
                row.Status = status;
                await pumpDb.SaveChangesAsync();
            }
        }

        await hub.Clients.Group(groupName).SendAsync(
            "agent.usage",
            new
            {
                invocationId = inv.InvocationId,
                agentId = inv.AgentId,
                ledgerId,
                inputTokens,
                outputTokens,
                costUsd = Math.Round(cost, 6),
                status,
                error,
            });
    }

    private static async Task<IResult> ListInvocations(
        string agentId,
        ForgeDbContext db,
        int? take,
        CancellationToken ct)
    {
        // Order by Id (monotonic auto-increment) instead of StartedAt because
        // SQLite cannot translate ORDER BY on DateTimeOffset columns.
        var rows = await db.AgentUsage.AsNoTracking()
            .Where(r => r.AgentId == agentId)
            .OrderByDescending(r => r.Id)
            .Take(Math.Clamp(take ?? 50, 1, 500))
            .ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> CancelInvocation(
        HttpContext ctx,
        string agentId,
        string invocationId,
        IAgentRunner runner,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;
        await runner.CancelAsync(invocationId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Pause(
        HttpContext ctx,
        string agentId,
        IAgentRunner runner,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var gate = RequireRole.Check(ctx, db, RequireRole.ProductOwner);
        if (gate is not null) return gate;
        await runner.PauseAsync(agentId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Resume(
        HttpContext ctx,
        string agentId,
        IAgentRunner runner,
        ForgeDbContext db,
        CancellationToken ct)
    {
        var gate = RequireRole.Check(ctx, db, RequireRole.ProductOwner);
        if (gate is not null) return gate;
        await runner.ResumeAsync(agentId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetState(
        string agentId,
        IAgentRunner runner,
        CancellationToken ct)
    {
        var state = await runner.GetStateAsync(agentId, ct);
        return Results.Ok(new { agentId, state = state.ToString().ToLowerInvariant() });
    }
}
