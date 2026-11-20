// -----------------------------------------------------------------------
// <copyright file="ChatDeliveryService.SlackRoots.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Common.Chat;
using Microsoft.EntityFrameworkCore;

public sealed partial class ChatDeliveryService
{
    private async Task UpdateSlackRootsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var binding in await control.ChatConversationBindings.Where(b => b.RetiredAt == null && b.RootMessageId != null
            && b.RootDesiredState != b.RootRenderedState && b.RootNextAttemptAt <= now.ToUnixTimeMilliseconds()
            && control.ChatConnections.Any(c => c.Id == b.ConnectionId && c.ProviderId == "slack" && c.Enabled && c.DeletedAt == null))
            .OrderBy(b => b.RootNextAttemptAt).Take(20).ToListAsync(ct))
        {
            var connection = await control.ChatConnections.AsNoTracking().SingleAsync(c => c.Id == binding.ConnectionId, ct);
            var session = await supervisor.GetAsync(connection, ct); if (session is null) continue;
            var desired = binding.RootDesiredState;
            var message = new ChatSemanticNotification(binding.Id, "conversation-root", binding.Title ?? "Forge workstream",
                desired == "completed" ? "This workstream is complete. Open Forge for its results." : "Forge workstream updates and questions appear in this thread.", desired == "completed" ? 2 : 1, []);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            ChatProviderResult<bool> result;
            try { result = await session.Provider.EditAsync(new(new(connection.Id, binding.ExternalTenantId, binding.ChannelId, binding.ConversationId), binding.RootMessageId!), message, timeout.Token); }
            catch { result = ChatProviderResult<bool>.Fail(ChatFailureKind.Unavailable, "slack-api-unavailable"); }
            // Updating a known root is idempotent. A lost checkpoint retries only that edit, never thread creation.
            if (result.Succeeded) binding.RootRenderedState = desired;
            else runtime.RecordHealth(connection.Id, "degraded", ChatConnectionSupervisor.SafeProviderReason(result.Failure?.Code), null, null);
            binding.RootNextAttemptAt = now.AddSeconds(Math.Clamp(result.Failure?.RetryAfter?.TotalSeconds ?? 30, 1, 86400)).ToUnixTimeMilliseconds();
            await budget.SaveAsync(control, ct);
        }
    }
}