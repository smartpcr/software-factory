// -----------------------------------------------------------------------
// <copyright file="ChatInboundPump.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// An inbound update enriched with the app route resolved from the channel it
/// arrived in (<see cref="IChatChannelMap.ResolveRouteAsync"/>). RouteKey is
/// null for the group root, DMs, and unmapped threads — handlers then fall
/// back to their own correlation (e.g. reply-to-message ids).
/// </summary>
public sealed record ChatInboundMessage(ChatInbound Update, string? RouteKey);

/// <summary>
/// The subscription seam: apps register one or more handlers (scoped DI) and
/// the <see cref="ChatInboundPump"/> fans every inbound update out to all of
/// them. A handler must not throw for "not mine" updates — just return.
/// </summary>
public interface IChatInboundHandler
{
    /// <summary>Handle one inbound update. Exceptions are caught + logged per handler.</summary>
    Task HandleAsync(ChatInboundMessage message, CancellationToken ct);
}

/// <summary>
/// Durable cursor seam so the long-poll never re-processes an update after a
/// restart. Apps back it with their own store; the in-memory default is for
/// tests / apps that tolerate at-least-once on restart.
/// </summary>
public interface IChatCursorStore
{
    /// <summary>The last processed update id (0 when none).</summary>
    Task<long> LoadAsync(CancellationToken ct);

    /// <summary>Persist the last processed update id.</summary>
    Task SaveAsync(long lastUpdateId, CancellationToken ct);
}

/// <summary>Volatile <see cref="IChatCursorStore"/> — register as a singleton.</summary>
public sealed class InMemoryChatCursorStore : IChatCursorStore
{
    private long lastUpdateId;

    /// <inheritdoc/>
    public Task<long> LoadAsync(CancellationToken ct) => Task.FromResult(Interlocked.Read(ref this.lastUpdateId));

    /// <inheritdoc/>
    public Task SaveAsync(long lastUpdateId, CancellationToken ct)
    {
        Interlocked.Exchange(ref this.lastUpdateId, lastUpdateId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The inbound long-poll loop: poll the transport for updates newer than the
/// persisted cursor, resolve each update's channel to an app route, fan out to
/// every registered <see cref="IChatInboundHandler"/>, then advance the
/// cursor. Idles (with backoff) while the bridge is disabled or has no token.
///
/// <para><b>Failure-isolated:</b> a throwing handler can't stop the other
/// handlers (per-handler try/catch), a bad poll can't kill the loop (per-tick
/// try/catch + backoff). Tests drive <see cref="RunOnceAsync"/> /
/// <see cref="HandleUpdatesAsync"/> directly for deterministic single ticks.</para>
/// </summary>
public sealed class ChatInboundPump : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IChatTransport transport;
    private readonly IOptionsMonitor<ChatTransportOptions> options;
    private readonly ILogger<ChatInboundPump> logger;
    private readonly IChatOperationGate? gate;

    public ChatInboundPump(
        IServiceScopeFactory scopeFactory,
        IChatTransport transport,
        IOptionsMonitor<ChatTransportOptions> options,
        ILogger<ChatInboundPump> logger,
        IChatOperationGate? gate = null)
    {
        this.scopeFactory = scopeFactory;
        this.transport = transport;
        this.options = options;
        this.logger = logger;
        this.gate = gate;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (this.gate is not null) await this.gate.InitializeAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = this.options.CurrentValue;
            if (!opt.Enabled || string.IsNullOrWhiteSpace(ChatTokenResolver.Resolve(opt, this.logger)))
            {
                // Idle — the long-poll can't run without a token; back off.
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (TaskCanceledException) { return; }
                continue;
            }

            try
            {
                // The long-poll (getUpdates?timeout=…) provides the pacing.
                await this.RunOnceAsync(stoppingToken);
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
    /// dispatch each, then advance the cursor. Returns the update count.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var lease = this.gate is null ? null : await this.gate.EnterAsync("legacy", ct);
        if (this.gate is not null && lease is null) return 0;
        using var continuation = this.gate?.ContinueLease();
        var offset = await this.LoadCursorAsync(ct);
        var pollOffset = offset == 0 ? 0 : offset + 1;
        var updates = await this.transport.PollUpdatesAsync(pollOffset, ct);
        if (updates.Count == 0)
        {
            return 0;
        }

        await this.HandleUpdatesAsync(updates, ct);

        var maxId = updates.Max(u => u.UpdateId);
        if (maxId > offset)
        {
            await this.SaveCursorAsync(maxId, ct);
        }

        return updates.Count;
    }

    /// <summary>Dispatch a batch of inbound updates (public for deterministic tests).</summary>
    public async Task HandleUpdatesAsync(IReadOnlyList<ChatInbound> updates, CancellationToken ct)
    {
        foreach (var update in updates)
        {
            using var scope = this.scopeFactory.CreateScope();

            // Which app route (if any) owns the channel this update arrived in?
            string? routeKey = null;
            var map = scope.ServiceProvider.GetService<IChatChannelMap>();
            if (map is not null)
            {
                try
                {
                    routeKey = await map.ResolveRouteAsync(update.ChatId, update.ThreadId, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger.LogWarning(ex, "chat route resolution failed for update {UpdateId} — dispatching unrouted.", update.UpdateId);
                }
            }

            var message = new ChatInboundMessage(update, routeKey);
            foreach (var handler in scope.ServiceProvider.GetServices<IChatInboundHandler>())
            {
                try
                {
                    await handler.HandleAsync(message, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.logger.LogWarning(ex, "chat inbound handler {Handler} failed for update {UpdateId}.", handler.GetType().Name, update.UpdateId);
                }
            }
        }
    }

    private async Task<long> LoadCursorAsync(CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var cursor = scope.ServiceProvider.GetService<IChatCursorStore>();
        return cursor is null ? 0 : await cursor.LoadAsync(ct);
    }

    private async Task SaveCursorAsync(long lastUpdateId, CancellationToken ct)
    {
        using var scope = this.scopeFactory.CreateScope();
        var cursor = scope.ServiceProvider.GetService<IChatCursorStore>();
        if (cursor is not null)
        {
            await cursor.SaveAsync(lastUpdateId, ct);
        }
    }
}
