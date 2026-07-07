// -----------------------------------------------------------------------
// <copyright file="ForgeChatChannelStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Common.Chat;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 95 (Part B) — Forge's <see cref="IChatChannelStore"/>: persists the
/// spawn ⇄ forum-topic bindings in the control-DB <c>chat_spawn_topics</c>
/// table (the supergroup is global). Route keys follow
/// <see cref="ForgeChatRoutes"/> (<c>spawn/{productId}/{spawnId}</c>); rows
/// keep the split (SpawnId PK + ProductId column), so no migration is needed.
/// </summary>
public sealed class ForgeChatChannelStore : IChatChannelStore
{
    private readonly ControlDbContext control;

    public ForgeChatChannelStore(ControlDbContext control)
    {
        this.control = control;
    }

    /// <inheritdoc/>
    public async Task<ChatChannelBinding?> FindByRouteAsync(string routeKey, CancellationToken ct)
    {
        if (!ForgeChatRoutes.TryParseSpawn(routeKey, out _, out var spawnId))
        {
            return null;
        }

        var row = await this.control.ChatSpawnTopics.AsNoTracking()
            .FirstOrDefaultAsync(t => t.SpawnId == spawnId, ct);
        return ToBinding(row);
    }

    /// <inheritdoc/>
    public async Task<ChatChannelBinding?> FindByAddressAsync(string chatId, string threadId, CancellationToken ct)
    {
        var row = await this.control.ChatSpawnTopics.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ChatId == chatId && t.ThreadId == threadId, ct);
        return ToBinding(row);
    }

    /// <inheritdoc/>
    public async Task<ChatChannelBinding> SaveAsync(ChatChannelBinding binding, CancellationToken ct)
    {
        if (!ForgeChatRoutes.TryParseSpawn(binding.RouteKey, out var productId, out var spawnId))
        {
            throw new ArgumentException($"Not a Forge spawn route key: '{binding.RouteKey}'", nameof(binding));
        }

        var existing = await this.control.ChatSpawnTopics.FirstOrDefaultAsync(t => t.SpawnId == spawnId, ct);
        var row = existing ?? new ChatSpawnTopicRow { SpawnId = spawnId };
        row.ProductId = productId;
        row.ChatId = binding.ChatId;
        row.ThreadId = binding.ThreadId ?? string.Empty;
        row.Name = binding.Name ?? string.Empty;
        row.CreatedAt = binding.CreatedAt;
        if (existing is null)
        {
            this.control.ChatSpawnTopics.Add(row);
        }

        try
        {
            await this.control.SaveChangesAsync(ct);
            return binding;
        }
        catch (DbUpdateException)
        {
            // Concurrency race — another tick persisted first. Re-read the winner.
            this.control.Entry(row).State = EntityState.Detached;
            var winner = await this.control.ChatSpawnTopics.AsNoTracking()
                .FirstOrDefaultAsync(t => t.SpawnId == spawnId, ct);
            return ToBinding(winner) ?? binding;
        }
    }

    /// <inheritdoc/>
    public async Task CloseAsync(string routeKey, CancellationToken ct)
    {
        if (!ForgeChatRoutes.TryParseSpawn(routeKey, out _, out var spawnId))
        {
            return;
        }

        var row = await this.control.ChatSpawnTopics.FirstOrDefaultAsync(t => t.SpawnId == spawnId, ct);
        if (row is null || row.ClosedAt is not null)
        {
            return;
        }

        row.ClosedAt = DateTimeOffset.UtcNow;
        await this.control.SaveChangesAsync(ct);
    }

    private static ChatChannelBinding? ToBinding(ChatSpawnTopicRow? row) =>
        row is null || string.IsNullOrEmpty(row.ThreadId)
            ? null
            : new ChatChannelBinding(
                ForgeChatRoutes.Spawn(row.ProductId, row.SpawnId),
                row.ChatId,
                row.ThreadId,
                row.Name,
                row.CreatedAt,
                row.ClosedAt);
}
