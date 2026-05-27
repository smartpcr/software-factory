// -----------------------------------------------------------------------
// <copyright file="SloppyVerdictException.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories;

using Forge.Api.Services;

/// <summary>
/// Phase 88 — typed exception raised by
/// <c>CrewStoryArtifactStrategy.RunGenEvalLoopAsync</c> when the
/// <see cref="DeliverableLoop"/> returns a sloppy verdict (iter-cap,
/// agent-refusal, score-plateau, token-cap, etc.). Carries the full
/// <see cref="DeliverableLoopResult"/> so the qa-e2e caller in
/// <c>StorySpawnOrchestrator.DriveOneQaE2eWorkstreamAsync</c> can read
/// <see cref="DeliverableLoopResult.SloppyReason"/> /
/// <see cref="DeliverableLoopResult.Iterations"/> /
/// <see cref="DeliverableLoopResult.FinalScore"/> for telemetry and to
/// correctly stamp <c>wi.ExecutionStatus = PendingReassign</c> so
/// <see cref="SloppyReassignmentReconciler"/> rotates the pair (mirrors
/// the impl-path's behavior at
/// <c>CrewQueueWorker.cs:2848-2884</c>).
/// <para>
/// Non-sloppy fatal verdicts (e.g., generator transport failure, evaluator
/// crash) still surface as the existing generic
/// <see cref="System.InvalidOperationException"/> — distinguishing the
/// two lets the qa-e2e catch path branch cleanly without parsing exception
/// messages.
/// </para>
/// </summary>
public sealed class SloppyVerdictException : System.Exception
{
    /// <summary>The full result that triggered this throw.</summary>
    public DeliverableLoopResult Result { get; }

    /// <summary>Structured sloppy trigger (iter-cap / agent-refusal / etc.).</summary>
    public string? SloppyReason => this.Result.SloppyReason;

    /// <summary>Iterations consumed before the sloppy exit.</summary>
    public int Iterations => this.Result.Iterations;

    /// <summary>Last evaluator score (or 0 for refusal exits).</summary>
    public int FinalScore => this.Result.FinalScore;

    public SloppyVerdictException(DeliverableLoopResult result, string message)
        : base(message)
    {
        this.Result = result;
    }
}
