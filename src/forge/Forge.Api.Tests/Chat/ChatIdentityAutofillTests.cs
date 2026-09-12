// -----------------------------------------------------------------------
// <copyright file="ChatIdentityAutofillTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ChatIdentityAutofillTests
{
    [Fact]
    public async Task Expired_previous_attempt_restores_own_input_without_marking_the_account_linked_or_exposing_proof()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(linked: false);
        using var owner = await f.PairedClientAsync("owner-p");
        var issued = await owner.PostAsJsonAsync("/api/chat/connections/slack-one/link-challenge", new ChatConnectionLinkRequest("T1", "U1"));
        issued.EnsureSuccessStatusCode();
        var challenge = (await issued.Content.ReadFromJsonAsync<ChatIdentityChallenge>())!;
        f.Clock.Advance(TimeSpan.FromMinutes(3));
        var response = await owner.GetStringAsync("/api/chat/management");
        var management = JsonSerializer.Deserialize<ChatManagementView>(response, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(new("slack-one", "T1", "U1"), Assert.Single(management.RecentLinkTargets));
        Assert.DoesNotContain(management.Links, l => l.ProviderId == "slack");
        Assert.DoesNotContain(challenge.Token, response);
        Assert.DoesNotContain(ChatEventCatalog.Hash(challenge.Token), response);
        using var other = await f.PairedClientAsync("reader-p");
        Assert.Empty((await other.GetFromJsonAsync<ChatManagementView>("/api/chat/management"))!.RecentLinkTargets);
        Assert.DoesNotContain(f.Slack.Calls, c => c.Method == "chat.postEphemeral");
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("user")]
    [InlineData("disabled")]
    [InlineData("grant")]
    [InlineData("invalid-id")]
    [InlineData("revoked")]
    [InlineData("rejected")]
    public async Task Hints_do_not_cross_current_account_connection_or_permission_boundaries(string scenario)
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(linked: false);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>();
            db.ChatLinkChallenges.Add(new()
            {
                Hash = "fixture", ConnectionId = "slack-one", UserId = scenario == "user" ? "reader-p" : "owner-p",
                ExternalTenantId = scenario == "workspace" ? "TOTHER" : "T1", ExternalUserId = scenario == "invalid-id" ? "bot-name" : "U1",
                State = scenario is "revoked" or "rejected" ? scenario : "pending", ExpiresAt = 1,
            });
            if (scenario == "disabled") (await db.ChatConnections.SingleAsync()).Enabled = false;
            if (scenario == "grant") db.ChatConnectionProducts.RemoveRange(await db.ChatConnectionProducts.ToListAsync());
            await db.SaveChangesAsync();
        });
        using var owner = await f.PairedClientAsync("owner-p");
        Assert.Empty((await owner.GetFromJsonAsync<ChatManagementView>("/api/chat/management"))!.RecentLinkTargets);
    }

    [Fact]
    public async Task Newest_input_is_used_and_revoking_links_clears_pending_and_cancelled_input()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(linked: false);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>();
            db.ChatLinkChallenges.AddRange(
                new() { Hash = "old", ConnectionId = "slack-one", UserId = "owner-p", ExternalTenantId = "T1", ExternalUserId = "UOLD", State = "pending", ExpiresAt = 1 },
                new() { Hash = "latest", ConnectionId = "slack-one", UserId = "owner-p", ExternalTenantId = "T1", ExternalUserId = "UNEW", State = "cancelled", ExpiresAt = 2 });
            await db.SaveChangesAsync();
        });
        using var owner = await f.PairedClientAsync("owner-p");
        Assert.Equal("UNEW", Assert.Single((await owner.GetFromJsonAsync<ChatManagementView>("/api/chat/management"))!.RecentLinkTargets).ExternalUserId);
        (await owner.DeleteAsync("/api/chat/operator-links/self")).EnsureSuccessStatusCode();
        Assert.Empty((await owner.GetFromJsonAsync<ChatManagementView>("/api/chat/management"))!.RecentLinkTargets);
    }

    [Fact]
    public async Task Simultaneous_different_account_inputs_are_left_for_the_user_to_choose()
    {
        await using var f = new ChatSlackRuntimeTestFactory(); await f.SetupAsync(linked: false);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>();
            foreach (var id in new[] { "U1", "U2" }) db.ChatLinkChallenges.Add(new()
            { Hash = id, ConnectionId = "slack-one", UserId = "owner-p", ExternalTenantId = "T1", ExternalUserId = id, ExpiresAt = 1 });
            await db.SaveChangesAsync();
        });
        using var owner = await f.PairedClientAsync("owner-p");
        Assert.Empty((await owner.GetFromJsonAsync<ChatManagementView>("/api/chat/management"))!.RecentLinkTargets);
    }
}