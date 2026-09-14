// -----------------------------------------------------------------------
// <copyright file="ChatSlackIdentityConfirmationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Text.Json;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ChatSlackIdentityConfirmationTests
{
    private static async Task<ChatSlackLinkConfirmation> RequestAsync(ChatSlackRuntimeTestFactory f, string sender = "U1", string destination = "dest-slack-one")
        => await f.ReadAsync(async sp => await sp.GetRequiredService<ChatLinkService>().BeginSlackConfirmationAsync(
            await sp.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", "owner-p"), "slack-one", new(destination, sender), default));

    private static string Token(ChatSlackRuntimeTestFactory f) => f.Slack.Calls.Last(c => c.Method == "chat.postEphemeral")
        .Body.GetProperty("blocks")[1].GetProperty("elements")[0].GetProperty("value").GetString()!;

    private static JsonElement Click(string id, string token, string sender = "U1", string workspace = "T1")
        => SlackProviderFixture.EphemeralButton(id, token, workspace, sender);

    [Fact]
    public async Task Private_button_links_only_the_requested_user_and_workspace_without_public_reply_or_browser_token()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(linked: false, secondWorkspace: true);
        var confirmation = await RequestAsync(f);
        Assert.Equal("https://slack.com/app_redirect?team=T1&channel=C1", confirmation.DeepLink);
        var call = Assert.Single(f.Slack.Calls.Where(c => c.Method == "chat.postEphemeral"));
        Assert.Equal("U1", call.Body.GetProperty("user").GetString()); Assert.Equal("C1", call.Body.GetProperty("channel").GetString());
        var token = Token(f); Assert.DoesNotContain(token, JsonSerializer.Serialize(confirmation));
        var identities = f.Services.GetRequiredService<HostIdentityStore>();
        Assert.Null(identities.FindLink("slack", "T1", "U1"));
        await f.ReceiveAsync(Click("other-user", token, "UOTHER")); await f.DispatchAsync();
        await f.ReceiveAsync(Click("other-workspace", token, workspace: "T2")); await f.DispatchAsync();
        Assert.Null(identities.FindLink("slack", "T1", "U1"));
        await f.ReceiveAsync(Click("confirmed", token)); await f.DispatchAsync(); await f.RespondAsync();
        Assert.Equal("owner-p", identities.FindLink("slack", "T1", "U1")?.UserId);
        await f.ReceiveAsync(Click("replay", token)); await f.DispatchAsync();
        Assert.Equal("link-invalid-or-expired", (await f.InboxAsync()).Single(i => i.DeliveryKey == "envelope:replay").Result);
        Assert.All(await f.InboxAsync(), row => Assert.DoesNotContain(token, row.EnvelopeJson));
        Assert.DoesNotContain(f.Slack.Calls, c => c.Method == "chat.postMessage");
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("expire")]
    [InlineData("rotation")]
    [InlineData("retry")]
    public async Task Cancelled_expired_replaced_and_old_revision_buttons_cannot_link(string scenario)
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(linked: false);
        await RequestAsync(f); var token = Token(f);
        if (scenario == "retry") await RequestAsync(f);
        else await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>();
            if (scenario == "cancel") await sp.GetRequiredService<ChatLinkService>().CancelSlackConfirmationAsync(
                await sp.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", "owner-p"), "slack-one", default);
            if (scenario == "expire") (await db.ChatLinkChallenges.SingleAsync()).ExpiresAt = 0;
            if (scenario == "rotation") (await db.ChatConnections.SingleAsync(c => c.Id == "slack-one")).Revision++;
            await db.SaveChangesAsync();
        });
        await f.ReceiveAsync(Click("stale", token)); await f.DispatchAsync();
        Assert.Null(f.Services.GetRequiredService<HostIdentityStore>().FindLink("slack", "T1", "U1"));
        Assert.Equal("link-invalid-or-expired", Assert.Single(await f.InboxAsync()).Result);
    }

    [Theory]
    [InlineData("user_not_in_channel", "slack-link-user-not-in-channel")]
    [InlineData("missing_scope", "slack-bot-scope-required")]
    [InlineData("request_timeout", "slack-send-outcome-unknown")]
    public async Task Provider_failure_is_actionable_and_leaves_no_usable_challenge(string error, string expected)
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(linked: false);
        f.Slack.Override = (_, method, _, _) => Task.FromResult<System.Net.Http.HttpResponseMessage?>(method == "chat.postEphemeral" ? SlackProviderFixture.Error(error) : null);
        var failure = await Assert.ThrowsAsync<ChatConfigurationException>(() => RequestAsync(f));
        Assert.Equal(expected, failure.Message);
        Assert.Equal("cancelled", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatLinkChallenges.Select(c => c.State).SingleAsync()));
    }

    [Theory]
    [InlineData("username", "dest-slack-one")]
    [InlineData("U1", "foreign-destination")]
    public async Task Invalid_target_does_not_send(string sender, string destination)
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(linked: false);
        await Assert.ThrowsAsync<ChatConfigurationException>(() => RequestAsync(f, sender, destination));
        Assert.DoesNotContain(f.Slack.Calls, c => c.Method == "chat.postEphemeral");
    }

    [Fact]
    public async Task Product_grants_are_required_before_disclosing_channels_or_sending_a_request()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(linked: false);
        await f.WithAsync(async sp =>
        {
            var actor = await sp.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", "owner-q");
            var links = sp.GetRequiredService<ChatLinkService>();
            await Assert.ThrowsAsync<ChatConfigurationException>(() => links.SlackDestinationsAsync(actor, "slack-one", default));
            await Assert.ThrowsAsync<ChatConfigurationException>(() => links.BeginSlackConfirmationAsync(actor, "slack-one", new("dest-slack-one", "U1"), default));
        });
        Assert.DoesNotContain(f.Slack.Calls, c => c.Method == "chat.postEphemeral");
    }

    [Fact]
    public async Task Precancelled_provider_request_has_no_network_effect()
    {
        var provider = new SlackProviderFixture(); await using var session = await provider.OpenAsync(); var before = provider.Calls.Count;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RequestIdentityLinkAsync(SlackProviderFixture.Address(thread: null), "U1", new string('A', 64), cancel.Token));
        Assert.Equal(before, provider.Calls.Count);
    }
}