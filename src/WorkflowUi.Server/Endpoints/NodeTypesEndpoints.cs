// -----------------------------------------------------------------------
// <copyright file="NodeTypesEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Endpoints;

using ExecutionEngine.Catalog;

/// <summary>
/// Minimal-API endpoints that expose the ExecutionEngine node type catalog.
/// Icons are mapped on the UI side.
/// </summary>
public static class NodeTypesEndpoints
{
    public static WebApplication MapNodeTypesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/node-types").WithTags("NodeTypes");

        group.MapGet("/", () =>
        {
            var catalog = NodeTypeCatalog.GetAll();
            var result = catalog.Select(n => new
            {
                n.RuntimeType,
                n.Label,
                n.Category,
                n.Description,
                ports = new { @in = n.HasInput, @out = n.HasOutput, error = n.HasError },
                n.OutputPorts,
                n.DynamicPorts,
                n.PortLabels,
                n.Events,
                n.Properties,
            });
            return Results.Ok(result);
        })
        .WithName("ListNodeTypes")
        .AllowAnonymous();

        return app;
    }
}
