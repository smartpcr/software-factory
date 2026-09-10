// -----------------------------------------------------------------------
// <copyright file="SlackSocketReceiver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat.Slack;

using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>One app-owned socket, independent of the member that started it. Only durable receipts are acknowledged.</summary>
internal sealed class SlackSocketReceiver(string appId, string fingerprint, SlackApiClient api, ISlackWebSocketFactory sockets, TimeProvider clock) : IAsyncDisposable
{
    private readonly SemaphoreSlim reception = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private ISlackWebSocket? socket;
    private string? failureCode;
    public string AppId => appId;
    public string TokenFingerprint => fingerprint;
    public string? FailureCode => Volatile.Read(ref this.failureCode);
    public ConcurrentDictionary<string, SlackConnectionSession> Members { get; } = new(StringComparer.Ordinal);

    public async Task<ChatProviderFailure?> OpenAsync(CancellationToken ct)
    {
        if (this.socket is not null) { await this.socket.DisposeAsync(); this.socket = null; }
        var result = await api.CallAsync("apps.connections.open", new { }, ct);
        if (!result.Succeeded) return this.Failed(result.Failure!);
        var url = SlackApiClient.String(result.Value, "url");
        // The short-lived ticket never enters configuration, logs, errors, or message storage.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "wss" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || !(uri.Host == "wss.slack.com" || uri.Host.EndsWith(".slack.com", StringComparison.Ordinal)))
            return this.Failed(new(ChatFailureKind.InvalidConfiguration, "slack-socket-url-invalid"));
        var candidate = sockets.Create();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, this.lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await candidate.ConnectAsync(uri, timeout.Token);
            var hello = await candidate.ReadAsync(timeout.Token);
            if (hello is null || SlackApiClient.String(hello.Value, "type") != "hello"
                || SlackApiClient.String(SlackApiClient.Object(hello.Value, "connection_info"), "app_id") != appId)
            { await candidate.DisposeAsync(); return this.Failed(new(ChatFailureKind.InvalidConfiguration, "slack-app-handshake-mismatch")); }
            this.socket = candidate; Volatile.Write(ref this.failureCode, null); return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { await candidate.DisposeAsync(); return this.Failed(new(ChatFailureKind.Unavailable, "slack-socket-unavailable")); }
    }
    private ChatProviderFailure Failed(ChatProviderFailure failure) { Volatile.Write(ref this.failureCode, failure.Code); return failure; }

