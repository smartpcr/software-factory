// -----------------------------------------------------------------------
// <copyright file="ModelsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Agents;

/// <summary>
/// Phase 11.2 — read-only model catalog. Pricing here is the registry default;
/// each <see cref="Forge.Api.Persistence.CrewRow"/> denormalises its assigned
/// model's pricing at assignment time so historical salary lookups don't drift.
/// </summary>
public static class ModelsEndpoints
{
    public static WebApplication MapModelsEndpoints(this WebApplication app)
    {
        var grp = app.MapGroup("/api/models").WithTags("Models");
        grp.MapGet("/", (IModelRegistry reg) =>
            Results.Ok(reg.All.Select(m => m with { Available = reg.ValidateAssignable(m.Id) is null })))
            .WithName("ListModels");
        grp.MapGet("/{id}", (string id, IModelRegistry reg) =>
        {
            var model = reg.Find(id);
            return model is null
                ? Results.Problem(title: $"Model {id} not found", statusCode: 404)
                : Results.Ok(model with { Available = reg.ValidateAssignable(model.Id) is null });
        }).WithName("GetModel");

        // 2026-09-10 — force a model-catalog enrichment cycle instead of waiting
        // for the daily tick: re-fetches the live Copilot catalog, auto-adds
        // newly released models, retires ones it no longer serves, and persists
        // both to the models.json seed. Returns the refreshed catalog so the
        // caller doesn't need a follow-up GET.
        grp.MapPost("/refresh", async (
            Forge.Api.Services.ModelCatalogEnrichmentService enrichment,
            IModelRegistry reg,
            CancellationToken ct) =>
        {
            await enrichment.RefreshNowAsync(ct);
            return Results.Ok(new
            {
                enriched = reg.CatalogEnriched,
                liveCatalogCount = reg.LiveCatalogIds.Count,
                models = reg.All.Select(m => m with { Available = reg.ValidateAssignable(m.Id) is null }),
            });
        }).WithName("RefreshModels");

        return app;
    }
}
