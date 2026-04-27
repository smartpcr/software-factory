// -----------------------------------------------------------------------
// <copyright file="StartNodeDefinition.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes.Definitions;

using System.ComponentModel.DataAnnotations;
using ExecutionEngine.Enums;

/// <summary>
/// Definition for a Start node — the entry point of a workflow.
/// </summary>
public class StartNodeDefinition : NodeDefinition
{
    /// <inheritdoc/>
    public override RuntimeType RuntimeType => RuntimeType.Start;

    /// <summary>
    /// Gets or sets the trigger type for this start node.
    /// </summary>
    public TriggerType TriggerType { get; set; } = TriggerType.Manual;

    /// <summary>
    /// Gets or sets the cron schedule expression (only used when TriggerType is Schedule).
    /// </summary>
    public string? Schedule { get; set; }

    /// <summary>
    /// Gets or sets the event name to listen for (only used when TriggerType is Event).
    /// </summary>
    public string? EventName { get; set; }

    /// <summary>
    /// Gets or sets the HTTP method for the webhook (only used when TriggerType is WebHook).
    /// </summary>
    public string WebHookMethod { get; set; } = "POST";

    /// <summary>
    /// Gets or sets the route name for the webhook endpoint (only used when TriggerType is WebHook).
    /// The full route is /webhook/{WebHookRoute}.
    /// </summary>
    public string? WebHookRoute { get; set; }

    /// <summary>
    /// Gets or sets the JSON schema describing the expected webhook request body
    /// (only used when TriggerType is WebHook).
    /// </summary>
    public string? BodyJsonSchema { get; set; }

    /// <inheritdoc/>
    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (this.TriggerType == TriggerType.Schedule)
        {
            if (string.IsNullOrWhiteSpace(this.Schedule))
            {
                yield return new ValidationResult(
                    "Schedule is required when TriggerType is Schedule.",
                    new[] { nameof(this.Schedule) });
            }
            else
            {
                // Validate cron expression
                string? cronError = null;
                try
                {
                    NCrontab.CrontabSchedule.Parse(this.Schedule);
                }
                catch (Exception ex)
                {
                    cronError = ex.Message;
                }

                if (cronError != null)
                {
                    yield return new ValidationResult(
                        $"Invalid cron expression: {cronError}",
                        new[] { nameof(this.Schedule) });
                }
            }
        }

        if (this.TriggerType == TriggerType.Event)
        {
            if (string.IsNullOrWhiteSpace(this.EventName))
            {
                yield return new ValidationResult(
                    "EventName is required when TriggerType is Event.",
                    new[] { nameof(this.EventName) });
            }
        }

        if (this.TriggerType == TriggerType.WebHook)
        {
            if (string.IsNullOrWhiteSpace(this.WebHookRoute))
            {
                yield return new ValidationResult(
                    "WebHookRoute is required when TriggerType is WebHook.",
                    new[] { nameof(this.WebHookRoute) });
            }
        }
    }
}