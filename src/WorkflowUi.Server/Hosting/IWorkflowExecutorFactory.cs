// -----------------------------------------------------------------------
// <copyright file="IWorkflowExecutorFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Hosting;

using ExecutionEngine.Workflow;

/// <summary>
/// Factory for creating workflow executors.
/// Placeholder until Phase 7 implements the real executor.
/// </summary>
public interface IWorkflowExecutorFactory
{
    /// <summary>
    /// Creates a workflow executor for the given definition.
    /// </summary>
    /// <param name="definition">The workflow to execute.</param>
    /// <returns>An executor instance.</returns>
    IWorkflowExecutor Create(WorkflowDefinition definition);
}

/// <summary>
/// Minimal executor interface — Phase 7 fills this in.
/// </summary>
public interface IWorkflowExecutor
{
    /// <summary>
    /// Runs the workflow asynchronously.
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Placeholder implementation that throws on use. Registered so the DI graph composes.
/// </summary>
internal sealed class PlaceholderWorkflowExecutorFactory : IWorkflowExecutorFactory
{
    /// <inheritdoc/>
    public IWorkflowExecutor Create(WorkflowDefinition definition)
    {
        throw new NotImplementedException("Pending Phase 7 — executor not yet implemented.");
    }
}