    public async Task ReceiveAsync(IChatInboundSink sink, CancellationToken ct)
    {
        if (!await this.reception.WaitAsync(0, ct)) throw new InvalidOperationException("slack-receiver-conflict");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, this.lifetime.Token);
        var token = linked.Token; var attempts = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                ChatProviderFailure? failure = null;
                try
                {
                    if (this.socket is null) failure = await this.OpenAsync(token);
                    if (failure is null)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromMinutes(5));
                        var payload = await this.socket!.ReadAsync(idle.Token);
                        if (payload is not null && SlackApiClient.String(payload.Value, "type") != "disconnect")
                        {
                            await this.AcceptAsync(payload.Value, sink, token); attempts = 0; continue;
                        }
                        var reason = payload is null ? null : SlackApiClient.String(payload.Value, "reason");
                        failure = new(ChatFailureKind.Unavailable, reason == "link_disabled" ? "slack-socket-disabled" : "slack-socket-reconnecting");
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { failure = new(ChatFailureKind.Unavailable, "slack-socket-unavailable"); }
                if (this.socket is not null) { await this.socket.DisposeAsync(); this.socket = null; }
                this.Failed(failure!);
                var delay = failure!.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempts++, 5))) + Random.Shared.NextDouble() * .25);
                await Task.Delay(delay, clock, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (this.socket is not null) { await this.socket.DisposeAsync(); this.socket = null; }
            this.reception.Release();
        }
    }

    private async Task AcceptAsync(JsonElement frame, IChatInboundSink sink, CancellationToken ct)
    {
        var envelopeId = SlackApiClient.String(frame, "envelope_id");
        if (envelopeId is not { Length: > 0 and <= 512 }) throw new InvalidOperationException("slack-socket-payload-invalid");
        // Reserve acknowledgement time within Slack's three-second window. Domain dispatch runs separately.
        using var acceptTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); acceptTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        ChatInboundAcceptance accepted;
        try
        {
            var normalized = this.Normalize(frame, envelopeId);
            var payload = SlackApiClient.Object(frame, "payload");
            var tenant = SlackApiClient.String(payload, "team_id") ?? SlackApiClient.String(SlackApiClient.Object(payload, "team"), "id");
            accepted = normalized.Envelope is { } envelope ? await sink.AcceptAsync(envelope, acceptTimeout.Token)
                : sink is IChatReceptionDiagnosticSink diagnostics ? await diagnostics.AcceptDiagnosticAsync(normalized.Diagnostic!, SlackApiClient.Id(tenant) ? tenant : null, acceptTimeout.Token)
                : ChatInboundAcceptance.Retry;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return; }
        if (accepted == ChatInboundAcceptance.Retry) return;
        using var ackTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); ackTimeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        await this.socket!.AcknowledgeAsync(envelopeId, ackTimeout.Token);
    }

    internal (ChatInboundEnvelope? Envelope, string? Diagnostic) Normalize(JsonElement frame, string envelopeId)
    {
        var type = SlackApiClient.String(frame, "type"); var payload = SlackApiClient.Object(frame, "payload");
        if (SlackApiClient.String(payload, "api_app_id") != appId) return (null, "slack-event-app-mismatch");
        var workspace = type == "events_api" ? SlackApiClient.String(payload, "team_id") : SlackApiClient.String(SlackApiClient.Object(payload, "team"), "id");
        if (workspace is null || !this.Members.TryGetValue(workspace, out var member)) return (null, "slack-unknown-workspace");
        if (SlackApiClient.True(payload, "is_enterprise_install") || SlackApiClient.True(payload, "is_ext_shared_channel")) return (null, "slack-cross-workspace-unsupported");
        if (type == "events_api")
        {
            var authorizations = SlackApiClient.Object(payload, "authorizations");
            if (authorizations.ValueKind != JsonValueKind.Array || !authorizations.EnumerateArray().Any(a => SlackApiClient.True(a, "is_bot")
                && !SlackApiClient.True(a, "is_enterprise_install") && SlackApiClient.String(a, "team_id") == workspace && SlackApiClient.String(a, "user_id") == member.BotUserId))
                return (null, "slack-app-installation-mismatch");
            var eventId = SlackApiClient.String(payload, "event_id");
            if (eventId is not { Length: > 0 and <= 480 }) return (null, "slack-event-invalid");
            var value = SlackApiClient.Object(payload, "event"); var channel = SlackApiClient.String(value, "channel");
            var sender = SlackApiClient.String(value, "user"); var ts = SlackApiClient.String(value, "ts");
            var thread = SlackApiClient.String(value, "thread_ts");
            if (!SlackApiClient.Id(channel) || channel![0] is not ('C' or 'G')) return (null, "slack-event-unsupported");
            if (SlackApiClient.String(value, "user_team") is { } userTeam && userTeam != workspace
                || SlackApiClient.String(value, "team") is { } eventTeam && eventTeam != workspace) return (null, "slack-cross-workspace-unsupported");
            var channelType = SlackApiClient.String(value, "channel_type");
            var ignored = SlackApiClient.String(value, "type") != "message" || SlackApiClient.String(value, "subtype") is not null
                || SlackApiClient.String(value, "bot_id") is not null || sender == member.BotUserId || !SlackApiClient.Id(sender)
                || channelType is not ("channel" or "group") || channelType == "group" && !member.PrivateChannels;
            if (!ignored && (!SlackApiClient.Timestamp(ts) || thread is not null && !SlackApiClient.Timestamp(thread))) return (null, "slack-event-invalid");
            var text = ignored ? null : SlackApiClient.String(value, "text");
            var command = ParseCommand(text);
            return (new("slack", member.ConnectionId, "event:" + eventId, new(member.ConnectionId, workspace, channel, thread), sender ?? "",
                ignored ? "ignored" : text is null ? "unsupported" : "text", ts, null, command.Text, null, clock.GetUtcNow(),
                Command: command.Command, CommandArgument: command.Argument), null);
        }
        if (type == "interactive" && SlackApiClient.String(payload, "type") == "block_actions")
        {
            var user = SlackApiClient.Object(payload, "user"); var sender = SlackApiClient.String(user, "id");
            if (SlackApiClient.String(user, "team_id") is { } userTeam && userTeam != workspace) return (null, "slack-cross-workspace-unsupported");
            var container = SlackApiClient.Object(payload, "container"); var message = SlackApiClient.Object(payload, "message");
            var channel = SlackApiClient.String(container, "channel_id"); var ts = SlackApiClient.String(container, "message_ts");
            var thread = SlackApiClient.String(message, "thread_ts");
            var actions = SlackApiClient.Object(payload, "actions");
            if (!SlackApiClient.Id(sender) || sender == member.BotUserId || !SlackApiClient.Id(channel) || channel![0] is not ('C' or 'G')
                || !SlackApiClient.Timestamp(ts) || SlackApiClient.String(message, "ts") != ts || thread is not null && !SlackApiClient.Timestamp(thread)
                || SlackApiClient.String(SlackApiClient.Object(payload, "channel"), "id") != channel
                || SlackApiClient.String(message, "user") != member.BotUserId || SlackApiClient.String(message, "bot_id") is { } bot && bot != member.BotId
                || actions.ValueKind != JsonValueKind.Array || actions.GetArrayLength() != 1) return (null, "slack-interaction-invalid");
            var action = actions[0]; var key = SlackApiClient.String(action, "value");
            if (SlackApiClient.String(action, "type") != "button" || !SlackMessageRenderer.ActionKey(key)) return (null, "slack-interaction-invalid");
            return (new("slack", member.ConnectionId, "envelope:" + envelopeId, new(member.ConnectionId, workspace, channel, thread), sender!, "action", ts, ts,
                null, key, clock.GetUtcNow()), null);
        }
        return (null, "slack-event-unsupported");
    }

    private static (string? Text, string? Command, string? Argument) ParseCommand(string? input)
    {
        var text = input?.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);
        if (text is null || !text.StartsWith("!forge ", StringComparison.Ordinal)) return (text, null, null);
        var words = text[7..].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return (null, "unknown", null);
        var command = words[0].ToLowerInvariant();
        return (command == "answer" && words.Length == 3 ? words[2] : null, command, words.Length >= 2 ? words[1] : null);
    }

    public async ValueTask DisposeAsync()
    {
        this.lifetime.Cancel(); await this.reception.WaitAsync();
        try { if (this.socket is not null) { await this.socket.DisposeAsync(); this.socket = null; } }
        finally { this.reception.Release(); this.lifetime.Dispose(); }
    }
}