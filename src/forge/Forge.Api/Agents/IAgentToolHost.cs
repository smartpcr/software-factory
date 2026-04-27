// -----------------------------------------------------------------------
// <copyright file="IAgentToolHost.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

/// <summary>
/// Phase 11.6 — gates every tool execution an agent attempts. Three rules:
///   (a) the workstream's worktree path is the only writable root,
///   (b) the per-tool allowlist from soul.md front-matter,
///   (c) hard wall-clock + token budgets per invocation.
/// Subprocess isolation is a Phase R upgrade — this host runs tools in-proc.
/// </summary>
public interface IAgentToolHost
{
    /// <summary>Allocate a per-invocation host bound to one worktree path + budget.</summary>
    IAgentToolSession OpenSession(AgentContext context, TimeSpan wallClock, long? maxTokens = null);
}

/// <summary>
/// Per-invocation tool host. Disposing closes the session and stops accepting
/// further tool calls.
/// </summary>
public interface IAgentToolSession : IDisposable
{
    /// <summary>Read a file under the worktree root. Throws if path escapes.</summary>
    Task<string> ReadFileAsync(string relativeOrAbsolutePath, CancellationToken ct);

    /// <summary>Write a file under the worktree root. Throws if path escapes.</summary>
    Task WriteFileAsync(string relativeOrAbsolutePath, string contents, CancellationToken ct);

    /// <summary>Run a shell command from the worktree root. Throws if the tool isn't allowlisted.</summary>
    Task<ToolRunResult> RunCommandAsync(string command, IReadOnlyList<string> args, CancellationToken ct);

    /// <summary>
    /// Increment the running token usage. Throws <see cref="AgentBudgetExceededException"/>
    /// if it crosses the per-invocation token budget.
    /// </summary>
    void RecordTokens(long input, long output);
}

/// <summary>Result of a sandboxed shell run.</summary>
public sealed record ToolRunResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Thrown when a tool call would breach the invocation budget.</summary>
public sealed class AgentBudgetExceededException : Exception
{
    public AgentBudgetExceededException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown when a tool call breaches the sandbox (path escape, disallowed tool).</summary>
public sealed class AgentSandboxViolationException : Exception
{
    public AgentSandboxViolationException(string message)
        : base(message)
    {
    }
}
