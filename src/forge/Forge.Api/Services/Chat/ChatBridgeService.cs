// -----------------------------------------------------------------------
// <copyright file="ChatBridgeService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Common.Chat;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 95 (chat bridge) — the <b>outbound</b> pump. A <see cref="BackgroundService"/>
/// that on each tick (1) sends <c>pending</c> outbox rows to the chat platform
/// and stores the returned message id, and (2) edits the sent message of any row
/// resolved elsewhere (<c>resolving</c>) exactly once before flipping it
/// terminal. The inbound half lives in the reusable <c>Common.Chat</c>
/// <see cref="ChatInboundPump"/>, which fans updates out to
/// <see cref="ChatInboundDispatcher"/>.
///
/// <para><b>Failure-isolated:</b> a bad row can't abort the tick (per-row
/// try/catch), a bad tick can't kill the loop (per-tick try/catch). Transient
/// send failures increment <c>Attempts</c> and retry next tick; past
/// <see cref="ChatBridgeOptions.MaxAttempts"/> the row is marked <c>failed</c>.
/// A <c>pending</c> row past its <c>ExpiresAt</c> is marked <c>expired</c> and
/// never sent.</para>
///
/// <para>Tests drive <see cref="RunOutboundOnceAsync"/> directly (single tick),
/// mirroring the <c>RunOnceAsync</c> convention on the other hosted services.
/// The loop is gated off under <c>FORGE_TEST_GUARDRAIL</c> like the rest.</para>
/// </summary>
public sealed class ChatBridgeService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IChatTransport transport;
    private readonly IOptionsMonitor<ChatBridgeOptions> options;
    private readonly IOptionsMonitor<ChatTransportOptions> transportOptions;
    private readonly ILogger<ChatBridgeService> logger;
    private readonly IChatOperationGate? gate;
    private readonly SemaphoreSlim outboundGate = new(1, 1);

    public ChatBridgeService(
        IServiceScopeFactory scopeFactory,
        IChatTransport transport,
        IOptionsMonitor<ChatBridgeOptions> options,
        IOptionsMonitor<ChatTransportOptions> transportOptions,
        ILogger<ChatBridgeService> logger,
        IChatOperationGate? gate = null)
    {
        this.scopeFactory = scopeFactory;
        this.transport = transport;
        this.options = options;
        this.transportOptions = transportOptions;
        this.logger = logger;
        this.gate = gate;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (this.gate is not null) await this.gate.InitializeAsync(stoppingToken);
        try
        {
            await Task.Delay(this.options.CurrentValue.OutboundTickInterval, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opt = this.options.CurrentValue;
                if (opt.Enabled && !string.IsNullOrWhiteSpace(
                    ChatTokenResolver.Resolve(this.transportOptions.CurrentValue, this.logger)))
                {
                    await this.RunOutboundOnceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "chat outbound tick errored — continuing.");
            }

            try
            {
                await Task.Delay(this.options.CurrentValue.OutboundTickInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One outbound tick: send pending rows, then edit resolving rows. Returns
    /// per-outcome counts for deterministic tests.
    /// </summary>
    public async Task<OutboundTickResult> RunOutboundOnceAsync(CancellationToken ct)
    {
        await this.outboundGate.WaitAsync(ct);
        try { return await this.RunOwnedOutboundOnceAsync(ct); }
        finally { this.outboundGate.Release(); }
    }

    private async Task<OutboundTickResult> RunOwnedOutboundOnceAsync(CancellationToken ct)
    {
        using var lease = this.gate is null ? null : await this.gate.EnterAsync("legacy", ct);
        if (this.gate is not null && lease is null) return default;
        using var continuation = this.gate?.ContinueLease();
        using var scope = this.scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        // Optional — always registered in production; some unit tests build a
        // minimal provider without it (spawn-less rows never need a topic anyway).
        var channels = scope.ServiceProvider.GetService<IChatChannelMap>();
        var opt = this.options.CurrentValue;
        var now = DateTimeOffset.UtcNow;
        var result = default(OutboundTickResult);
        // This singleton's previous tick has finished. An intent left by another process has an unknown outcome.
        foreach (var interrupted in await control.ChatNotifications.Where(n => (n.Platform == "telegram" || n.Platform == "") && n.Status == "sending").ToListAsync(ct))
        { interrupted.Status = "uncertain"; interrupted.LastError = "legacy-send-outcome-unknown"; }
        if (control.ChangeTracker.HasChanges()) await control.SaveChangesAsync(ct);

        // ---- Send pass: pending → sent (or expired / failed) --------------
        // NOTE: order client-side — SQLite can't ORDER BY a DateTimeOffset
        // column (documented Forge SQLite gotcha), so materialize then sort.
        var pending = await control.ChatNotifications
            .Where(r => r.Status == ChatNotificationStatuses.Pending && (r.Platform == "telegram" || r.Platform == ""))
            .ToListAsync(ct);
        pending = pending.OrderBy(r => r.CreatedAt).ToList();

        foreach (var row in pending)
        {
            try
            {
                if (row.ExpiresAt is { } exp && exp <= now)
                {
                    row.Status = ChatNotificationStatuses.Expired;
                    result.Expired++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(row.ChatId))
                {
                    this.Fail(row, "no recipient chat id configured", opt, ref result);
                    continue;
                }

                // Record intent before creating a topic or sending. A crash cannot turn an unknown send into a fresh retry at cutover.
                row.Status = "sending";
                await control.SaveChangesAsync(ct);
                var (text, buttons) = ChatMessageRenderer.Render(row);
                if (!opt.AllowRemoteActions)
                {
                    buttons = null;
                    text += "\n\nRemote actions are disabled. Open Forge to respond.";
                }

                // Part B — route the alert into its spawn's forum topic (group
                // mode). Null threadId → group root (single-chat mode, spawn-less
                // alert, or topic-creation failure) — send is unaffected.
                // A missing ProductId would render a malformed `spawn//…` route
                // key that the channel store rejects, burning all send attempts
                // on a routing detail — fall back to the group root instead.
                // Topic display name derives from the SPAWN, not the alert title:
                // the topic hosts every alert kind for the story's lifetime, and
                // naming it after whichever alert happened to create it left
                // topics permanently titled "⚠ Stalled · Stage 1.1".
                var threadId = channels is not null
                    && row.SpawnId is { Length: > 0 } spawn
                    && row.ProductId is { Length: > 0 } prod
                    ? (await channels.EnsureChannelAsync(ForgeChatRoutes.Spawn(prod, spawn), SpawnTopicName(spawn), ct))?.ThreadId
                    : null;

                var send = await this.transport.SendAsync(row.ChatId, text, buttons, ct, threadId);
                if (send.Ok)
                {
                    row.Status = ChatNotificationStatuses.Sent;
                    row.ChatMessageId = send.MessageId;
                    row.SentAt = now;
                    row.LastError = null;
                    result.Sent++;
                }
                else if (send.OutcomeUnknown)
                {
                    row.Status = "uncertain"; row.LastError = "legacy-send-outcome-unknown"; row.Attempts++;
                }
                else
                {
                    row.Status = ChatNotificationStatuses.Pending;
                    this.Fail(row, send.Error ?? "send failed", opt, ref result);
                }
                await control.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException)
            {
                row.Status = "uncertain"; row.LastError = "legacy-send-outcome-unknown";
                await control.SaveChangesAsync(CancellationToken.None);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                row.Status = "uncertain"; row.LastError = "legacy-send-outcome-unknown"; row.Attempts++;
                await control.SaveChangesAsync(CancellationToken.None);
                this.logger.LogWarning("Chat send outcome is unknown for notification {Id}.", row.Id);
            }
        }

        // ---- Edit pass: resolving → terminal (exactly one edit) -----------
        var resolving = await control.ChatNotifications
            .Where(r => r.Status == ChatNotificationStatuses.Resolving && (r.Platform == "telegram" || r.Platform == ""))
            .ToListAsync(ct);

        foreach (var row in resolving)
        {
            var terminal = TerminalFor(row);
            try
            {
                if (string.IsNullOrWhiteSpace(row.ChatId) || string.IsNullOrWhiteSpace(row.ChatMessageId))
                {
                    row.Status = terminal;
                    continue;
                }

                var edit = await this.transport.EditAsync(
                    row.ChatId, row.ChatMessageId, ChatMessageRenderer.RenderResolved(row), buttons: null, ct);
                if (edit.Ok)
                {
                    row.Status = terminal;
                    row.LastError = null;
                    result.Edited++;
                }
                else
                {
                    row.Attempts++;
                    row.LastError = edit.Error;
                    // The edit is best-effort — never wedge a resolved item on it.
                    if (row.Attempts >= opt.MaxAttempts)
                    {
                        row.Status = terminal;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                row.Attempts++;
                row.LastError = ex.Message;
                if (row.Attempts >= opt.MaxAttempts)
                {
                    row.Status = terminal;
                }

                this.logger.LogWarning(ex, "chat edit threw for notification {Id}", row.Id);
            }
        }

        await control.SaveChangesAsync(ct);
        return result;
    }

    private void Fail(ChatNotificationRow row, string error, ChatBridgeOptions opt, ref OutboundTickResult result)
    {
        row.Attempts++;
        row.LastError = error;
        if (row.Attempts >= opt.MaxAttempts)
        {
            row.Status = ChatNotificationStatuses.Failed;
            result.Failed++;
        }
    }

    private static string TerminalFor(ChatNotificationRow row) =>
        row.Kind == "open-questions" ? ChatNotificationStatuses.Answered : ChatNotificationStatuses.Acted;

    /// <summary>
    /// Stable display name for a spawn's forum topic, derived from the spawn id
    /// (<c>spawn-release-RELEASE-PROVIDER</c> → <c>📖 release-RELEASE-PROVIDER</c>).
    /// Deterministic and DB-free — the name only matters on first topic creation,
    /// and deriving it from the alert instead named topics after whichever alert
    /// arrived first.
    /// </summary>
    internal static string SpawnTopicName(string spawnId) =>
        "📖 " + (spawnId.StartsWith("spawn-", StringComparison.Ordinal) ? spawnId["spawn-".Length..] : spawnId);
}

/// <summary>Per-tick outcome counts returned by <see cref="ChatBridgeService.RunOutboundOnceAsync"/>.</summary>
public struct OutboundTickResult
{
    public int Sent;
    public int Edited;
    public int Expired;
    public int Failed;
}
