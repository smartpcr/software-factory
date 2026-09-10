// -----------------------------------------------------------------------
// <copyright file="ChatSlackProtocolTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Common.Chat;
using Common.Chat.Slack;
using Xunit;

public sealed class ChatSlackProtocolTests
{
    [Theory]
    [InlineData("bot-token", "slack-bot-token-required")]
    [InlineData("app-token", "slack-app-token-required")]
    [InlineData("workspace", "slack-workspace-mismatch")]
    [InlineData("bot", "slack-bot-installation-required")]
    [InlineData("enterprise", "slack-enterprise-installation-unsupported")]
    [InlineData("app", "slack-app-handshake-mismatch")]
    [InlineData("app-scope", "slack-app-scope-required")]
    [InlineData("bot-revoked", "slack-bot-token-revoked")]
    [InlineData("app-revoked", "slack-app-token-revoked")]
    [InlineData("url", "slack-socket-url-invalid")]
    public async Task Setup_separates_credentials_installation_workspace_and_app_failures(string scenario, string code)
    {
        using var f = new SlackProviderFixture();
        if (scenario is "bot-token" or "app-token") f.Secrets[scenario == "bot-token" ? "bot-T1" : "app-A1"] = "wrong-token";
        if (scenario == "app") f.CreateSocket = _ => new("AOTHER");
        f.Override = (_, method, _, _) => Task.FromResult<HttpResponseMessage?>(
            method == "auth.test" ? scenario switch
            {
                "workspace" => SlackProviderFixture.Json(new { ok = true, team_id = "TOTHER" }),
                "bot" => SlackProviderFixture.Json(new { ok = true, team_id = "T1", user_id = "UUSER" }),
                "enterprise" => SlackProviderFixture.Json(new { ok = true, team_id = "T1", is_enterprise_install = true }),
                "bot-revoked" => SlackProviderFixture.Error("token_revoked"), _ => null,
            } : method == "apps.connections.open" ? scenario switch
            {
                "app-scope" => SlackProviderFixture.Error("missing_scope"), "app-revoked" => SlackProviderFixture.Error("invalid_auth"),
                "url" => SlackProviderFixture.Json(new { ok = true, url = "wss://attacker.test/?ticket=secret" }), _ => null,
            } : null);
        Assert.Equal(code, (await f.Provider.OpenSessionAsync(f.Configuration(), f)).Failure!.Code);
        Assert.All(f.Sockets, socket => Assert.True(socket.Disposed));
        Assert.DoesNotContain(f.Calls, c => c.Method is "chat.postMessage" or "conversations.replies" or "bots.info");
    }

    [Fact]
    public async Task Shared_app_validates_once_rejects_duplicate_installations_and_conflicting_app_credentials()
    {
        using var f = new SlackProviderFixture(); await using var first = await f.OpenAsync(); await using var second = await f.OpenAsync("slack-two", "T2");
        Assert.Equal(first.ReceptionKey, second.ReceptionKey); Assert.Single(f.Sockets);
        Assert.Equal("duplicate-slack-workspace", (await f.Provider.OpenSessionAsync(f.Configuration("duplicate"), f)).Failure!.Code);
        f.Secrets["app-A1"] = "xapp-A1-differentcredential";
        Assert.Equal("slack-app-token-conflict", (await f.Provider.OpenSessionAsync(f.Configuration("third", "T3"), f)).Failure!.Code);
        Assert.Equal(1, f.Calls.Count(c => c.Method == "apps.connections.open"));
    }

    [Fact]
    public async Task Discovery_preserves_cursors_and_filters_membership_archived_private_and_connect_channels()
    {
        using var f = new SlackProviderFixture(); await using var session = await f.OpenAsync(privateChannels: false);
        f.Override = (_, method, body, _) => Task.FromResult<HttpResponseMessage?>(method != "conversations.list" ? null
            : SlackProviderFixture.Json(new { ok = true, channels = new[] { SlackProviderFixture.Channel("C1"), SlackProviderFixture.Channel("G1", true),
                SlackProviderFixture.Channel("C2", member: false), SlackProviderFixture.Channel("C3", archived: true), SlackProviderFixture.Channel("C4", shared: true) },
                response_metadata = new { next_cursor = body.GetProperty("cursor").GetString() == "" ? "opaque+/==" : "" } }));
        var page = (await session.DiscoverAsync(null)).Value!;
        Assert.Equal("C1", Assert.Single(page.Items).Address.ChannelId); Assert.Equal("opaque+/==", page.NextCursor);
        Assert.Null((await session.DiscoverAsync(page.NextCursor)).Value!.NextCursor);
        Assert.All(f.Calls.Where(c => c.Method == "conversations.list"), c => Assert.Equal("public_channel", c.Body.GetProperty("types").GetString()));
    }

