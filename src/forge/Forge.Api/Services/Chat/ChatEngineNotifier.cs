// -----------------------------------------------------------------------
// <copyright file="ChatEngineNotifier.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Microsoft.Extensions.Options;

/// <summary>Engine selection also gates production of legacy plaintext rows, not just the two legacy pumps.</summary>
public sealed class ChatEngineNotifier(ChatNotifier legacy, ChatTelegramSliceService slice, ChatEventService events, IOptions<ChatPluginOptions> options) : IChatNotifier
{
    private bool Legacy => options.Value.Engine == "legacy";
    public Task<bool> EnqueueOpenQuestionsAsync(OpenQuestionsAlert alert, CancellationToken ct)
        => this.Legacy ? legacy.EnqueueOpenQuestionsAsync(alert, ct) : options.Value.TelegramSlice.Enabled ? slice.EnqueueAsync(alert, ct) : events.EnqueueAsync(new("open-questions", $"{alert.WorkItemId}:{alert.Iter}:{ChatEventCatalog.Hash(alert.Body)}", alert.ProductId, alert.SpawnId, alert.WorkItemId, RepoId: alert.RepoId, Revision: alert.Iter), ct);
    public Task<bool> EnqueueWorkstreamStalledAsync(WorkstreamStalledAlert alert, CancellationToken ct)
        => this.Legacy ? legacy.EnqueueWorkstreamStalledAsync(alert, ct) : options.Value.TelegramSlice.Enabled ? Task.FromResult(false) : events.EnqueueAsync(new("workstream-stalled", $"{alert.WorkstreamId}:{ChatEventCatalog.Hash(alert.StallReason)}", alert.ProductId, alert.SpawnId, WorkstreamId: alert.WorkstreamId, RepoId: alert.RepoId), ct);
    public Task<bool> EnqueueQueueStrainAsync(QueueStrainAlert alert, CancellationToken ct)
        => this.Legacy ? legacy.EnqueueQueueStrainAsync(alert, ct) : options.Value.TelegramSlice.Enabled ? Task.FromResult(false) : events.EnqueueAsync(new("queue-strain", alert.CrewId + ":" + alert.QueueId), ct);
    public Task<bool> EnqueueHumanInteractionAsync(HumanInteractionAlert alert, CancellationToken ct)
        => this.Legacy ? legacy.EnqueueHumanInteractionAsync(alert, ct) : options.Value.TelegramSlice.Enabled ? Task.FromResult(false) : events.EnqueueAsync(new("human-interaction", alert.InteractionKey), ct);
    // Metadata-only notifications promise no remote controls or resolution edits in Stage 1S.
    public Task<int> MarkOpenQuestionsResolvedAsync(string workItemId, CancellationToken ct)
        => this.Legacy ? legacy.MarkOpenQuestionsResolvedAsync(workItemId, ct) : Task.FromResult(0);
    public Task<int> MarkWorkstreamResolvedAsync(string workstreamId, CancellationToken ct)
        => this.Legacy ? legacy.MarkWorkstreamResolvedAsync(workstreamId, ct) : Task.FromResult(0);
}