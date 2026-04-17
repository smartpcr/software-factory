// -----------------------------------------------------------------------
// <copyright file="MapperRoundTripEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Endpoints;

using System.Text.Json;
using WorkflowUi.Server.Mapping;

/// <summary>
/// Minimal-API endpoints for mapper round-trip operations.
/// </summary>
public static class MapperRoundTripEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// Maps mapper round-trip endpoints to the application.
    /// </summary>
    public static WebApplication MapMapperRoundTripEndpoints(this WebApplication app)
    {
        app.MapPost("/api/mapper/roundtrip", (
            WorkflowDefinitionDto request,
            ILogger<Program> logger) =>
        {
            logger.LogInformation("Mapper round-trip for workflow '{WorkflowId}'", request.WorkflowId);

            // Verify no unrecognized runtime types
            var unrecognized = MapperDiagnostics.FindUnrecognizedRuntimeTypes(request);
            if (unrecognized.Count > 0)
            {
                logger.LogWarning("Unrecognized runtime types: {Types}", string.Join(", ", unrecognized));
                return Results.BadRequest(new { error = $"Unrecognized runtime types: {string.Join(", ", unrecognized)}" });
            }

            // Map DTO -> engine -> DTO (round-trip)
            var engineWorkflow = EngineDtoMapper.MapWorkflowFromDto(request);
            var roundTripped = EngineDtoMapper.MapWorkflowToDto(engineWorkflow);

            var summary = MapperDiagnostics.Summarize(roundTripped);
            logger.LogInformation(
                "Round-trip complete: {NodeCount} nodes, {ConnectionCount} connections",
                summary.NodeCount,
                summary.ConnectionCount);

            return Results.Ok(roundTripped);
        })
        .WithTags("Mapper")
        .WithName("MapperRoundTrip");

        return app;
    }
}
