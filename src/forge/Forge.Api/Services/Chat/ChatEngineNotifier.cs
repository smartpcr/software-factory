// -----------------------------------------------------------------------
// <copyright file="ChatEngineNotifier.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Microsoft.Extensions.Options;

/// <summary>Engine selection also gates production of legacy plaintext rows, not just the two legacy pumps.</summary>
public sealed class ChatEngineNotifier(ChatNotifier legacy, ChatTelegramSliceService slice, IOptions<ChatPluginOptions> options) : IChatNotifier
{
    private bool Legacy => options.Value.Engine == "legacy";
    public Task<bool> EnqueueOpenQuestionsAsync(OpenQuestionsAlert alert, CancellationToken ct)
        => this.Legacy ? legacy.EnqueueOpenQuestionsAsync(alert, ct) : options.Value.PluginsEnabled ? slice.EnqueueAsync(alert, ct) : Task.FromResult(false);
    public Task<bool> EnqueueWorkstreamStalledAsync(WorkstreamStalledAlert alert, CancellationToken ct)
        => this.Legacy ? legacy.EnqueueWorkstreamStalledAsync(alert, ct) : Task.FromResult(false);
    public Task<bool> EnqueueQueueStrainAsync(QueueStrainAlert alert, CancellationToken ct)
        => this.Legacy ? legacy.EnqueueQueueStrainAsync(alert, ct) : Task.FromResult(false);
    public Task<bool> EnqueueHumanInteractionAsync(HumanInteractionAlert alert, CancellationToken ct)
        => this.Legacy ? legacy.EnqueueHumanInteractionAsync(alert, ct) : Task.FromResult(false);
    // Metadata-only notifications promise no remote controls or resolution edits in Stage 1S.
    public Task<int> MarkOpenQuestionsResolvedAsync(string workItemId, CancellationToken ct)
        => this.Legacy ? legacy.MarkOpenQuestionsResolvedAsync(workItemId, ct) : Task.FromResult(0);
    public Task<int> MarkWorkstreamResolvedAsync(string workstreamId, CancellationToken ct)
        => this.Legacy ? legacy.MarkWorkstreamResolvedAsync(workstreamId, ct) : Task.FromResult(0);
}