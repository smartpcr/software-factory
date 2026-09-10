// -----------------------------------------------------------------------
// <copyright file="ChatNotifier.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Security.Cryptography;
using System.Text;
using Common.Chat;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 95 (chat bridge) — the concrete <see cref="IChatNotifier"/>. Writes
/// dedupe-keyed <c>pending</c> rows to the control-DB outbox and flips open rows
/// toward resolved. No network I/O — that's the outbound pump's job.
///
/// <para>Recipient resolution is the Phase-1/2 bootstrap: the row's
/// <c>ChatId</c> is the first configured <c>AllowedChatIds</c>. Phase 4 replaces
/// this with a <c>chat_subscribers</c> lookup; the pump treats an empty
/// <c>ChatId</c> as a soft delivery failure, not a crash.</para>
/// </summary>
public sealed class ChatNotifier : IChatNotifier
{
    private const string KindOpenQuestions = "open-questions";
    private const string KindWorkstreamStalled = "workstream-stalled";
    private const string KindQueueStrain = "queue-strain";
    private const string KindHumanInteraction = "human-interaction";

    private readonly ControlDbContext control;
    private readonly IOptionsMonitor<ChatBridgeOptions> options;
    private readonly ILogger<ChatNotifier> logger;

    public ChatNotifier(
        ControlDbContext control,
        IOptionsMonitor<ChatBridgeOptions> options,
        ILogger<ChatNotifier> logger)
    {
        this.control = control;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task<bool> EnqueueOpenQuestionsAsync(OpenQuestionsAlert alert, CancellationToken ct)
    {
        // Content-aware dedupe: a re-emitted UNCHANGED question block stays
        // deduped iteration over iteration, but a CHANGED set (question added or
        // answered) must produce a fresh chat message — an in-place edit would
        // not notify the operator, and without the hash the first alert held the
        // per-work-item slot forever while new questions accumulated unseen
        // (release RELEASE-PROVIDER, 2026-07-17: the iter-30 question never
        // reached chat because the iter-13 alert was still open).
        var dedupeKey = $"oq:{alert.WorkItemId}:{StableHash(alert.Body)}";

        // Supersede open alerts carrying an OUTDATED set for this work item —
        // frees their dedupe slot so the current set can send. Their chat
        // messages remain as history; the newest message is the live one.
        var stale = await this.control.ChatNotifications
            .Where(r => r.Platform != ChatTelegramSliceService.Platform && r.Platform != "plugins" && r.Kind == KindOpenQuestions
                && r.WorkItemId == alert.WorkItemId
                && r.DedupeKey != dedupeKey
                && (r.Status == ChatNotificationStatuses.Pending || r.Status == ChatNotificationStatuses.Sent))
            .ToListAsync(ct);
        if (stale.Count > 0)
        {
            foreach (var r in stale)
            {
                r.Status = ChatNotificationStatuses.Superseded;
            }

            await this.control.SaveChangesAsync(ct);
        }

        var row = new ChatNotificationRow
        {
            Kind = KindOpenQuestions,
            ProductId = alert.ProductId,
            RepoId = alert.RepoId,
            WorkItemId = alert.WorkItemId,
            SpawnId = alert.SpawnId,
            DedupeKey = dedupeKey,
            Title = alert.Title,
            Body = alert.Body,
            ActionsJson = ChatActions.Serialize(alert.Buttons),
        };
        return await this.EnqueueAsync(row, ct);
    }

    /// <inheritdoc/>
    public Task<bool> EnqueueWorkstreamStalledAsync(WorkstreamStalledAlert alert, CancellationToken ct)
    {
        var row = new ChatNotificationRow
        {
            Kind = KindWorkstreamStalled,
            ProductId = alert.ProductId,
            RepoId = alert.RepoId,
            WorkstreamId = alert.WorkstreamId,
            SpawnId = alert.SpawnId,
            DedupeKey = $"stall:{alert.WorkstreamId}:{StableHash(alert.StallReason)}",
            Title = alert.Title,
            Body = alert.Body,
            ActionsJson = ChatActions.Serialize(alert.Buttons),
        };
        return this.EnqueueAsync(row, ct);
    }

    /// <inheritdoc/>
    public Task<bool> EnqueueQueueStrainAsync(QueueStrainAlert alert, CancellationToken ct)
    {
        var row = new ChatNotificationRow
        {
            Kind = KindQueueStrain,
            ProductId = alert.ProductId,
            RepoId = alert.RepoId,
            QueueId = alert.QueueId,
            DedupeKey = $"strain:{alert.CrewId}",
            Title = alert.Title,
            Body = alert.Body,
            ActionsJson = ChatActions.Serialize(alert.Buttons),
        };
        return this.EnqueueAsync(row, ct);
    }

    /// <inheritdoc/>
    public Task<bool> EnqueueHumanInteractionAsync(HumanInteractionAlert alert, CancellationToken ct)
    {
        var row = new ChatNotificationRow
        {
            Kind = KindHumanInteraction,
            ProductId = alert.ProductId,
            RepoId = alert.RepoId,
            DedupeKey = $"interaction:{alert.InteractionKey}",
            Title = alert.Title,
            Body = alert.Body,
            ActionsJson = ChatActions.Serialize(alert.Buttons),
        };
        return this.EnqueueAsync(row, ct);
    }

    /// <inheritdoc/>
    public Task<int> MarkOpenQuestionsResolvedAsync(string workItemId, CancellationToken ct) =>
        this.MarkResolvedAsync(KindOpenQuestions, r => r.WorkItemId == workItemId, ChatNotificationStatuses.Answered, ct);

    /// <inheritdoc/>
    public Task<int> MarkWorkstreamResolvedAsync(string workstreamId, CancellationToken ct) =>
        this.MarkResolvedAsync(KindWorkstreamStalled, r => r.WorkstreamId == workstreamId, ChatNotificationStatuses.Acted, ct);

    private async Task<bool> EnqueueAsync(ChatNotificationRow row, CancellationToken ct)
    {
        var opt = this.options.CurrentValue;
        var now = DateTimeOffset.UtcNow;

        row.Id = Guid.NewGuid().ToString("N");
        row.Status = ChatNotificationStatuses.Pending;
        row.Platform = opt.Platform;
        row.CreatedAt = now;
        row.ExpiresAt = now + opt.NotificationTtl;

        // Part B — in group mode alerts go to the supergroup (a forum topic is
        // then selected per-spawn by the outbound pump); otherwise the first
        // configured single chat.
        var target = opt.Telegram.SupergroupChatId != 0
            ? opt.Telegram.SupergroupChatId
            : opt.Telegram.AllowedChatIds.FirstOrDefault();
        row.ChatId = target == 0 ? null : target.ToString();

        // Dedupe: skip when an OPEN (pending/sent) row already holds this key.
        // The partial-unique index is the hard backstop; this is the fast path.
        var duplicate = await this.control.ChatNotifications
            .AnyAsync(r => r.DedupeKey == row.DedupeKey
                && (r.Status == ChatNotificationStatuses.Pending || r.Status == ChatNotificationStatuses.Sent), ct);
        if (duplicate)
        {
            return false;
        }

        this.control.ChatNotifications.Add(row);
        try
        {
            await this.control.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex)
        {
            // Lost a race to the partial-unique index — treat as deduped.
            this.control.ChatNotifications.Remove(row);
            this.logger.LogDebug(ex, "chat notification dedupe race on key {Key}", row.DedupeKey);
            return false;
        }
    }

    private async Task<int> MarkResolvedAsync(
        string kind,
        System.Linq.Expressions.Expression<Func<ChatNotificationRow, bool>> anchor,
        string terminalStatus,
        CancellationToken ct)
    {
        var rows = await this.control.ChatNotifications
            .Where(r => r.Platform != ChatTelegramSliceService.Platform && r.Platform != "plugins" && r.Kind == kind
                && (r.Status == ChatNotificationStatuses.Pending || r.Status == ChatNotificationStatuses.Sent))
            .Where(anchor)
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var r in rows)
        {
            r.RespondedAt = now;

            // Already sent → the pump owes one "resolved" edit (Resolving).
            // Never sent → jump straight to terminal; there's no message to edit.
            r.Status = string.IsNullOrEmpty(r.ChatMessageId)
                ? terminalStatus
                : ChatNotificationStatuses.Resolving;
        }

        await this.control.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>Deterministic short hash (stable across process restarts, unlike <c>string.GetHashCode</c>).</summary>
    private static string StableHash(string? value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}
