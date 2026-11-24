// -----------------------------------------------------------------------
// <copyright file="PairModelResolutionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Endpoints;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The story delivery strip showed every engineer as "claude-opus-4-7" from a
/// hard-coded UI table. The spawn detail now carries the models resolved from
/// agent + crew config, with the same fallback chain the runner dispatches with.
/// </summary>
public class PairModelResolutionTests
{
    [Fact]
    public async Task Resolves_generator_and_evaluator_models_from_agent_then_crew()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot host + migrations */ }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        control.Crews.Add(new CrewRow { Id = "pm-crew", Name = "PM", Lead = "pm-crew-only", Model = "crew-gen", EvaluatorModel = "crew-eval" });
        control.Crews.Add(new CrewRow { Id = "pm-crew-noeval", Name = "PM2", Lead = "pm-own-gen", Model = "crew2-gen" });
        control.Agents.Add(new AgentRow { Id = "pm-crew-only", Name = "Crew only", Role = "eng", CrewId = "pm-crew" });
        control.Agents.Add(new AgentRow { Id = "pm-own", Name = "Own", Role = "eng", CrewId = "pm-crew", GeneratorModel = "agent-gen", EvaluatorModel = "agent-eval" });
        control.Agents.Add(new AgentRow { Id = "pm-own-gen", Name = "Own gen", Role = "eng", CrewId = "pm-crew-noeval", GeneratorModel = "agent2-gen" });
        await control.SaveChangesAsync();

        var models = await StoriesEndpoints.ResolvePairModelsAsync(control, new[] { "pm-crew-only", "pm-own", "pm-own-gen", "pm-missing" });

        Assert.Equal(("crew-gen", "crew-eval"), models["pm-crew-only"]);
        Assert.Equal(("agent-gen", "agent-eval"), models["pm-own"]);
        // No evaluator model anywhere: falls back to the agent's generator model.
        Assert.Equal(("agent2-gen", "agent2-gen"), models["pm-own-gen"]);
        Assert.False(models.ContainsKey("pm-missing"));
    }
}