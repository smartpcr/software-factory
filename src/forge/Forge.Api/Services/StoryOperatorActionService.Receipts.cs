// -----------------------------------------------------------------------
// <copyright file="StoryOperatorActionService.Receipts.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Auth;
using Forge.Api.Services.Chat;

public sealed partial class StoryOperatorActionService
{
    private Task<IResult> WithReceiptAsync(string action, string storyId, string? key, Func<Task<IResult>> execute, CancellationToken ct)
        => services.GetRequiredService<ChatDomainActionRunner>().RunAsync(action, new(StoryId: storyId, ArtifactKey: key?.ToLowerInvariant()), execute,
            reason => Results.Problem(title: reason, statusCode: 409), result => result is not IStatusCodeHttpResult status || status.StatusCode is null or < 400, ct);
    public Task<IResult> PauseSpawnAsync(string storyId, CancellationToken ct)
        => this.WithReceiptAsync("spawn.pause", storyId, null, () => this.PauseSpawnCoreAsync(storyId, ct), ct);
    public Task<IResult> ResumeSpawnAsync(string storyId, CancellationToken ct)
        => this.WithReceiptAsync("spawn.resume", storyId, null, () => this.ResumeSpawnCoreAsync(storyId, ct), ct);
    public Task<IResult> ResumeArtifactAsync(string storyId, string key, CancellationToken ct)
        => this.WithReceiptAsync("artifact.retry", storyId, key, () => this.ResumeArtifactCoreAsync(storyId, key, ct), ct);
    public Task<IResult> RetryArtifactAsync(string storyId, string key, CancellationToken ct)
        => this.WithReceiptAsync("artifact.retry", storyId, key, () => this.RetryArtifactCoreAsync(storyId, key, ct), ct);
    public Task<IResult> CancelArtifactAsync(string storyId, string key, CancellationToken ct)
        => this.WithReceiptAsync("artifact.cancel", storyId, key, () => this.CancelArtifactCoreAsync(storyId, key, ct), ct);
    public Task<IResult> AcceptArtifactAsync(string storyId, string key, CancellationToken ct)
        => this.WithReceiptAsync("artifact.accept", storyId, key, () => this.AcceptArtifactCoreAsync(storyId, key, ct), ct);
    public Task<IResult> RestartArtifactAsync(string storyId, string key, CancellationToken ct)
        => this.WithReceiptAsync("artifact.restart", storyId, key, () => this.RestartArtifactCoreAsync(storyId, key, ct), ct);
}