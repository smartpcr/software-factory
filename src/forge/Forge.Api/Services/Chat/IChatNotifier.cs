// -----------------------------------------------------------------------
// <copyright file="IChatNotifier.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

/// <summary>
/// Phase 95 (chat bridge) — the enqueue API producers call. Writing a
/// dedupe-keyed <c>pending</c> row to the outbox is the <b>only</b> thing a
/// producer does; the (background) outbound pump does the network I/O. If the
/// bridge is disabled the row simply sits and expires — producers never block
/// on chat delivery.
///
/// <para>Resolution echo: when an item is answered/acted <b>elsewhere</b> (e.g.
/// the web UI), call the matching <c>Mark…ResolvedAsync</c> so the pump edits
/// the already-sent chat message to "✅ Resolved" instead of leaving a stale
/// prompt with live buttons.</para>
/// </summary>
public interface IChatNotifier
{
    /// <summary>Enqueue an open-questions alert. Returns false when deduped (an open row already exists).</summary>
    Task<bool> EnqueueOpenQuestionsAsync(OpenQuestionsAlert alert, CancellationToken ct);

    /// <summary>Enqueue a stalled-workstream alert. Returns false when deduped.</summary>
    Task<bool> EnqueueWorkstreamStalledAsync(WorkstreamStalledAlert alert, CancellationToken ct);

    /// <summary>Enqueue a queue-strain alert. Returns false when deduped.</summary>
    Task<bool> EnqueueQueueStrainAsync(QueueStrainAlert alert, CancellationToken ct);

    /// <summary>
    /// Enqueue an auth / human-interaction alert (Forge blocked on a host-side
    /// sign-in or manual step). Returns false when deduped. No token/secret is
    /// ever placed in the alert — only the prompt to act.
    /// </summary>
    Task<bool> EnqueueHumanInteractionAsync(HumanInteractionAlert alert, CancellationToken ct);

    /// <summary>Flip open open-questions notifications for a work item toward resolved. Returns rows affected.</summary>
    Task<int> MarkOpenQuestionsResolvedAsync(string workItemId, CancellationToken ct);

    /// <summary>Flip open stalled-workstream notifications for a workstream toward resolved. Returns rows affected.</summary>
    Task<int> MarkWorkstreamResolvedAsync(string workstreamId, CancellationToken ct);
}

/// <summary>Producer input for an open-questions alert.</summary>
public sealed record OpenQuestionsAlert(
    string ProductId,
    string? RepoId,
    string WorkItemId,
    int Iter,
    string Title,
    string Body,
    IReadOnlyList<ChatButton>? Buttons,
    string? SpawnId = null);

/// <summary>Producer input for a stalled-workstream alert.</summary>
public sealed record WorkstreamStalledAlert(
    string ProductId,
    string? RepoId,
    string WorkstreamId,
    string StallReason,
    string Title,
    string Body,
    IReadOnlyList<ChatButton>? Buttons,
    string? SpawnId = null);

/// <summary>Producer input for a queue-strain alert.</summary>
public sealed record QueueStrainAlert(
    string ProductId,
    string? RepoId,
    string CrewId,
    string? QueueId,
    string Title,
    string Body,
    IReadOnlyList<ChatButton>? Buttons);

/// <summary>
/// Producer input for an auth / human-interaction alert. <see cref="InteractionKey"/>
/// identifies the blocked flow (e.g. <c>ado-signin:{account}</c>) and drives the
/// dedupe key; the buttons carry <c>hi|{action}|{InteractionKey}</c> callbacks.
/// </summary>
public sealed record HumanInteractionAlert(
    string ProductId,
    string? RepoId,
    string InteractionKey,
    string Title,
    string Body,
    IReadOnlyList<ChatButton>? Buttons);
