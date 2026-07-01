// -----------------------------------------------------------------------
// <copyright file="ChatBridgeService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 95 (chat bridge) — the outbound pump. A <see cref="BackgroundService"/>
/// that on each tick (1) sends <c>pending</c> outbox rows to the chat platform
/// and stores the returned message id, and (2) edits the sent message of any row
/// resolved elsewhere (<c>resolving</c>) exactly once before flipping it
/// terminal.
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
    private readonly ILogger<ChatBridgeService> logger;

    public ChatBridgeService(
        IServiceScopeFactory scopeFactory,
        IChatTransport transport,
        IOptionsMonitor<ChatBridgeOptions> options,
        ILogger<ChatBridgeService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.transport = transport;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Two independent loops: the outbound pump (timer) and the inbound
        // long-poll. A fault in one must not stop the other.
        await Task.WhenAll(
            this.OutboundLoopAsync(stoppingToken),
            this.InboundLoopAsync(stoppingToken));
    }

    private async Task OutboundLoopAsync(CancellationToken stoppingToken)
    {
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
                if (opt.Enabled && !string.IsNullOrWhiteSpace(ChatTokenResolver.Resolve(opt, this.logger)))
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

    private async Task InboundLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = this.options.CurrentValue;
            var hasToken = !string.IsNullOrWhiteSpace(ChatTokenResolver.Resolve(opt, this.logger));
            if (!opt.Enabled || !hasToken)
            {
                // Idle — the long-poll can't run without a token; back off.
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (TaskCanceledException) { return; }
                continue;
            }

            try
            {
                // The long-poll (getUpdates?timeout=…) provides the pacing.
                await this.RunInboundOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "chat inbound tick errored — continuing.");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (TaskCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// One inbound tick: long-poll for updates newer than the persisted cursor,
    /// route each, then advance the cursor. Returns the update count.
    /// </summary>
    public async Task<int> RunInboundOnceAsync(CancellationToken ct)
    {
        var offset = await this.LoadOffsetAsync(ct);
        var pollOffset = offset == 0 ? 0 : offset + 1;
        var updates = await this.transport.PollUpdatesAsync(pollOffset, ct);
        if (updates.Count == 0)
        {
            return 0;
        }

        await this.HandleInboundAsync(updates, ct);

        var maxId = updates.Max(u => u.UpdateId);
        if (maxId > offset)
        {
            await this.SaveOffsetAsync(maxId, ct);
        }

        return updates.Count;
    }

    /// <summary>Route a batch of inbound updates (public for deterministic tests).</summary>
    public async Task HandleInboundAsync(IReadOnlyList<ChatInbound> updates, CancellationToken ct)
    {
        foreach (var update in updates)
        {
            using var scope = this.scopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<ChatInboundDispatcher>();
            await dispatcher.DispatchAsync(update, ct);
        }
    }

    private async Task<long> LoadOffsetAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var row = await control.ChatBridgeState.AsNoTracking().FirstOrDefaultAsync(ct);
        return row?.LastUpdateId ?? 0;
    }

    private async Task SaveOffsetAsync(long lastUpdateId, CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var row = await control.ChatBridgeState.FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new ChatBridgeStateRow { Id = ChatBridgeStateRow.SingletonId };
            control.ChatBridgeState.Add(row);
        }

        row.LastUpdateId = lastUpdateId;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await control.SaveChangesAsync(ct);
    }

    /// <summary>
    /// One outbound tick: send pending rows, then edit resolving rows. Returns
    /// per-outcome counts for deterministic tests.
    /// </summary>
    public async Task<OutboundTickResult> RunOutboundOnceAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var opt = this.options.CurrentValue;
        var now = DateTimeOffset.UtcNow;
        var result = default(OutboundTickResult);

        // ---- Send pass: pending → sent (or expired / failed) --------------
        // NOTE: order client-side — SQLite can't ORDER BY a DateTimeOffset
        // column (documented Forge SQLite gotcha), so materialize then sort.
        var pending = await control.ChatNotifications
            .Where(r => r.Status == ChatNotificationStatuses.Pending)
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

                var (text, buttons) = ChatMessageRenderer.Render(row);
                var send = await this.transport.SendAsync(row.ChatId, text, buttons, ct);
                if (send.Ok)
                {
                    row.Status = ChatNotificationStatuses.Sent;
                    row.ChatMessageId = send.MessageId;
                    row.SentAt = now;
                    row.LastError = null;
                    result.Sent++;
                }
                else
                {
                    this.Fail(row, send.Error ?? "send failed", opt, ref result);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.Fail(row, ex.Message, opt, ref result);
                this.logger.LogWarning(ex, "chat send threw for notification {Id}", row.Id);
            }
        }

        // ---- Edit pass: resolving → terminal (exactly one edit) -----------
        var resolving = await control.ChatNotifications
            .Where(r => r.Status == ChatNotificationStatuses.Resolving)
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
}

/// <summary>Per-tick outcome counts returned by <see cref="ChatBridgeService.RunOutboundOnceAsync"/>.</summary>
public struct OutboundTickResult
{
    public int Sent;
    public int Edited;
    public int Expired;
    public int Failed;
}