    [Theory]
    [InlineData("member", "slack-membership-required")]
    [InlineData("archived", "slack-channel-archived")]
    [InlineData("shared", "slack-connect-unsupported")]
    [InlineData("private", "slack-private-channels-disabled")]
    public async Task Destination_validation_reports_repairable_channel_failure(string scenario, string code)
    {
        using var f = new SlackProviderFixture(); await using var session = await f.OpenAsync(privateChannels: false);
        f.Override = (_, method, _, _) => Task.FromResult<HttpResponseMessage?>(method == "conversations.info"
            ? SlackProviderFixture.Json(new { ok = true, channel = SlackProviderFixture.Channel(privateChannel: scenario == "private", member: scenario != "member", archived: scenario == "archived", shared: scenario == "shared") }) : null);
        Assert.Equal(code, (await session.ValidateDestinationAsync(SlackProviderFixture.Address(thread: null))).Failure!.Code);
        Assert.False((await session.ValidateDestinationAsync(SlackProviderFixture.Address(workspace: "OTHER"))).Succeeded);
    }

    [Fact]
    public async Task Root_split_messages_accessibility_and_edits_preserve_opaque_timestamps_and_retire_locally()
    {
        using var f = new SlackProviderFixture(); await using var session = await f.OpenAsync();
        var destination = (await session.ValidateDestinationAsync(SlackProviderFixture.Address(thread: null))).Value!;
        var conversation = (await session.EnsureConversationAsync(destination, ChatConversationPolicy.Thread, "operation", "Parent")).Value!;
        Assert.Equal(conversation.RootMessageId, conversation.Address.ConversationId);
        var message = SlackProviderFixture.Notification(new string('a', 2992) + "😀<&>" + new string('b', 3200));
        var receipt = (await session.SendAsync(conversation.Address, message)).Value!;
        Assert.Equal(3, receipt.Messages.Count);
        var sent = f.Calls.Where(c => c.Method == "chat.postMessage").Skip(1).ToArray();
        Assert.All(sent, c => { Assert.Equal(conversation.RootMessageId, c.Body.GetProperty("thread_ts").GetString()); Assert.False(c.Body.GetProperty("mrkdwn").GetBoolean()); Assert.True(c.Body.GetProperty("blocks")[0].GetProperty("text").GetProperty("text").GetString()!.Length <= 3000); });
        Assert.Equal("Forge\n" + message.Body, string.Concat(sent.Select(c => c.Body.GetProperty("blocks")[0].GetProperty("text").GetProperty("text").GetString())));
        Assert.Equal(1, sent[0].Body.GetProperty("blocks").GetArrayLength()); Assert.Equal(2, sent[^1].Body.GetProperty("blocks").GetArrayLength());
        Assert.Contains("Choices: Answer", sent[^1].Body.GetProperty("text").GetString());
        Assert.Equal(SlackProviderFixture.Key, sent[^1].Body.GetProperty("blocks")[1].GetProperty("elements")[0].GetProperty("value").GetString());
        Assert.True((await session.EditAsync(receipt.Messages[^1], message with { Body = "Resolved", Actions = [] })).Succeeded);
        var update = f.Calls.Last(c => c.Method == "chat.update").Body; Assert.Equal(receipt.Messages[^1].MessageId, update.GetProperty("ts").GetString());
        Assert.Equal(1, update.GetProperty("blocks").GetArrayLength());
        Assert.True((await session.RetireConversationAsync(conversation)).Succeeded);
        Assert.Equal(conversation.RootMessageId, f.Calls.Last(c => c.Method == "chat.update").Body.GetProperty("ts").GetString());
        Assert.False((await session.SendAsync(conversation.Address, message)).Succeeded);
        Assert.DoesNotContain(f.Calls, c => c.Method.Contains("close", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("label", "button-label:75")]
    [InlineData("value", "button-value:2000")]
    [InlineData("key", "action-key:25")]
    [InlineData("text", "text:65536")]
    [InlineData("unicode", "text:65536")]
    [InlineData("buttons", "buttons:20")]
    [InlineData("payload", "payload-bytes:32768")]
    public async Task All_parts_are_preflighted_and_invalid_payloads_have_visible_field_errors(string scenario, string field)
    {
        using var f = new SlackProviderFixture(); await using var session = await f.OpenAsync();
        var normal = SlackProviderFixture.Notification();
        var value = scenario switch
        {
            "label" => normal with { Body = new string('x', 4000), Actions = [new(new string('x', 76), SlackProviderFixture.Key, "question.answer")] },
            "value" => normal with { Actions = [new("Answer", new string('x', 2001), "question.answer")] },
            "key" => normal with { Actions = [new("Answer", "act|restart", "question.answer")] },
            "text" => normal with { Body = new string('x', 65537) }, "unicode" => normal with { Body = "broken\ud800" },
            "buttons" => normal with { Actions = Enumerable.Repeat(normal.Actions[0], 21).ToArray() },
            _ => normal with { Body = new string('界', 2980) },
        };
        Assert.Equal("render-payload-limit:" + field, (await session.SendAsync(SlackProviderFixture.Address(), value)).Failure!.Code);
        Assert.DoesNotContain(f.Calls, c => c.Method == "chat.postMessage");
    }

    [Fact]
    public async Task Partial_and_unknown_creations_are_never_replayed_and_rate_limits_are_workspace_scoped()
    {
        using var f = new SlackProviderFixture(TimeProvider.System); await using var session = await f.OpenAsync(); await using var other = await f.OpenAsync("slack-two", "T2");
        var sends = 0;
        f.Override = (workspace, method, _, _) =>
        {
            if (workspace != "T1" || method != "chat.postMessage" || ++sends == 1) return Task.FromResult<HttpResponseMessage?>(null);
            var response = SlackProviderFixture.Error("ratelimited"); response.StatusCode = HttpStatusCode.TooManyRequests;
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(9)); return Task.FromResult<HttpResponseMessage?>(response);
        };
        var partial = await session.SendAsync(SlackProviderFixture.Address(), SlackProviderFixture.Notification(new string('x', 3500)));
        Assert.Equal(ChatFailureKind.UnknownOutcome, partial.Failure!.Kind); Assert.Single(partial.Value!.Messages);
        var blocked = await session.SendAsync(SlackProviderFixture.Address(), SlackProviderFixture.Notification());
        Assert.Equal(ChatFailureKind.RateLimited, blocked.Failure!.Kind); Assert.InRange(blocked.Failure.RetryAfter!.Value.TotalSeconds, 7, 9);
        Assert.True((await other.SendAsync(SlackProviderFixture.Address("slack-two", "T2"), SlackProviderFixture.Notification())).Succeeded);
        Assert.Equal(2, sends);
    }

    [Fact]
    public async Task Durable_acceptance_precedes_ack_retry_is_not_acked_and_timeout_meets_window()
    {
        using var f = new SlackProviderFixture(); await using var session = await f.OpenAsync(); var socket = Assert.Single(f.Sockets); var sink = new SlackFixtureSink { Result = ChatInboundAcceptance.Retry };
        using var cancel = new CancellationTokenSource(); var receiving = session.ReceiveAsync(sink, cancel.Token);
        socket.BeforeAck = (_, _) => { Assert.NotEmpty(sink.Envelopes); Assert.NotEqual(ChatInboundAcceptance.Retry, sink.Result); return Task.CompletedTask; };
        socket.Push(SlackProviderFixture.Event("retry")); await SlackProviderFixture.UntilAsync(() => sink.Envelopes.Count == 1); Assert.Empty(socket.Acknowledged);
        sink.Result = ChatInboundAcceptance.Accepted; socket.Push(SlackProviderFixture.Event("retry")); await SlackProviderFixture.UntilAsync(() => socket.Acknowledged.Count == 1);
        sink.Result = ChatInboundAcceptance.Duplicate; socket.Push(SlackProviderFixture.Event("retry")); await SlackProviderFixture.UntilAsync(() => socket.Acknowledged.Count == 2);
        var elapsed = Stopwatch.StartNew(); var timedOut = false;
        sink.BeforeAccept = async ct => { try { await Task.Delay(Timeout.Infinite, ct); } finally { timedOut = true; } };
        socket.Push(SlackProviderFixture.Event("slow")); await SlackProviderFixture.UntilAsync(() => timedOut);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3)); Assert.Equal(2, socket.Acknowledged.Count);
        cancel.Cancel(); await receiving.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("public")]
    [InlineData("private")]
    [InlineData("self")]
    [InlineData("bot")]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("unknown-workspace")]
    [InlineData("unknown-app")]
    [InlineData("wrong-installation")]
    [InlineData("cross-workspace")]
    public async Task Socket_normalizes_only_supported_workspace_events(string scenario)
    {
        using var f = new SlackProviderFixture(); await using var session = await f.OpenAsync(); var socket = Assert.Single(f.Sockets); var sink = new SlackFixtureSink();
        using var cancel = new CancellationTokenSource(); var receiving = session.ReceiveAsync(sink, cancel.Token);
        socket.Push(SlackProviderFixture.Event("opaque", workspace: scenario == "unknown-workspace" ? "TOTHER" : "T1", channel: scenario == "private" ? "G1" : "C1",
            sender: scenario == "self" ? "UBOTT1" : "U1", subtype: scenario == "edit" ? "message_changed" : scenario == "delete" ? "message_deleted" : null,
            bot: scenario == "bot" ? "BOTHER" : null, app: scenario == "unknown-app" ? "AOTHER" : "A1",
            authorizer: scenario == "wrong-installation" ? "UOTHERBOT" : null, userTeam: scenario == "cross-workspace" ? "TOTHER" : null));
        await SlackProviderFixture.UntilAsync(() => socket.Acknowledged.Count == 1);
        if (scenario is "unknown-workspace" or "unknown-app" or "wrong-installation" or "cross-workspace") { Assert.Empty(sink.Envelopes); Assert.Single(sink.Diagnostics); }
        else
        {
            var envelope = Assert.Single(sink.Envelopes); Assert.Equal("event:opaque", envelope.DeliveryKey); Assert.Equal("1700000000.000001", envelope.Address.ConversationId);
            Assert.Null(envelope.ReplyToMessageId); Assert.Equal(scenario is "self" or "bot" or "edit" or "delete" ? "ignored" : "text", envelope.Kind);
        }
        cancel.Cancel(); await receiving;
    }

    [Fact]
    public async Task Reconnect_revalidates_handshake_and_preserves_single_receiver_ownership_and_buttons()
    {
        using var f = new SlackProviderFixture(); await using var first = await f.OpenAsync(); await using var second = await f.OpenAsync("slack-two", "T2");
        var sink = new SlackFixtureSink(); using var cancel = new CancellationTokenSource(); var receiving = first.ReceiveAsync(sink, cancel.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.ReceiveAsync(sink, cancel.Token));
        var original = Assert.Single(f.Sockets); original.Push(JsonSerializer.SerializeToElement(new { type = "disconnect", reason = "refresh_requested" }));
        await SlackProviderFixture.UntilAsync(() => f.Sockets.Count == 2); var replacement = f.Sockets.Last();
        replacement.Push(SlackProviderFixture.Button("button", "1700000000.000200", workspace: "T2"));
        await SlackProviderFixture.UntilAsync(() => replacement.Acknowledged.Count == 1);
        var envelope = Assert.Single(sink.Envelopes); Assert.Equal("slack-two", envelope.ConnectionId); Assert.Equal("T2", envelope.Address.ExternalTenantId);
        Assert.Equal("1700000000.000200", envelope.ReplyToMessageId); Assert.Equal(SlackProviderFixture.Key, envelope.ActionToken);
        await first.DisposeAsync(); Assert.False(replacement.Disposed);
        replacement.Push(SlackProviderFixture.Event("still-live", workspace: "T2")); await SlackProviderFixture.UntilAsync(() => replacement.Acknowledged.Count == 2);
        cancel.Cancel(); await receiving; Assert.True(original.Disposed); Assert.True(replacement.Disposed);
    }

    [Fact]
    public async Task A_reconnect_with_the_wrong_app_is_rejected_before_events_and_a_later_valid_handshake_recovers()
    {
        using var f = new SlackProviderFixture(); f.CreateSocket = index => new(index == 1 ? "AOTHER" : "A1");
        await using var session = await f.OpenAsync(); var sink = new SlackFixtureSink(); using var cancel = new CancellationTokenSource();
        var receiving = session.ReceiveAsync(sink, cancel.Token); f.Sockets.First().Push(null);
        await SlackProviderFixture.UntilAsync(() => f.Sockets.Count >= 3);
        Assert.True(f.Sockets.ElementAt(1).Disposed); Assert.Empty(f.Sockets.ElementAt(1).Acknowledged);
        f.Sockets.Last().Push(SlackProviderFixture.Event("recovered")); await SlackProviderFixture.UntilAsync(() => sink.Envelopes.Count == 1);
        Assert.Null(session.ReceptionFailureCode); cancel.Cancel(); await receiving;
    }
}