// -----------------------------------------------------------------------
// <copyright file="WorkflowValidationEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Endpoints;

using ExecutionEngine.Workflow;

/// <summary>
/// Minimal-API validation endpoints for workflow validation.
/// </summary>
public static class WorkflowValidationEndpoints
{
    /// <summary>
    /// Maps the workflow validation endpoint to the application.
    /// </summary>
    public static WebApplication MapWorkflowValidationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/workflows/validate", async (
            ValidateWorkflowRequest request,
            WorkflowLoader loader,
            ILogger<Program> logger) =>
        {
            logger.LogInformation("Validating workflow '{WorkflowId}'", request.WorkflowId);

            try
            {
                var workflow = loader.Load(request.WorkflowId);
                if (workflow == null)
                {
                    return Results.Ok(new[]
                    {
                        new ValidationEntryDto
                        {
                            NodeId = string.Empty,
                            Message = $"Workflow '{request.WorkflowId}' not found",
                            Severity = "error",
                        },
                    });
                }

                var result = loader.Validate(workflow);
                var entries = new List<ValidationEntryDto>();

                foreach (var error in result.Errors)
                {
                    entries.Add(new ValidationEntryDto
                    {
                        NodeId = ExtractNodeId(error),
                        Message = error,
                        Severity = "error",
                    });
                }

                foreach (var warning in result.Warnings)
                {
                    entries.Add(new ValidationEntryDto
                    {
                        NodeId = ExtractNodeId(warning),
                        Message = warning,
                        Severity = "warning",
                    });
                }

                return Results.Ok(entries);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Validation failed for workflow '{WorkflowId}'", request.WorkflowId);
                return Results.Ok(new[]
                {
                    new ValidationEntryDto
                    {
                        NodeId = string.Empty,
                        Message = $"Validation failed: {ex.Message}",
                        Severity = "error",
                    },
                });
            }
        })
        .WithTags("Validation")
        .WithName("ValidateWorkflow");

        return app;
    }

    private static string ExtractNodeId(string message)
    {
        // Try to extract node ID from validation messages like "Node 'nodeId': ..."
        var match = System.Text.RegularExpressions.Regex.Match(message, @"Node '([^']+)'");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}

/// <summary>
/// Request body for workflow validation.
/// </summary>
public class ValidateWorkflowRequest
{
    /// <summary>
    /// Gets or sets the workflow ID to validate.
    /// </summary>
    public string WorkflowId { get; set; } = string.Empty;
}

/// <summary>
/// Individual validation entry returned to the UI.
/// </summary>
public class ValidationEntryDto
{
    /// <summary>
    /// Gets or sets the node ID associated with this validation entry.
    /// </summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the field path, if applicable.
    /// </summary>
    public string? Field { get; set; }

    /// <summary>
    /// Gets or sets the validation message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the severity: "error" or "warning".
    /// </summary>
    public string Severity { get; set; } = "error";
}
