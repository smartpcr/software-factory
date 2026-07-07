// -----------------------------------------------------------------------
// <copyright file="ForgeChatCursorStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Common.Chat;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Phase 95 (Phase 4) — Forge's <see cref="IChatCursorStore"/>: the durable
/// inbound long-poll cursor over the control-DB <c>chat_bridge_state</c>
/// singleton row, so a restart resumes instead of reprocessing (or re-acting
/// on) the 24h backlog Telegram redelivers.
/// </summary>
public sealed class ForgeChatCursorStore : IChatCursorStore
{
    private readonly ControlDbContext control;

    public ForgeChatCursorStore(ControlDbContext control)
    {
        this.control = control;
    }

    /// <inheritdoc/>
    public async Task<long> LoadAsync(CancellationToken ct)
    {
        var row = await this.control.ChatBridgeState.AsNoTracking().FirstOrDefaultAsync(ct);
        return row?.LastUpdateId ?? 0;
    }

    /// <inheritdoc/>
    public async Task SaveAsync(long lastUpdateId, CancellationToken ct)
    {
        var row = await this.control.ChatBridgeState.FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new ChatBridgeStateRow { Id = ChatBridgeStateRow.SingletonId };
            this.control.ChatBridgeState.Add(row);
        }

        row.LastUpdateId = lastUpdateId;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await this.control.SaveChangesAsync(ct);
    }
}
