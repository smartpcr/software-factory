// -----------------------------------------------------------------------
// <copyright file="ChatEngineNotifier.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Microsoft.Extensions.Options;

/// <summary>Engine selection also gates production of legacy plaintext rows, not just the two legacy pumps.</summary>
public sealed class ChatEngineNotifier(ChatNotifier legacy, ChatTelegramSliceService slice, ChatEventService events, IOptions<ChatPluginOptions> options, ChatEngineCoordinator engine, ChatMigrationRouting migration) : IChatNotifier
{
    private bool Legacy => options.Value.Engine == "legacy";
    public Task<bool> EnqueueOpenQuestionsAsync(OpenQuestionsAlert alert, CancellationToken ct)
        => this.EnqueueAsync(new("open-questions", $"{alert.WorkItemId}:{alert.Iter}:{ChatEventCatalog.Hash(alert.Body)}", alert.ProductId,
            alert.SpawnId, alert.WorkItemId, RepoId: alert.RepoId, Revision: alert.Iter,
            LegacyDedupeKey: $"oq:{alert.WorkItemId}:{ChatNotifier.StableHash(alert.Body)}"),
            () => legacy.EnqueueOpenQuestionsAsync(alert, ct), ct, () => slice.EnqueueAsync(alert, ct));
    public Task<bool> EnqueueWorkstreamStalledAsync(WorkstreamStalledAlert alert, CancellationToken ct)
        => this.EnqueueAsync(new("workstream-stalled", $"{alert.WorkstreamId}:{ChatEventCatalog.Hash(alert.StallReason)}", alert.ProductId,
            alert.SpawnId, WorkstreamId: alert.WorkstreamId, RepoId: alert.RepoId, LegacyDedupeKey: $"stall:{alert.WorkstreamId}:{ChatNotifier.StableHash(alert.StallReason)}"),
            () => legacy.EnqueueWorkstreamStalledAsync(alert, ct), ct);
    public Task<bool> EnqueueQueueStrainAsync(QueueStrainAlert alert, CancellationToken ct)
        => this.EnqueueAsync(new("queue-strain", alert.CrewId + ":" + alert.QueueId, LegacyDedupeKey: "strain:" + alert.CrewId),
            () => legacy.EnqueueQueueStrainAsync(alert, ct), ct);
    public Task<bool> EnqueueHumanInteractionAsync(HumanInteractionAlert alert, CancellationToken ct)
        => this.EnqueueAsync(new("human-interaction", alert.InteractionKey, LegacyDedupeKey: "interaction:" + alert.InteractionKey),
            () => legacy.EnqueueHumanInteractionAsync(alert, ct), ct);
    // Metadata-only notifications promise no remote controls or resolution edits in Stage 1S.
    public Task<int> MarkOpenQuestionsResolvedAsync(string workItemId, CancellationToken ct)
        => this.RouteAsync(() => this.Legacy ? legacy.MarkOpenQuestionsResolvedAsync(workItemId, ct) : Task.FromResult(0), ct);
    public Task<int> MarkWorkstreamResolvedAsync(string workstreamId, CancellationToken ct)
        => this.RouteAsync(() => this.Legacy ? legacy.MarkWorkstreamResolvedAsync(workstreamId, ct) : Task.FromResult(0), ct);
    private Task<bool> EnqueueAsync(ChatEventInput input, Func<Task<bool>> writeLegacy, CancellationToken ct, Func<Task<bool>>? writeSlice = null)
        => this.RouteAsync(async () =>
        {
            if (await migration.SuppressAsync(input, input.LegacyDedupeKey!, this.Legacy, ct)) return false;
            if (this.Legacy) return await writeLegacy();
            if (options.Value.TelegramSlice.Enabled) return writeSlice is not null && await writeSlice();
            return await events.EnqueueAsync(input, ct);
        }, ct);
    private async Task<T> RouteAsync<T>(Func<Task<T>> write, CancellationToken ct)
    {
        await engine.InitializeAsync(ct);
        await engine.Routing.WaitAsync(ct);
        try { return await write(); }
        finally { engine.Routing.Release(); }
    }
}