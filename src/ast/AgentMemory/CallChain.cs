// -----------------------------------------------------------------------
// <copyright file="CallChain.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory;

/// <summary>
/// How a call chain was discovered. Prevents conflating static code-graph topology
/// with agent-observed behavior — inferred chains are scored differently from observed ones.
/// </summary>
public enum CallChainSource
{
    /// <summary>Discovered by an agent during an actual task (has episode provenance).</summary>
    Observed,

    /// <summary>Inferred from static code analysis (HypergraphData MethodInvocation edges).</summary>
    Inferred
}

/// <summary>
/// Represents an ordered sequence of method calls observed during agent interactions.
/// Unlike binary <see cref="MemoryEdge"/>s, call chains capture n-ary ordered
/// relationships — e.g., "MethodA → MethodB → MethodC" as a single unit.
/// Inspired by HyperGraphRAG (arXiv:2503.21322) for n-ary relation modeling.
/// </summary>
public class CallChain
{
    /// <summary>
    /// Unique identifier for this call chain.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// How this chain was discovered. Inferred chains (from static analysis)
    /// are scored differently from observed chains (from agent episodes).
    /// </summary>
    public CallChainSource Source { get; init; } = CallChainSource.Observed;

    /// <summary>
    /// Ordered sequence of method calls in this chain.
    /// </summary>
    public List<CallChainStep> Steps { get; init; } = [];

    /// <summary>
    /// Named pattern if this chain matches a recognized code pattern
    /// (e.g., "repository-query-pipeline", "error-handling-chain").
    /// </summary>
    public string? PatternName { get; init; }

    /// <summary>
    /// Human-readable description of what this call chain does.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Fully qualified symbol ID of the entry point (first step).
    /// Denormalized for fast lookup without loading all steps.
    /// </summary>
    public string? EntrySymbolId { get; init; }

    /// <summary>
    /// Fully qualified symbol ID of the terminal node (last step).
    /// Denormalized for fast lookup without loading all steps.
    /// </summary>
    public string? TerminalSymbolId { get; init; }

    /// <summary>
    /// IDs of episodes in which this call chain was observed.
    /// Provides provenance — which agent interactions discovered this chain.
    /// </summary>
    public List<string> ObservedInEpisodeIds { get; init; } = [];

    /// <summary>
    /// Number of times this call chain has been observed.
    /// Higher counts indicate more stable/important call paths.
    /// </summary>
    public int ObservationCount { get; set; } = 1;

    /// <summary>
    /// Confidence level from 0.0 to 1.0, reinforced by observation count
    /// and decayed by time since last observation.
    /// </summary>
    public double Confidence { get; set; } = 0.5;

    /// <summary>
    /// Weight of this call chain (0.0 to 1.0).
    /// Reinforced by observation frequency and agent success outcomes.
    /// </summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>
    /// When this call chain was first observed.
    /// </summary>
    public DateTime FirstObserved { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When this call chain was last observed.
    /// </summary>
    public DateTime LastObserved { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Git commit SHA when this chain was last validated.
    /// </summary>
    public string? CommitSha { get; set; }

    /// <summary>
    /// Number of times this chain was retrieved for context.
    /// Higher access counts slow decay (same pattern as ExpertiseRecord).
    /// </summary>
    public int AccessCount { get; set; }

    /// <summary>
    /// Optional tags for categorization.
    /// </summary>
    public List<string> Tags { get; init; } = [];
}

/// <summary>
/// A single step in a <see cref="CallChain"/>.
/// </summary>
public class CallChainStep
{
    /// <summary>
    /// Fully qualified symbol ID for this step (e.g., "MyNamespace.MyClass.MyMethod").
    /// </summary>
    public required string SymbolId { get; init; }

    /// <summary>
    /// File path where this symbol is defined.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Zero-based index of this step in the chain.
    /// </summary>
    public int StepIndex { get; init; }

    /// <summary>
    /// Optional description of what this step does in the chain context.
    /// </summary>
    public string? Role { get; init; }
}

/// <summary>
/// Filter criteria for querying call chains.
/// </summary>
public class CallChainFilter
{
    /// <summary>
    /// Filter to chains containing this symbol at any step.
    /// </summary>
    public string? ContainsSymbolId { get; set; }

    /// <summary>
    /// Filter to chains with this entry point.
    /// </summary>
    public string? EntrySymbolId { get; set; }

    /// <summary>
    /// Filter to chains matching this pattern name.
    /// </summary>
    public string? PatternName { get; set; }

    /// <summary>
    /// Minimum confidence threshold.
    /// </summary>
    public double MinConfidence { get; set; }

    /// <summary>
    /// Minimum observation count.
    /// </summary>
    public int MinObservationCount { get; set; }

    /// <summary>
    /// Maximum number of results.
    /// </summary>
    public int Limit { get; set; } = 50;
}
