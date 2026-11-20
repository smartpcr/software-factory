// -----------------------------------------------------------------------
// <copyright file="StoryOperatorActionService.Chat.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using Forge.Api.Auth;
using Forge.Api.Endpoints;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Story actions that were HTTP-endpoint-only, made callable from chat
/// (docs/chat/chat-per-workstream-conversations.md): the HALTED banner's Retry and
/// pinning a plan-document decision. Same effect as the endpoints, with chat receipts.
/// </summary>
public sealed partial class StoryOperatorActionService
{
    public Task<IResult> RetrySpawnAsync(string storyId, CancellationToken ct)
        => this.WithReceiptAsync(ForgeCapabilities.SpawnRetry, storyId, null, () => this.RetrySpawnCoreAsync(storyId, ct), ct);

    public Task<IResult> AddDecisionPinAsync(string storyId, string key, string text, string workItemId, string actor, CancellationToken ct)
        => this.WithReceiptAsync(ForgeCapabilities.ArtifactPin, storyId, key, () => this.AddDecisionPinCoreAsync(storyId, text, workItemId, actor, ct), ct);

    /// <summary>Mirror of POST /api/stories/{storyId}/spawn/retry: clear the error and re-drive with forceResume.</summary>
    private async Task<IResult> RetrySpawnCoreAsync(string storyId, CancellationToken ct)
    {
        var control = services.GetRequiredService<ControlDbContext>();
        var db = services.GetRequiredService<ProductDbContext>();
        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId, ct);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        spawn.LastError = null;
        spawn.LastErrorAt = null;
        spawn.UpdatedAt = DateTimeOffset.UtcNow;
        await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
        var hub = services.GetService<IHubContext<ForgeHub>>();
        if (hub is not null) await hub.Clients.All.SendAsync("story-spawn.changed", new { kind = "spawn-retry", spawn = StoriesEndpoints.ToContract(spawn) }, ct);

        var config = services.GetService<IConfiguration>();
        var enabled = !bool.TryParse(config?["Forge:Stories:OrchestratorEnabled"], out var flag) || flag;
        if (enabled)
        {
            // Detached like the endpoint: the drive can run for minutes and must outlive this action.
            var scopes = services.GetRequiredService<IServiceScopeFactory>();
            var slug = db.ProductSlug;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var productScope = slug is null ? null : ProductDbContextFactory.PushAsyncLocalSlug(slug);
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>().DriveAsync(storyId, forceResume: true, CancellationToken.None);
                }
                catch
                {
                    // The orchestrator records its own failures as the spawn's LastError.
                }
            });
        }

        return Results.Ok(new { storyId, retried = true });
    }

    private async Task<IResult> AddDecisionPinCoreAsync(string storyId, string text, string workItemId, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return Results.Problem(title: "text is required", statusCode: 400);
        var control = services.GetRequiredService<ControlDbContext>();
        var db = services.GetRequiredService<ProductDbContext>();
        var spawn = await db.StorySpawns.FirstOrDefaultAsync(s => s.StoryId == storyId, ct);
        if (spawn is null) return Results.Problem(title: $"No spawn for story {storyId}", statusCode: 404);

        var pin = StoriesEndpoints.AppendDecisionPin(spawn, text, workItemId, actor);
        await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
        var hub = services.GetService<IHubContext<ForgeHub>>();
        if (hub is not null) await hub.Clients.All.SendAsync("story-spawn.changed", new { kind = "decision-pin-added", spawn = StoriesEndpoints.ToContract(spawn) }, ct);
        return Results.Ok(pin);
    }
}