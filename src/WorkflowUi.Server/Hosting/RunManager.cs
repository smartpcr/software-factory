// -----------------------------------------------------------------------
// <copyright file="RunManager.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Hosting;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Xml;
using ExecutionEngine.Engine;
using ExecutionEngine.Workflow;
using Microsoft.Extensions.Logging;
using WorkflowUi.Server.Telemetry;

/// <summary>
/// Manages active workflow runs. Creates a <see cref="WorkflowEngine"/> per run,
/// wires engine CLR events → <see cref="IRunEventSink"/>, and tracks lifecycle.
/// </summary>
public class RunManager
{
    private readonly ConcurrentDictionary<Guid, ActiveRun> runs = new();
    private readonly IRunEventSink eventSink;
    private readonly SequenceAllocator seqAllocator;
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<RunManager> logger;

    public RunManager(
        IRunEventSink eventSink,
        SequenceAllocator seqAllocator,
        IServiceProvider serviceProvider,
        ILogger<RunManager> logger)
    {
        this.eventSink = eventSink;
        this.seqAllocator = seqAllocator;
        this.serviceProvider = serviceProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Starts a new workflow run in a background task.
    /// Returns the run ID immediately; the engine executes asynchronously.
    /// </summary>
    public async Task<Guid> StartRunAsync(WorkflowDefinition workflow, Dictionary<string, object>? initialVariables = null)
    {
        var runId = Guid.NewGuid();
        var cts = new CancellationTokenSource();

        var engine = new WorkflowEngine(checkpointStorage: null, serviceProvider: this.serviceProvider);

        this.WireEngineEvents(engine, runId, workflow.WorkflowId);

        var activeRun = new ActiveRun(runId, workflow.WorkflowId, workflow.WorkflowName, cts);
        this.runs[runId] = activeRun;

        await this.PublishEventAsync(runId, RunEventKind.WorkflowStarted, payload: new Dictionary<string, object>
        {
            ["workflowId"] = workflow.WorkflowId,
            ["workflowName"] = workflow.WorkflowName,
            ["totalNodes"] = workflow.Nodes.Count,
        });

        this.logger.LogInformation("Starting run {RunId} for workflow {WorkflowId}", runId, workflow.WorkflowId);

        var timeout = workflow.TimeoutSeconds > 0
            ? TimeSpan.FromSeconds(workflow.TimeoutSeconds)
            : (TimeSpan?)null;

        var initVars = initialVariables ?? [];

        activeRun.ExecutionTask = Task.Run(async () =>
        {
            try
            {
                var context = await engine.StartAsync(workflow, initVars, runId, timeout, cts.Token);
                activeRun.Status = context.Status.ToString();

                var kind = context.Status == ExecutionEngine.Enums.WorkflowExecutionStatus.Completed
                    ? RunEventKind.WorkflowCompleted
                    : RunEventKind.WorkflowFailed;

                var payload = new Dictionary<string, object>
                {
                    ["workflowId"] = workflow.WorkflowId,
                    ["status"] = context.Status.ToString(),
                };

                if (context.Duration.HasValue)
                {
                    payload["duration"] = XmlConvert.ToString(context.Duration.Value);
                }

                await this.PublishEventAsync(runId, kind, payload: payload);
            }
            catch (OperationCanceledException)
            {
                activeRun.Status = "Cancelled";
                await this.PublishEventAsync(runId, RunEventKind.WorkflowCancelled, payload: new Dictionary<string, object>
                {
                    ["workflowId"] = workflow.WorkflowId,
                    ["reason"] = "Cancelled by user",
                });
            }
            catch (Exception ex)
            {
                activeRun.Status = "Failed";
                this.logger.LogError(ex, "Run {RunId} failed with unhandled exception", runId);
                await this.PublishEventAsync(runId, RunEventKind.WorkflowFailed, payload: new Dictionary<string, object>
                {
                    ["workflowId"] = workflow.WorkflowId,
                    ["errorMessage"] = ex.Message,
                });
            }
            finally
            {
                activeRun.EndTimeUtc = DateTime.UtcNow;
            }
        });

        return runId;
    }

    /// <summary>
    /// Cancels a running workflow.
    /// </summary>
    public bool CancelRun(Guid runId)
    {
        if (!this.runs.TryGetValue(runId, out var run))
        {
            return false;
        }

        try
        {
            run.Cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets a single run by ID.
    /// </summary>
    public ActiveRun? GetRun(Guid runId)
    {
        return this.runs.TryGetValue(runId, out var run) ? run : null;
    }

    /// <summary>
    /// Lists all tracked runs.
    /// </summary>
    public IReadOnlyList<ActiveRun> GetRuns()
    {
        return this.runs.Values.ToList().AsReadOnly();
    }

    private void WireEngineEvents(WorkflowEngine engine, Guid runId, string workflowId)
    {
        engine.NodeStarted += (nodeId, instanceId) =>
        {
            _ = this.PublishEventAsync(
                runId,
                RunEventKind.NodeStarted,
                nodeId: nodeId,
                payload: new Dictionary<string, object>
                {
                    ["nodeInstanceId"] = instanceId.ToString(),
                });
        };

        engine.NodeCompleted += (nodeId, instanceId, duration) =>
        {
            _ = this.PublishEventAsync(
                runId,
                RunEventKind.NodeCompleted,
                nodeId: nodeId,
                payload: new Dictionary<string, object>
                {
                    ["nodeInstanceId"] = instanceId.ToString(),
                    ["duration"] = XmlConvert.ToString(duration),
                });
        };

        engine.NodeCompletedEx += (nodeId, instanceId, duration, inputData, outputData, globals) =>
        {
            // Store context data in the same event via a separate "context" event
            _ = this.PublishEventAsync(
                runId,
                RunEventKind.NodeCompleted,
                nodeId: nodeId,
                payload: new Dictionary<string, object>
                {
                    ["nodeInstanceId"] = instanceId.ToString(),
                    ["duration"] = XmlConvert.ToString(duration),
                    ["inputData"] = inputData ?? new Dictionary<string, object>(),
                    ["outputData"] = outputData ?? new Dictionary<string, object>(),
                    ["globalVariables"] = globals ?? new Dictionary<string, object>(),
                },
                synthetic: true);
        };

        engine.NodeFailed += (nodeId, instanceId, errorMessage) =>
        {
            _ = this.PublishEventAsync(
                runId,
                RunEventKind.NodeFailed,
                nodeId: nodeId,
                payload: new Dictionary<string, object>
                {
                    ["nodeInstanceId"] = instanceId.ToString(),
                    ["errorMessage"] = errorMessage,
                });
        };

        engine.NodeFailedEx += (nodeId, instanceId, errorMessage, inputData, globals) =>
        {
            _ = this.PublishEventAsync(
                runId,
                RunEventKind.NodeFailed,
                nodeId: nodeId,
                payload: new Dictionary<string, object>
                {
                    ["nodeInstanceId"] = instanceId.ToString(),
                    ["errorMessage"] = errorMessage,
                    ["inputData"] = inputData ?? new Dictionary<string, object>(),
                    ["globalVariables"] = globals ?? new Dictionary<string, object>(),
                },
                synthetic: true);
        };

        engine.NodeCancelled += (nodeId, instanceId, reason) =>
        {
            _ = this.PublishEventAsync(
                runId,
                RunEventKind.NodeCancelled,
                nodeId: nodeId,
                payload: new Dictionary<string, object>
                {
                    ["nodeInstanceId"] = instanceId.ToString(),
                    ["reason"] = reason,
                });
        };
    }

    private async Task PublishEventAsync(
        Guid runId,
        RunEventKind kind,
        string? nodeId = null,
        string? nodeName = null,
        Dictionary<string, object>? payload = null,
        bool synthetic = false)
    {
        var runEvent = new RunEvent
        {
            RunId = runId,
            Seq = this.seqAllocator.Next(runId),
            Kind = kind,
            Synthetic = synthetic,
            TimestampUtc = DateTime.UtcNow,
            NodeId = nodeId,
            NodeName = nodeName,
            Payload = payload,
        };

        try
        {
            await this.eventSink.PublishAsync(runEvent);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to publish {Kind} event for run {RunId}", kind, runId);
        }
    }
}

/// <summary>
/// Tracks state for a single active workflow run.
/// </summary>
public class ActiveRun
{
    public ActiveRun(Guid runId, string workflowId, string workflowName, CancellationTokenSource cts)
    {
        this.RunId = runId;
        this.WorkflowId = workflowId;
        this.WorkflowName = workflowName;
        this.Cts = cts;
        this.Status = "Running";
        this.StartTimeUtc = DateTime.UtcNow;
    }

    public Guid RunId { get; }

    public string WorkflowId { get; }

    public string WorkflowName { get; }

    public string Status { get; set; }

    public DateTime StartTimeUtc { get; }

    public DateTime? EndTimeUtc { get; set; }

    internal CancellationTokenSource Cts { get; }

    internal Task? ExecutionTask { get; set; }
}