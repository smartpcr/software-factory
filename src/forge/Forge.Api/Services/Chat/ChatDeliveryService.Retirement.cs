// -----------------------------------------------------------------------
// <copyright file="ChatDeliveryService.Retirement.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Common.Chat;
using Microsoft.EntityFrameworkCore;

public sealed partial class ChatDeliveryService
{
    private async Task RetireTopicsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var binding in await control.ChatConversationBindings.Where(b => b.RetiredAt != null
            && (b.RetirementState == "none" || b.RetirementState == "pending" || b.RetirementState == "closing")
            && b.RetirementNextAttemptAt <= now.ToUnixTimeMilliseconds()).Take(20).ToListAsync(ct))
        {
            if (binding.ConversationId is null) { binding.RetirementState = binding.ProvisioningState == "uncertain" ? "creation-uncertain" : "local"; await budget.SaveAsync(control, ct); continue; }
            if (await control.ChatConversationBindings.AnyAsync(b => b.Id != binding.Id && b.ConnectionId == binding.ConnectionId
                && b.ExternalTenantId == binding.ExternalTenantId && b.ChannelId == binding.ChannelId && b.ConversationId == binding.ConversationId && b.RetiredAt == null, ct))
            { binding.RetirementState = "shared-active"; await budget.SaveAsync(control, ct); continue; }
            // Remove stale controls before closing a forum topic. Unknown create/send records stay visible for operator repair.
            if (await control.ChatDeliveryMessages.AnyAsync(m => control.ChatDeliveries.Any(d => d.Id == m.DeliveryId && d.BindingId == binding.Id)
                && (m.State == "sending" || m.EditState == "pending" || m.EditState == "editing"), ct)) continue;
            var connection = await control.ChatConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == binding.ConnectionId && c.Enabled && c.DeletedAt == null, ct);
            if (connection is null) { binding.RetirementState = "unavailable"; await budget.SaveAsync(control, ct); continue; }
            if (providers.Resolve(connection.ProviderId)?.Descriptor.Capabilities.Retirement != ChatConversationRetirement.RemoteClose && connection.ProviderId != "slack")
            { binding.RetirementState = "local"; await budget.SaveAsync(control, ct); continue; }
            var session = await supervisor.GetAsync(connection, ct); if (session is null) continue;
            binding.RetirementState = "closing"; binding.OperationOwner = runtime.ProcessId; binding.RecoveryDeadline = now.AddSeconds(60);
            await budget.SaveAsync(control, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            ChatProviderResult<bool> result;
            try { result = await session.Provider.RetireConversationAsync(new(new(binding.ConnectionId, binding.ExternalTenantId, binding.ChannelId, binding.ConversationId), binding.RootMessageId), timeout.Token); }
            catch { result = ChatProviderResult<bool>.Fail(ChatFailureKind.Unavailable, "topic-close-unconfirmed"); }
            // closeForumTopic is idempotent; uncertain close can be retried, unlike createForumTopic.
            binding.RetirementState = result.Succeeded ? connection.ProviderId == "slack" ? "local" : "closed"
                : result.Failure?.Kind is ChatFailureKind.Unauthorized or ChatFailureKind.InvalidConfiguration ? "unavailable" : "pending";
            binding.RetirementNextAttemptAt = now.AddSeconds(Math.Clamp(result.Failure?.RetryAfter?.TotalSeconds ?? 30, 1, 86400)).ToUnixTimeMilliseconds();
            binding.OperationOwner = null; binding.RecoveryDeadline = null; await budget.SaveAsync(control, ct);
        }
    }
}