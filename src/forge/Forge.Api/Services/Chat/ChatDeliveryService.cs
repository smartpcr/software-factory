// -----------------------------------------------------------------------
// <copyright file="ChatDeliveryService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Security.Cryptography;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Per-copy scheduling with durable claims and memory-only successful-result checkpoints.</summary>
public sealed partial class ChatDeliveryService(ControlDbContext control, ChatRuntimeState runtime, ChatDbBudget budget,
    ChatScopeResolver resolver, ChatConnectionSupervisor supervisor, ChatSecretResolver secrets, ChatContentPolicy policy,
    IChatProviderRegistry providers, TimeProvider clock, ChatEngineCoordinator engine)
{
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        using var lease = await engine.EnterAsync("plugins", ct);
        if (lease is null) return;
        using var continuation = engine.ContinueLease();
        using var limits = ChatDbBudget.Limit(control);
        if (!runtime.EnsureHostOwnership()) return;
        await runtime.DeliveryGate.WaitAsync(ct);
        try
        {
            await this.FlushCheckpointsAsync(ct);
            await this.RecoverAsync(ct);
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            // The oldest ready item per connection/channel gets one slot before another item on that channel.
            var candidates = await control.ChatDeliveries.FromSqlInterpolated($"""
                SELECT * FROM chat_deliveries WHERE Id IN (SELECT Id FROM
                  (SELECT Id, ROW_NUMBER() OVER (PARTITION BY ConnectionId,ChannelId ORDER BY NextAttemptAt,CreatedAt,Id) AS position
                   FROM chat_deliveries WHERE State IN ('pending','retry') AND NextAttemptAt <= {now}) WHERE position=1)
                """).ToListAsync(ct);
            static string Channel(ChatDeliveryRow d) => d.ConnectionId + "\0" + d.ChannelId;
            var ordered = candidates.OrderBy(Channel, StringComparer.Ordinal).ToArray();
            var fair = ordered.Where(d => StringComparer.Ordinal.Compare(Channel(d), runtime.LastScheduledChannel) > 0)
                .Concat(ordered.Where(d => StringComparer.Ordinal.Compare(Channel(d), runtime.LastScheduledChannel) <= 0)).Take(20).ToArray();
            foreach (var delivery in fair)
            {
                runtime.LastScheduledChannel = Channel(delivery);
                try { await this.SendAsync(delivery, ct); }
                catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested)
                { runtime.RecordHealth(delivery.ConnectionId, "degraded", "delivery-storage-unavailable", null, null); control.ChangeTracker.Clear(); }
            }
            await this.EditOnceAsync(ct);
            await this.UpdateSlackRootsAsync(ct);
            await this.RetireTopicsAsync(ct);
        }
        finally { runtime.DeliveryGate.Release(); }
    }

    private async Task SendAsync(ChatDeliveryRow delivery, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var notification = await control.ChatNotifications.SingleAsync(n => n.Id == delivery.NotificationId, ct);
        var target = JsonSerializer.Deserialize<ChatDeliveryTarget>(delivery.TargetJson);
        if (target is null) { await this.CancelAsync(delivery, "invalid-target", ct); return; }
        var reason = await resolver.ValidateAsync(target, notification, ct);
        if (reason is not null)
        {
            if (reason == "ownership-unverified") { delivery.LastError = reason; delivery.NextAttemptAt = now.AddMinutes(5).ToUnixTimeMilliseconds(); await budget.SaveAsync(control, ct); }
            else await this.CancelAsync(delivery, reason, ct);
            return;
        }
        if (notification.Status is "superseded" or "expired" or "resolved" || notification.ExpiresAt <= now)
        { await this.CancelAsync(delivery, notification.Status == "superseded" ? "superseded" : "expired", ct); return; }
        if (ChatPayloadConverter.Read(notification) is null)
        { delivery.State = "failed"; delivery.LastError = "unsupported-payload"; await budget.SaveAsync(control, ct); return; }
        // Actionable messages are ordered per conversation: a stuck alert in one workstream's
        // topic must not hold back another workstream's (same subscription, different ChildKey).
        var deliveryChild = delivery.Actionable
            ? await control.ChatConversationBindings.AsNoTracking().Where(b => b.Id == delivery.BindingId).Select(b => b.ChildKey).SingleOrDefaultAsync(ct) ?? ""
            : "";
        if (delivery.Actionable && await control.ChatDeliveries.AnyAsync(d => d.Id != delivery.Id && d.SubscriptionId == delivery.SubscriptionId
            && control.ChatConversationBindings.Any(b => b.Id == d.BindingId && b.ChildKey == deliveryChild)
            && d.Actionable && (d.DomainRevision < delivery.DomainRevision || d.DomainRevision == delivery.DomainRevision && d.CreatedAt < delivery.CreatedAt)
            && (d.State == "pending" || d.State == "retry" || d.State == "sending" || d.State == "uncertain" || d.State == "failed"), ct))
        { delivery.LastError = "blocked-by-earlier-action"; delivery.NextAttemptAt = now.AddSeconds(5).ToUnixTimeMilliseconds(); await budget.SaveAsync(control, ct); return; }
        var connection = await control.ChatConnections.AsNoTracking().SingleAsync(c => c.Id == delivery.ConnectionId, ct);
        var session = await supervisor.GetAsync(connection, ct);
        if (session is null) { this.Failure(delivery, new(ChatFailureKind.InvalidConfiguration, "session-unavailable")); await budget.SaveAsync(control, ct); return; }
        var binding = await control.ChatConversationBindings.SingleAsync(b => b.Id == delivery.BindingId, ct);
        if (binding.RetiredAt is not null) { await this.CancelAsync(delivery, "binding-retired", ct); return; }
        if (binding.ProvisioningState == "uncertain") { delivery.State = "uncertain"; delivery.LastError = "conversation-outcome-unknown"; await budget.SaveAsync(control, ct); return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var address = new ChatAddress(connection.Id, target.ExternalTenantId, target.ChannelId);
        var validated = await session.Provider.ValidateDestinationAsync(address, timeout.Token);
        if (!validated.Succeeded || validated.Value!.Address != address)
        { this.Failure(delivery, validated.Failure ?? new(ChatFailureKind.InvalidConfiguration, "destination-mismatch")); await budget.SaveAsync(control, ct); return; }
        if (binding.ProvisioningState != "ready")
        {
            if (target.ConversationPolicy == "flat") { binding.ProvisioningState = "ready"; await budget.SaveAsync(control, ct); }
            else
            {
                if (binding.ProvisioningState == "provisioning") return;
                binding.ProvisioningState = "provisioning"; binding.OperationOwner = runtime.ProcessId; binding.RecoveryDeadline = now.AddSeconds(60);
                await budget.SaveAsync(control, ct);
                ChatProviderResult<ChatConversation> result;
                try { result = await session.Provider.EnsureConversationAsync(validated.Value!, Enum.Parse<ChatConversationPolicy>(target.ConversationPolicy, true), binding.Id, binding.Title ?? "Forge workstream", timeout.Token); }
                catch { result = ChatProviderResult<ChatConversation>.Fail(ChatFailureKind.UnknownOutcome, "conversation-outcome-unknown"); }
                if (!result.Succeeded || result.Value!.Address.ConnectionId != address.ConnectionId
                    || result.Value.Address.ExternalTenantId != address.ExternalTenantId || result.Value.Address.ChannelId != address.ChannelId)
                {
                    var failure = result.Failure ?? new(ChatFailureKind.UnknownOutcome, "conversation-outcome-unknown");
                    binding.ProvisioningState = failure.Kind == ChatFailureKind.UnknownOutcome ? "uncertain" : "pending";
                    this.Failure(delivery, failure); await budget.SaveAsync(control, ct); return;
                }
                runtime.Checkpoints[binding.Id] = new("provision", binding.Id, Conversation: result.Value);
                await this.FlushCheckpointsAsync(ct);
            }
        }
        address = address with { ConversationId = binding.ConversationId };
        var part = await control.ChatDeliveryMessages.Where(m => m.DeliveryId == delivery.Id && m.State == "pending").OrderBy(m => m.PartKey).FirstOrDefaultAsync(ct);
        if (part is null) { delivery.State = "sent"; await budget.SaveAsync(control, ct); return; }
        if (part.ControlsRetired || part.ExpiresAt <= now.ToUnixTimeMilliseconds()) { part.State = "cancelled"; await budget.SaveAsync(control, ct); return; }
        var subscription = await control.ChatSubscriptions.AsNoTracking().SingleAsync(s => s.Id == target.SubscriptionId, ct);
        var actions = new List<ChatSemanticAction>();
        var capabilities = providers.Resolve(target.ProviderId)!.Descriptor.Capabilities;
        foreach (var previousToken in await control.ChatActionTokens.Where(t => t.DeliveryMessageId == part.Id && t.State == "active").ToListAsync(ct)) previousToken.State = "retired";
        if (target.Interactive && capabilities.Buttons && part.QuestionId is not null && part.ActionKind != "question.prompt" && policy.AllowsQuestions(target.ProductId, subscription.ContentMode))
        {
            var question = ChatPayloadConverter.Read(notification)?.Questions.SingleOrDefault(q => q.Id == part.QuestionId && q.Revision == part.ExpectedRevision);
            var choices = question?.Choices ?? [];
            // Operator rule: answers are picked from the options, never typed, so there is
            // no "Custom answer" control.
            for (var index = 0; index < choices.Count; index++)
            {
                var token = NewActionToken();
                control.ChatActionTokens.Add(new() { Hash = ChatEventCatalog.Hash(token), DeliveryMessageId = part.Id,
                    Action = "question.answer", QuestionId = part.QuestionId, ExpectedRevision = part.ExpectedRevision, ChoiceIndex = index,
                    ExpiresAt = Math.Min(now.AddDays(1).ToUnixTimeMilliseconds(), notification.ExpiresAt?.ToUnixTimeMilliseconds() ?? long.MaxValue) });
                // Star the agent's recommended option, matching the Questions tab. The label is display
                // only: the answer is resolved from ChoiceIndex, never from the button text.
                actions.Add(new((question?.Recommended == index ? "★ " : "") + choices[index], token, "question.answer"));
            }
            // Dismiss without answering, and (plan documents) pin a binding decision.
            foreach (var extra in notification.ArtifactKey is null ? new[] { "question.dismiss" } : new[] { "question.dismiss", ForgeCapabilities.ArtifactPin })
            {
                var token = NewActionToken();
                control.ChatActionTokens.Add(new() { Hash = ChatEventCatalog.Hash(token), DeliveryMessageId = part.Id, Action = extra, QuestionId = part.QuestionId,
                    ExpectedRevision = part.ExpectedRevision, ExpiresAt = Math.Min(now.AddDays(1).ToUnixTimeMilliseconds(), notification.ExpiresAt?.ToUnixTimeMilliseconds() ?? long.MaxValue) });
                actions.Add(new(ActionLabel(extra), token, extra));
            }
        }
        else if (target.Interactive && capabilities.Buttons && part.ActionKind is not ("question.prompt" or ChatInboxService.PinPrompt) && policy.AllowsQuestions(target.ProductId, subscription.ContentMode))
        {
            var kinds = part.ActionKind is not null ? new[] { part.ActionKind }
                : notification.Kind == "workstream-stalled" && notification.WorkstreamId is not null ? new[] { "workstream.retry", "workstream.accept", "workstream.cancel", "workstream.restart" }
                : notification.Kind == ChatEventCatalog.SpawnHalted
                    ? notification.ArtifactKey is not null ? new[] { ForgeCapabilities.SpawnRetry, "artifact.restart", ForgeCapabilities.ArtifactPin } : new[] { ForgeCapabilities.SpawnRetry }
                : Array.Empty<string>();
            foreach (var kind in kinds)
            {
                var token = NewActionToken();
                control.ChatActionTokens.Add(new() { Hash = ChatEventCatalog.Hash(token), DeliveryMessageId = part.Id, Action = kind,
                    ActorId = part.ConfirmationActorId, ExpectedRevision = part.ExpectedRevision,
                    ExpiresAt = part.ExpiresAt ?? Math.Min(now.AddDays(1).ToUnixTimeMilliseconds(), notification.ExpiresAt?.ToUnixTimeMilliseconds() ?? long.MaxValue) });
                actions.Add(new(part.ConfirmationActorId is null ? ActionLabel(kind) : "Confirm restart", token, kind));
            }
        }
        var rendered = policy.Render(notification, part, subscription.ContentMode, await secrets.ValuesAsync(connection, ct), actions);
        if (connection.ProviderId is not ("telegram" or "slack")) rendered = Bound(rendered, capabilities);
        // Revalidate after session opening/provisioning, immediately before a durable send claim.
        if (await resolver.ValidateAsync(target, notification, ct) is { } changed) { await this.CancelAsync(delivery, changed, ct); return; }
        part.RenderedJson = JsonSerializer.Serialize(rendered with { Actions = [] });
        part.AddressJson = JsonSerializer.Serialize(address); part.State = "sending"; part.OperationId = Guid.NewGuid().ToString("N");
        part.OwnerProcessInstanceId = runtime.ProcessId; part.RecoveryDeadline = now.AddSeconds(60).ToUnixTimeMilliseconds(); part.Attempts++;
        delivery.State = "sending"; delivery.OperationId = part.OperationId; delivery.OwnerProcessInstanceId = runtime.ProcessId;
        delivery.RecoveryDeadline = part.RecoveryDeadline; delivery.Attempts++; delivery.LastError = null;
        await budget.SaveAsync(control, ct);
        ChatProviderResult<ChatSendReceipt> sent;
        try { sent = await session.Provider.SendAsync(address, rendered, timeout.Token); }
        catch { sent = ChatProviderResult<ChatSendReceipt>.Fail(ChatFailureKind.UnknownOutcome, "send-outcome-unknown"); }
        if (sent.Value is { Messages.Count: > 0 } && sent.Value.Messages.All(m => m.Address == address && !string.IsNullOrEmpty(m.MessageId)))
        {
            runtime.Checkpoints[part.Id] = new("send", part.Id, sent.Value, Revision: part.DesiredRevision, Uncertain: !sent.Succeeded);
            // A storage exception leaves the successful receipt in memory. Next tick retries only this checkpoint.
            await this.FlushCheckpointsAsync(ct);
        }
        else
        {
            this.Failure(delivery, sent.Failure ?? new(ChatFailureKind.UnknownOutcome, "send-outcome-unknown"));
            part.State = delivery.State == "uncertain" ? "uncertain" : "pending"; part.LastError = delivery.LastError;
            await budget.SaveAsync(control, ct);
        }
    }

    private static string NewActionToken() => "a1_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static ChatSemanticNotification Bound(ChatSemanticNotification value, ChatProviderCapabilities caps)
    {
        var limit = Math.Max(1, Math.Min(caps.MaxTextCharacters, caps.MaxPayloadBytes / 4));
        var length = Math.Min(value.Body.Length, limit);
        if (length < value.Body.Length && length > 0 && char.IsHighSurrogate(value.Body[length - 1])) length--;
        return value with { Body = value.Body[..length], Actions = value.Actions.Where(a => a.Token.Length <= caps.MaxActionBytes).ToArray() };
    }

    private void Failure(ChatDeliveryRow delivery, ChatProviderFailure failure)
    {
        // Codes from providers are not trusted log text. Persist only the host's fixed classification.
        delivery.State = failure.Kind switch { ChatFailureKind.UnknownOutcome => "uncertain", ChatFailureKind.RateLimited or ChatFailureKind.Unavailable => "retry", _ => "failed" };
        delivery.LastError = failure.Kind switch { ChatFailureKind.UnknownOutcome => "send-outcome-unknown", ChatFailureKind.RateLimited => "rate-limited",
            ChatFailureKind.Unavailable => "provider-unavailable", ChatFailureKind.Unauthorized => "provider-permission-denied", _ => "provider-configuration-error" };
        if (failure.Kind == ChatFailureKind.InvalidPayload) delivery.LastError = failure.Code switch
        {
            "render-payload-limit:callback_data:64" or "render-payload-limit:action-key:25" or "render-payload-limit:button-label:256"
                or "render-payload-limit:buttons:20" or "render-payload-limit:text:65536" or "render-payload-limit:payload-bytes:32768"
                or "render-payload-limit:edit-text:4096" => failure.Code,
            "render-payload-limit:button-value:2000" or "render-payload-limit:button-label:75" or "render-payload-limit:edit-text:3000" => failure.Code,
            _ => "render-payload-limit",
        };
        if (failure.Code.StartsWith("slack-", StringComparison.Ordinal) && ChatConnectionSupervisor.SafeProviderReason(failure.Code) is var slackReason && slackReason != "session-unavailable")
        {
            delivery.LastError = slackReason;
            runtime.RecordHealth(delivery.ConnectionId, "degraded", slackReason, null, null);
        }
        var delay = failure.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(delivery.Attempts, 8))) + Random.Shared.NextDouble());
        delay = TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 86400));
        delivery.NextAttemptAt = clock.GetUtcNow().Add(delay).ToUnixTimeMilliseconds();
    }

    internal async Task CancelAsync(ChatDeliveryRow delivery, string reason, CancellationToken ct)
    {
        delivery.State = reason == "superseded" ? "superseded" : "cancelled"; delivery.LastError = reason;
        delivery.TerminalAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var message in await control.ChatDeliveryMessages.Where(m => m.DeliveryId == delivery.Id).ToListAsync(ct))
        {
            message.ControlsRetired = true; message.DesiredRevision++; message.TerminalAt = delivery.TerminalAt;
            if (message.State == "sent") message.EditState = "pending";
            else if (message.State == "pending") message.State = "cancelled";
            foreach (var token in await control.ChatActionTokens.Where(t => t.DeliveryMessageId == message.Id && t.State == "active").ToListAsync(ct)) token.State = "retired";
        }
        await budget.SaveAsync(control, ct);
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        foreach (var part in await control.ChatDeliveryMessages.Where(m => (m.State == "sending" || m.EditState == "editing") && m.OwnerProcessInstanceId != runtime.ProcessId).Take(100).ToListAsync(ct))
        {
            if (part.State == "sending")
            {
                part.State = "uncertain"; part.LastError = "send-outcome-unknown";
                var delivery = await control.ChatDeliveries.SingleAsync(d => d.Id == part.DeliveryId, ct);
                delivery.State = "uncertain"; delivery.LastError = part.LastError;
            }
            // Edits address an existing message and are safe to reconcile to the latest desired revision.
            if (part.EditState == "editing") part.EditState = "pending";
        }
        foreach (var binding in await control.ChatConversationBindings.Where(b => b.ProvisioningState == "provisioning" && b.OperationOwner != runtime.ProcessId).Take(100).ToListAsync(ct))
            binding.ProvisioningState = "uncertain";
        if (control.ChangeTracker.HasChanges()) await budget.SaveAsync(control, ct);
    }

    private async Task FlushCheckpointsAsync(CancellationToken ct)
    {
        foreach (var checkpoint in runtime.Checkpoints.Values.Take(20))
        {
            if (checkpoint.Kind == "provision")
            {
                var binding = await control.ChatConversationBindings.SingleAsync(b => b.Id == checkpoint.Id, ct);
                binding.ConversationId = checkpoint.Conversation!.Address.ConversationId; binding.RootMessageId = checkpoint.Conversation.RootMessageId;
                binding.ProvisioningState = "ready"; binding.OperationOwner = null; binding.RecoveryDeadline = null;
            }
            else
            {
                var part = await control.ChatDeliveryMessages.SingleAsync(m => m.Id == checkpoint.Id, ct);
                var delivery = await control.ChatDeliveries.SingleAsync(d => d.Id == part.DeliveryId, ct);
                if (checkpoint.Kind == "send")
                {
                    // Providers place controls on the last split message; this remains the token's canonical reference.
                    part.MessageId = checkpoint.Send!.Messages[^1].MessageId; part.State = "sent";
                    if (checkpoint.Uncertain) { part.ControlsRetired = true; delivery.State = "uncertain"; delivery.LastError = "partial-send-outcome-unknown"; }
                    for (var index = 0; index < checkpoint.Send.Messages.Count - 1; index++)
                    {
                        var partKey = part.PartKey + ":" + (index + 1);
                        if (!await control.ChatDeliveryMessages.AnyAsync(m => m.DeliveryId == delivery.Id && m.PartKey == partKey, ct))
                            control.ChatDeliveryMessages.Add(new() { DeliveryId = delivery.Id, PartKey = partKey, QuestionId = part.QuestionId,
                                ExpectedRevision = part.ExpectedRevision, MessageId = checkpoint.Send.Messages[index].MessageId,
                                AddressJson = JsonSerializer.Serialize(checkpoint.Send.Messages[index].Address), State = "sent", RenderedRevision = checkpoint.Revision,
                                ControlsRetired = part.ControlsRetired, EditState = part.ControlsRetired ? "pending" : "none",
                                ActionKind = part.ActionKind, ConfirmationActorId = part.ConfirmationActorId, ExpiresAt = part.ExpiresAt });
                    }
                    if (delivery.State == "sending") delivery.State = await control.ChatDeliveryMessages.AnyAsync(m => m.DeliveryId == delivery.Id && m.Id != part.Id && m.State == "pending", ct) ? "pending" : "sent";
                    delivery.OwnerProcessInstanceId = null; delivery.RecoveryDeadline = null;
                    runtime.RecordHealth(delivery.ConnectionId, checkpoint.Uncertain ? "degraded" : "ready", checkpoint.Uncertain ? "partial-send-outcome-unknown" : null, clock.GetUtcNow().ToUnixTimeMilliseconds(), null);
                }
                part.RenderedRevision = Math.Max(part.RenderedRevision, checkpoint.Revision);
                part.EditState = part.DesiredRevision > part.RenderedRevision || part.ControlsRetired && checkpoint.Kind == "send" ? "pending" : "none";
                part.OwnerProcessInstanceId = null; part.RecoveryDeadline = null;
            }
            await budget.SaveAsync(control, ct);
            runtime.Checkpoints.TryRemove(checkpoint.Id, out _);
        }
    }

    private async Task EditOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var part in await control.ChatDeliveryMessages.Where(m => m.EditState == "pending" && m.MessageId != null && m.NextAttemptAt <= now).Take(20).ToListAsync(ct))
        {
            var delivery = await control.ChatDeliveries.SingleAsync(d => d.Id == part.DeliveryId, ct);
            var notification = await control.ChatNotifications.SingleAsync(n => n.Id == delivery.NotificationId, ct);
            var connection = await control.ChatConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == delivery.ConnectionId && c.DeletedAt == null && c.Enabled, ct);
            if (connection is null) { part.EditState = "unavailable"; await budget.SaveAsync(control, ct); continue; }
            if (providers.Resolve(connection.ProviderId)?.Descriptor.Capabilities.Edits != true) { part.EditState = "not-supported"; await budget.SaveAsync(control, ct); continue; }
            var session = await supervisor.GetAsync(connection, ct);
            if (session is null) continue;
            // Reconciliation edits can bypass disabled subscriptions but only remove controls, never add authority.
            var rendered = Bound(policy.Render(notification, part, "metadata", await secrets.ValuesAsync(connection, ct), []), providers.Resolve(connection.ProviderId)!.Descriptor.Capabilities);
            part.EditState = "editing"; part.OwnerProcessInstanceId = runtime.ProcessId; part.RecoveryDeadline = now + 60000;
            part.RenderedJson = JsonSerializer.Serialize(rendered); await budget.SaveAsync(control, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            ChatProviderResult<bool> result;
            try { result = await session.Provider.EditAsync(new(JsonSerializer.Deserialize<ChatAddress>(part.AddressJson)!, part.MessageId!), rendered, timeout.Token); }
            catch { result = ChatProviderResult<bool>.Fail(ChatFailureKind.Unavailable, "edit-unconfirmed"); }
            if (result.Succeeded)
            { runtime.Checkpoints[part.Id] = new("edit", part.Id, Revision: part.DesiredRevision); await this.FlushCheckpointsAsync(ct); }
            else
            {
                part.EditState = result.Failure?.Kind is ChatFailureKind.Unavailable or ChatFailureKind.RateLimited or ChatFailureKind.UnknownOutcome ? "pending" : "failed";
                part.NextAttemptAt = now + 30000; part.LastError = "control-retirement-unconfirmed"; await budget.SaveAsync(control, ct);
            }
        }
    }

    /// <summary>Button text for an action kind.</summary>
    internal static string ActionLabel(string kind) => kind switch
    {
        "workstream.retry" or "spawn.retry" or "artifact.retry" => "🔁 Retry",
        "workstream.accept" or "artifact.accept" => "✅ Accept",
        "workstream.cancel" or "artifact.cancel" => "🚫 Cancel",
        "workstream.restart" => "⚠ Restart",
        "artifact.restart" => "⚠ Restart doc",
        "artifact.pin" => "📌 Pin decision",
        "question.dismiss" => "Dismiss",
        _ => kind.Split('.').Last(),
    };
}