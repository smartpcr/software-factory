// -----------------------------------------------------------------------
// <copyright file="ChatTopicResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 95 (chat bridge, Part B) — resolves a story spawn to its Telegram forum
/// topic (creating + caching the topic on first need). Returns null when the
/// bridge is in single-chat / DM mode (<c>SupergroupChatId == 0</c>) or topic
/// creation fails — the caller then posts to the group root.
/// </summary>
public interface IChatTopicResolver
{
    /// <summary>Thread id for the spawn's topic (create-on-first-need), or null → group root.</summary>
    Task<string?> EnsureTopicAsync(string spawnId, string productId, string topicName, CancellationToken ct);

    /// <summary>Mark a spawn's topic closed (best-effort; local mapping only).</summary>
    Task CloseTopicAsync(string spawnId, CancellationToken ct);
}

/// <inheritdoc/>
public sealed class ChatTopicResolver : IChatTopicResolver
{
    private readonly ControlDbContext control;
    private readonly IChatTransport transport;
    private readonly IOptionsMonitor<ChatBridgeOptions> options;
    private readonly ILogger<ChatTopicResolver> logger;

    public ChatTopicResolver(
        ControlDbContext control,
        IChatTransport transport,
        IOptionsMonitor<ChatBridgeOptions> options,
        ILogger<ChatTopicResolver> logger)
    {
        this.control = control;
        this.transport = transport;
        this.options = options;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task<string?> EnsureTopicAsync(string spawnId, string productId, string topicName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(spawnId)) return null;

        var chatId = this.options.CurrentValue.Telegram.SupergroupChatId;
        if (chatId == 0) return null; // single-chat / DM mode — no per-spawn topics.

        var existing = await this.control.ChatSpawnTopics.FirstOrDefaultAsync(t => t.SpawnId == spawnId, ct);
        if (existing is not null && !string.IsNullOrEmpty(existing.ThreadId))
        {
            return existing.ThreadId;
        }

        var chatIdStr = chatId.ToString();
        var name = string.IsNullOrWhiteSpace(topicName) ? $"spawn {spawnId}" : topicName;
        var threadId = await this.transport.CreateTopicAsync(chatIdStr, name, ct);
        if (string.IsNullOrEmpty(threadId))
        {
            this.logger.LogWarning("Could not create a forum topic for spawn {Spawn}; falling back to the group root.", spawnId);
            return null;
        }

        var row = existing ?? new ChatSpawnTopicRow { SpawnId = spawnId };
        row.ProductId = productId;
        row.ChatId = chatIdStr;
        row.ThreadId = threadId;
        row.Name = name;
        row.CreatedAt = DateTimeOffset.UtcNow;
        if (existing is null) this.control.ChatSpawnTopics.Add(row);

        try
        {
            await this.control.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Concurrency race — another tick persisted first. Re-read the winner.
            this.control.Entry(row).State = EntityState.Detached;
            var winner = await this.control.ChatSpawnTopics.AsNoTracking().FirstOrDefaultAsync(t => t.SpawnId == spawnId, ct);
            return winner is not null && !string.IsNullOrEmpty(winner.ThreadId) ? winner.ThreadId : threadId;
        }

        return threadId;
    }

    /// <inheritdoc/>
    public async Task CloseTopicAsync(string spawnId, CancellationToken ct)
    {
        var row = await this.control.ChatSpawnTopics.FirstOrDefaultAsync(t => t.SpawnId == spawnId, ct);
        if (row is null || row.ClosedAt is not null) return;
        row.ClosedAt = DateTimeOffset.UtcNow;
        await this.control.SaveChangesAsync(ct);
    }
}
