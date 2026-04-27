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
        grp.MapGet("/", (IModelRegistry reg) => Results.Ok(reg.All)).WithName("ListModels");
        grp.MapGet("/{id}", (string id, IModelRegistry reg) =>
        {
            var model = reg.Find(id);
            return model is null
                ? Results.Problem(title: $"Model {id} not found", statusCode: 404)
                : Results.Ok(model);
        }).WithName("GetModel");
        return app;
    }
}
