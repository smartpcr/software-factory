// -----------------------------------------------------------------------
// <copyright file="CompactionOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

/// <summary>
/// Config for <see cref="MemoryCompactor"/> bound from
/// <c>Forge:Compaction:*</c>. Phase F (docs/forge/sloppy_guard.md) used
/// a hardcoded <c>0.3</c> model-window multiplier, which clamped Opus's
/// 1M-context cells down to 300K but also clamped GPT-5.5's 128K-context
/// cells down to a too-tight 38K. Making the ratio configurable lets the
/// operator pick a single fraction that suits the pool they run; the
/// per-role math (each role's own model's context window) is handled in
/// <see cref="MemoryCompactor.ComputeEffectiveThreshold"/>.
/// </summary>
public sealed class CompactionOptions
{
    public const string SectionName = "Forge:Compaction";

    /// <summary>
    /// Fraction of <c>model.ContextWindow</c> at which the compactor
    /// folds older turns. Defaults to <c>0.5</c> — half the window is
    /// dense enough that the synthetic summary turn + recency tail
    /// still fits comfortably, while leaving the other half free for
    /// the next iter's tool calls / file dumps. The effective threshold
    /// the compactor uses is
    /// <c>min(crew.CompactionTokenThreshold ?? <see cref="MemoryCompactor.DefaultThreshold"/>,
    /// model.ContextWindow × ContextWindowRatio)</c>.
    /// </summary>
    public double ContextWindowRatio { get; set; } = 0.5;
}
