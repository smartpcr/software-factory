// -----------------------------------------------------------------------
// <copyright file="ChatChannelMap.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// A persisted mapping between an app-defined <b>route key</b> (opaque to this
/// library — e.g. Forge encodes <c>spawn/{productId}/{spawnId}</c>) and a chat
/// channel address (chat + optional forum-topic thread). This is how an
/// inbound message from a channel is mapped back to the correct
/// sender/receiver inside the app, and how outbound sends land in the right
/// per-route topic.
/// </summary>
public sealed record ChatChannelBinding(
    string RouteKey,
    string ChatId,
    string? ThreadId,
    string? Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt = null);

/// <summary>A send target: chat id + optional forum-topic thread.</summary>
public sealed record ChatChannelAddress(string ChatId, string? ThreadId);

/// <summary>
/// Persistence seam for channel bindings — the host app implements it over its
/// own store (Forge: the control-DB <c>chat_spawn_topics</c> table). All
/// methods are expected to be race-tolerant, not throwing on duplicates.
/// </summary>
public interface IChatChannelStore
{
    /// <summary>The binding for an app route, or null when none exists yet.</summary>
    Task<ChatChannelBinding?> FindByRouteAsync(string routeKey, CancellationToken ct);

    /// <summary>The binding owning a channel address (reverse lookup), or null.</summary>
    Task<ChatChannelBinding?> FindByAddressAsync(string chatId, string threadId, CancellationToken ct);

    /// <summary>
    /// Persist a new binding. On a uniqueness race (another writer bound the
    /// route first) returns the winning binding instead of throwing.
    /// </summary>
    Task<ChatChannelBinding> SaveAsync(ChatChannelBinding binding, CancellationToken ct);

    /// <summary>Mark a route's binding closed (best-effort; local mapping only).</summary>
    Task CloseAsync(string routeKey, CancellationToken ct);
}

/// <summary>
/// Maps app routes to chat channels both ways: outbound
/// (<see cref="EnsureChannelAsync"/> — create a forum topic on first need) and
/// inbound (<see cref="ResolveRouteAsync"/> — which app route does this
/// chat/thread belong to). Returns null addresses in single-chat / DM mode
/// (<see cref="TelegramOptions.SupergroupChatId"/> == 0) or when topic
/// creation fails — callers then post to the group root.
/// </summary>
public interface IChatChannelMap
{
    /// <summary>The channel address for a route (create-on-first-need), or null → no per-route channel.</summary>
    Task<ChatChannelAddress?> EnsureChannelAsync(string routeKey, string displayName, CancellationToken ct);

    /// <summary>The app route key bound to an inbound chat/thread, or null when unmapped.</summary>
    Task<string?> ResolveRouteAsync(string chatId, string? threadId, CancellationToken ct);

    /// <summary>Mark a route's channel closed (best-effort).</summary>
    Task CloseChannelAsync(string routeKey, CancellationToken ct);
}

/// <inheritdoc/>
public sealed class ChatChannelMap : IChatChannelMap
{
    private readonly IChatChannelStore store;
    private readonly IChatTransport transport;
    private readonly IOptionsMonitor<ChatTransportOptions> options;
    private readonly ILogger<ChatChannelMap> logger;

    public ChatChannelMap(
        IChatChannelStore store,
        IChatTransport transport,
        IOptionsMonitor<ChatTransportOptions> options,
        ILogger<ChatChannelMap> logger)
    {
        this.store = store;
        this.transport = transport;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ChatChannelAddress?> EnsureChannelAsync(string routeKey, string displayName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(routeKey)) return null;

        var groupChatId = this.options.CurrentValue.Telegram.SupergroupChatId;
        if (groupChatId == 0) return null; // single-chat / DM mode — no per-route topics.

        var existing = await this.store.FindByRouteAsync(routeKey, ct);
        if (existing is not null && !string.IsNullOrEmpty(existing.ThreadId))
        {
            return new ChatChannelAddress(existing.ChatId, existing.ThreadId);
        }

        var chatId = groupChatId.ToString();
        var name = string.IsNullOrWhiteSpace(displayName) ? routeKey : displayName;
        var threadId = await this.transport.CreateTopicAsync(chatId, name, ct);
        if (string.IsNullOrEmpty(threadId))
        {
            this.logger.LogWarning("Could not create a chat topic for route {Route}; falling back to the group root.", routeKey);
            return null;
        }

        var winner = await this.store.SaveAsync(
            new ChatChannelBinding(routeKey, chatId, threadId, name, DateTimeOffset.UtcNow), ct);
        return new ChatChannelAddress(winner.ChatId, winner.ThreadId);
    }

    /// <inheritdoc/>
    public async Task<string?> ResolveRouteAsync(string chatId, string? threadId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(threadId))
        {
            return null; // group root / DM — no per-route channel to resolve.
        }

        var binding = await this.store.FindByAddressAsync(chatId, threadId, ct);
        return binding?.RouteKey;
    }

    /// <inheritdoc/>
    public Task CloseChannelAsync(string routeKey, CancellationToken ct) => this.store.CloseAsync(routeKey, ct);
}
