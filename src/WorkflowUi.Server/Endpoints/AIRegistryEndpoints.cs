// -----------------------------------------------------------------------
// <copyright file="AIRegistryEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Endpoints;

/// <summary>
/// Minimal-API endpoints that expose available AI agents and skills loaded from disk.
/// </summary>
public static class AIRegistryEndpoints
{
    public static WebApplication MapAIRegistryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/ai").WithTags("AI Registry");

        group.MapGet("/agents", (AIRegistryService registry) =>
        {
            return Results.Ok(registry.GetAgents());
        })
        .WithName("ListAgents")
        .AllowAnonymous();

        group.MapGet("/skills", (AIRegistryService registry) =>
        {
            return Results.Ok(registry.GetSkills());
        })
        .WithName("ListSkills")
        .AllowAnonymous();

        group.MapGet("/agents/{name}/inputs", async (string name, ExecutionEngine.AI.IAIPromptResolver resolver, CancellationToken ct) =>
        {
            var info = await resolver.LoadAgentAsync(name, ct);
            if (info == null) return Results.NotFound();
            var hasScenarios = info.Scenarios != null && info.Scenarios.Count > 0;
            return Results.Ok(new
            {
                inputs = hasScenarios ? (object)System.Array.Empty<object>() : info.Inputs,
                outputs = hasScenarios ? (object)System.Array.Empty<object>() : info.Outputs,
                scenarios = info.Scenarios,
                defaultScenario = info.DefaultScenario,
            });
        })
        .WithName("GetAgentInputs")
        .AllowAnonymous();

        group.MapGet("/skills/{name}/inputs", async (string name, ExecutionEngine.AI.IAIPromptResolver resolver, CancellationToken ct) =>
        {
            var info = await resolver.LoadSkillAsync(name, ct);
            if (info == null) return Results.NotFound();
            var hasScenarios = info.Scenarios != null && info.Scenarios.Count > 0;
            return Results.Ok(new
            {
                inputs = hasScenarios ? (object)System.Array.Empty<object>() : info.Inputs,
                outputs = hasScenarios ? (object)System.Array.Empty<object>() : info.Outputs,
                scenarios = info.Scenarios,
                defaultScenario = info.DefaultScenario,
            });
        })
        .WithName("GetSkillInputs")
        .AllowAnonymous();

        return app;
    }
}
