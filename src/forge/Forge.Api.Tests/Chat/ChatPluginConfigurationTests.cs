// -----------------------------------------------------------------------
// <copyright file="ChatPluginConfigurationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ChatPluginConfigurationTests
{
    private const string ParentPath = "/api/products/p/story-spawns/shared/plugins/chat";

    [Fact]
    public async Task Two_Products_Same_Spawn_Id_Three_Connections_And_Future_Children_Stay_Isolated()
    {
        await using var app = new ChatPluginTestFactory(); await app.SeedAsync(); await app.ConnectionsAsync();
        var other = await app.RunAsync("owner-q", (s, a) => s.GetPluginAsync(a, "q", "shared"));
        Assert.False(other.Enabled); Assert.Equal(0, other.Revision);
        var parent = await app.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", ChatPluginTestFactory.Plugin()));
        foreach (var id in new[] { "alpha-one", "alpha-two", "beta" })
        {
            await app.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), "sub-" + id,
                new(0, parent.Revision, "dest-" + id, id == "beta" ? "thread" : "topic")));
            parent = await app.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"));
        }
        Assert.Equal(3, parent.Subscriptions.Count);
        Assert.Equal(3, (await app.RunAsync("owner-p", (s, a) => s.ListConnectionsAsync(a, "p"))).Count);
        using (var routingScope = app.Services.CreateScope())
        {
            var control = routingScope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var registry = routingScope.ServiceProvider.GetRequiredService<IChatProviderRegistry>();
            foreach (var subscription in parent.Subscriptions)
            {
                var destination = await control.ChatDestinations.SingleAsync(d => d.Id == subscription.DestinationId);
                var connection = await control.ChatConnections.SingleAsync(c => c.Id == destination.ConnectionId);
                using var settings = JsonDocument.Parse(connection.SettingsJson);
                var configuration = new ChatConnectionConfiguration(connection.Id, connection.ProviderId, connection.InstallationId,
                    connection.SettingsVersion, connection.Revision, settings.RootElement, new Dictionary<string, ChatSecretReference>());
                var provider = registry.Resolve(configuration.ProviderId)!;
                await using var session = (await provider.OpenSessionAsync(configuration, new FakeSecretStore())).Value!;
                var address = new ChatAddress(connection.Id, destination.ExternalTenantId, destination.ChannelId);
                var receipt = (await session.SendAsync(address, new("n", "open-questions", "Title", "Metadata", 1, []))).Value!;
                Assert.Equal(address, Assert.Single(receipt.Messages).Address);
            }
        }
        using (var scope = app.Services.CreateScope())
        {
            var dbs = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>();
            var db = dbs.CreateFor("p");
            Assert.NotEqual(db.Database.GetDbConnection().DataSource, dbs.CreateFor("q").Database.GetDbConnection().DataSource);
            var spawn = await db.StorySpawns.SingleAsync(); spawn.StoryTitle = "Renamed"; spawn.Phase = "done";
            db.Workstreams.Add(new WorkstreamRow { Id = "new-child", RepoId = "repo-p", StorySpawnId = "shared", Status = "pending" });
            await db.SaveChangesAsync();
            // Neither configuring nor disabling Chat changes execution state.
            Assert.Equal(32, await db.Workstreams.CountAsync(w => w.Status == "running"));
        }
        foreach (var child in new[] { "child-0", "child-31", "new-child" })
        {
            var inherited = await app.RunAsync("owner-p", (s, a) => s.GetForChildAsync(a, "p", child));
            Assert.Equal(parent.ActivationGeneration, inherited.ActivationGeneration);
            Assert.Equal(parent.Revision, inherited.Revision); Assert.Equal(3, inherited.Subscriptions.Count);
        }
        Assert.Empty((await app.RunAsync("owner-q", (s, a) => s.GetForChildAsync(a, "q", "child-0"))).Subscriptions);
        await app.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", ChatPluginTestFactory.Plugin(parent.Revision, false)));
        using var finalScope = app.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
        Assert.Equal("done", (await finalDb.StorySpawns.SingleAsync()).Phase);
        Assert.Equal(32, await finalDb.Workstreams.CountAsync(w => w.Status == "running"));
    }

    [Fact]
    public async Task Capability_And_Connection_Grants_Are_Rechecked_And_Duplicates_And_Stale_Writes_Reject()
    {
        await using var app = new ChatPluginTestFactory(); await app.SeedAsync(); await app.ConnectionsAsync();
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => app.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "q", "shared")));
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => app.RunAsync("admin", (s, a) => s.PutPluginAsync(a, "p", "shared", ChatPluginTestFactory.Plugin())));
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => app.RunAsync("reader-p", (s, a) => s.PutPluginAsync(a, "p", "shared", ChatPluginTestFactory.Plugin())));
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => app.RunAsync("owner-p", (s, a) => s.PutConnectionAsync(a, "c", ChatPluginTestFactory.Connection("FakeAlpha", "c"))));
        await Assert.ThrowsAsync<ChatConfigurationException>(() => app.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "duplicate", ChatPluginTestFactory.Connection("FakeAlpha", "alpha-one"))));
        await app.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "alpha-two", ChatPluginTestFactory.Connection("FakeAlpha", "alpha-two", 1, "q")));
        Assert.Equal(2, (await app.RunAsync("owner-p", (s, a) => s.ListConnectionsAsync(a, "p"))).Count);
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => app.RunAsync("owner-p", (s, a) => s.ListDestinationsAsync(a, "alpha-two", "p")));
        var parent = await app.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", ChatPluginTestFactory.Plugin()));
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => app.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), "denied", new(0, parent.Revision, "dest-alpha-two", "flat"))));
        await app.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), "sub", new(0, parent.Revision, "dest-alpha-one", "flat")));
        var stale = await Assert.ThrowsAsync<ChatConfigurationException>(() => app.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", ChatPluginTestFactory.Plugin(parent.Revision))));
        Assert.Equal("revision-conflict", stale.Code);
        parent = await app.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"));
        var duplicate = await Assert.ThrowsAsync<ChatConfigurationException>(() => app.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), "dup", new(0, parent.Revision, "dest-alpha-one", "flat"))));
        Assert.Equal(409, duplicate.Status);
        // A failed subscription insert rolls back its parent revision too.
        Assert.Equal(parent.Revision, (await app.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"))).Revision);
        await app.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "alpha-one", ChatPluginTestFactory.Connection("FakeAlpha", "alpha-one", 1, "q")));
        Assert.Empty((await app.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"))).Subscriptions);
    }

    [Fact]
    public async Task Unsupported_Versions_Preserve_Data_And_Pause_Only_Affected_Settings()
    {
        await using var app = new ChatPluginTestFactory(); await app.SeedAsync(); await app.ConnectionsAsync();
        var incompatible = ChatPluginTestFactory.Connection("FakeAlpha", "alpha-one", 1) with { SettingsVersion = 99 };
        var bad = await app.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "alpha-one", incompatible));
        Assert.Equal("paused", bad.State); Assert.Equal("unsupported-connection-settings-version", bad.Reason);
        var all = await app.RunAsync("admin", (s, a) => s.ListConnectionsAsync(a, null));
        Assert.All(all.Where(c => c.Id != "alpha-one"), c => Assert.Equal("configured", c.State));
        var p = await app.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", ChatPluginTestFactory.Plugin(version: 99)));
        Assert.Equal("unsupported-plugin-settings-version", p.Reason); Assert.True(p.Enabled);
        var q = await app.RunAsync("owner-q", (s, a) => s.PutPluginAsync(a, "q", "shared", ChatPluginTestFactory.Plugin()));
        Assert.Equal("needs-setup", q.State);
        p = await app.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", ChatPluginTestFactory.Plugin(p.Revision)));
        Assert.Equal("needs-setup", p.State);
        Assert.True((await app.RunAsync("admin", (s, a) => s.GetConnectionAsync(a, "alpha-one"))).Enabled);
    }

    [Fact]
    public async Task Recreated_Run_Requires_Activation_And_Retires_Old_Subscriptions_And_Bindings()
    {
        await using var app = new ChatPluginTestFactory(); await app.SeedAsync(); await app.ConnectionsAsync();
        var parent = await app.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", ChatPluginTestFactory.Plugin()));
        await app.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), "old", new(0, parent.Revision, "dest-alpha-one", "topic")));
        parent = await app.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            var old = await db.StorySpawns.SingleAsync();
            var created = old.CreatedAt; db.StorySpawns.Remove(old); await db.SaveChangesAsync();
            db.StorySpawns.Add(new StorySpawnRow { Id = "shared", StoryId = "story", RepoId = "repo-p", CreatedAt = created.AddSeconds(1) }); await db.SaveChangesAsync();
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            control.ChatConversationBindings.Add(new ChatConversationBindingRow { Id = "binding", SubscriptionId = "old", SubscriptionRevision = 1,
                ConnectionId = "alpha-one", ExternalTenantId = "tenant", ChannelId = "channel", ConversationId = "old-topic", ActivationGeneration = parent.ActivationGeneration });
            await control.SaveChangesAsync();
        }
        var view = await app.RunAsync("owner-p", (s, a) => s.GetForChildAsync(a, "p", "child-0"));
        Assert.False(view.Enabled); Assert.Empty(view.Subscriptions); Assert.Equal("new-run-needs-activation", view.Reason);
        await Assert.ThrowsAsync<ChatConfigurationException>(() => app.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", ChatPluginTestFactory.Plugin(parent.Revision))));
        await Assert.ThrowsAsync<ChatConfigurationException>(() => app.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), "new", new(0, parent.Revision, "dest-alpha-one", "topic"))));
        var activated = await app.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", ChatPluginTestFactory.Plugin(parent.Revision) with { ActivateNewRun = true }));
        Assert.True(activated.Enabled); Assert.Empty(activated.Subscriptions); Assert.True(activated.ActivationGeneration > parent.ActivationGeneration);
        using var final = app.Services.CreateScope();
        var finalDb = final.ServiceProvider.GetRequiredService<ControlDbContext>();
        Assert.NotNull((await finalDb.ChatSubscriptions.SingleAsync()).RetiredAt);
        Assert.NotNull((await finalDb.ChatConversationBindings.SingleAsync()).RetiredAt);
    }

    [Fact]
    public async Task Explicit_Copy_Is_Disabled_And_Has_New_Subscriptions_And_No_Bindings()
    {
        await using var app = new ChatPluginTestFactory(); await app.SeedAsync(); await app.ConnectionsAsync();
        var source = await app.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", ChatPluginTestFactory.Plugin()));
        await app.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), "original", new(0, source.Revision, "dest-beta", "thread")));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            db.StorySpawns.Add(new StorySpawnRow { Id = "copy", StoryId = "copy-story", RepoId = "repo-p" }); await db.SaveChangesAsync();
        }
        var copy = await app.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "copy", ChatPluginTestFactory.Plugin(enabled: false) with { CopyFromSpawnId = "shared" }));
        Assert.False(copy.Enabled); Assert.Equal(ChatConfigurationScope.Spawn("p", "shared").Key, copy.CopyOrigin);
        Assert.NotEqual("original", Assert.Single(copy.Subscriptions).Id);
        var enabled = await app.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "copy", ChatPluginTestFactory.Plugin(copy.Revision)));
        Assert.True(enabled.ActivationGeneration > copy.ActivationGeneration);
        Assert.Equal(enabled.ActivationGeneration, Assert.Single(enabled.Subscriptions).ActivationGeneration);
    }

    [Fact]
    public async Task Host_Scope_Is_Permanent_Notification_Only_And_Admin_Configured()
    {
        await using var app = new ChatPluginTestFactory(); await app.SeedAsync(); await app.ConnectionsAsync();
        var host = await app.RunAsync("admin", (s, a) => s.GetHostSetupAsync(a));
        Assert.Equal("host-alerts-need-destination", host.Reason);
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => app.RunAsync("owner-p", (s, a) => s.GetHostSetupAsync(a)));
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => app.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Host("test-host"), "host", new(0, 0, "dest-beta", "thread"))));
        var subscription = await app.RunAsync("admin", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Host("test-host"), "host", new(0, 0, "dest-beta", "thread")));
        Assert.Null(subscription.ProductId); Assert.Null(subscription.SpawnId); Assert.Null(subscription.PluginId);
        Assert.False(subscription.Interactive); Assert.Contains("queue-strain", subscription.EventKindsJson); Assert.Contains("human-interaction", subscription.EventKindsJson);
        await Assert.ThrowsAsync<ChatConfigurationException>(() => app.RunAsync("admin", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Host("test-host"), "host", new(1, 0, "dest-beta", "thread", Interactive: true))));
        Assert.Equal(host.ScopeKey, (await app.RunAsync("admin", (s, a) => s.GetHostSetupAsync(a))).ScopeKey);
        await app.RunAsync("admin", async (s, a) => { await s.DeleteDestinationAsync(a, "beta", "dest-beta", 1); return true; });
        Assert.Equal("host-alerts-need-destination", (await app.RunAsync("admin", (s, a) => s.GetHostSetupAsync(a))).Reason);
    }

    [Fact]
    public async Task Http_Uses_Route_Ownership_Real_Cookies_Csrf_And_Revisions()
    {
        await using var app = new ChatPluginTestFactory(); await app.SeedAsync(); await app.ConnectionsAsync();
        using var owner = await app.PairedClientAsync("owner-p");
        using var reader = await app.PairedClientAsync("reader-p");
        using var admin = await app.PairedClientAsync("admin");
        using var anonymous = app.AnonymousClient(); anonymous.DefaultRequestHeaders.Add("X-Forge-User", "admin");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/chat/providers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(ParentPath, ChatPluginTestFactory.Plugin())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync(ParentPath, ChatPluginTestFactory.Plugin())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(ParentPath.Replace("/p/", "/q/"))).StatusCode);
        var saved = await owner.PutAsJsonAsync(ParentPath, ChatPluginTestFactory.Plugin()); saved.EnsureSuccessStatusCode();
        var parent = (await saved.Content.ReadFromJsonAsync<ChatPluginView>())!;
        var sub = await owner.PutAsJsonAsync(ParentPath + "/subscriptions/sub", new ChatSubscriptionWrite(0, parent.Revision, "dest-alpha-one", "topic"));
        sub.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(ParentPath, ChatPluginTestFactory.Plugin(parent.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/chat/connections/alpha-one")).StatusCode);
        var details = (await admin.GetFromJsonAsync<ChatConnectionDetails>("/api/chat/connections/alpha-one"))!;
        Assert.Equal(new[] { "p", "q" }, details.ProductIds.OrderBy(p => p));
        Assert.Empty(details.SecretReferences);
        Assert.Equal(3, (await owner.GetFromJsonAsync<ChatConnectionView[]>("/api/chat/connections?productId=p"))!.Length);
        owner.DefaultRequestHeaders.Remove(OperatorAuthentication.CsrfHeader);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync(ParentPath, ChatPluginTestFactory.Plugin())).StatusCode);
        using var scope = app.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        control.UserProductRoles.Remove(await control.UserProductRoles.SingleAsync(r => r.UserId == "owner-p")); await control.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(ParentPath)).StatusCode);
    }

    [Fact]
    public async Task Validation_Rejects_Unsupported_Policies_Filters_Settings_And_Inaccessible_Copy()
    {
        await using var app = new ChatPluginTestFactory(); await app.SeedAsync(); await app.ConnectionsAsync();
        var parent = await app.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", ChatPluginTestFactory.Plugin()));
        var scope = ChatConfigurationScope.Spawn("p", "shared");
        var valid = new ChatSubscriptionWrite(0, parent.Revision, "dest-beta", "thread");
        foreach (var request in new[] { valid with { ConversationPolicy = "topic" }, valid with { EventKinds = ["queue-strain"] },
            valid with { EventKinds = ["future-kind"] }, valid with { EventKinds = ["open-questions", "open-questions"] },
            valid with { CatalogVersion = 99 }, valid with { Interactive = true } })
            await Assert.ThrowsAsync<ChatConfigurationException>(() => app.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, scope, "bad", request)));
        var invalidSettings = ChatPluginTestFactory.Plugin(parent.Revision) with { Settings = JsonSerializer.SerializeToElement(new { unknown = true }) };
        await Assert.ThrowsAsync<ChatConfigurationException>(() => app.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "shared", invalidSettings)));
        var invalidConnection = ChatPluginTestFactory.Connection("FakeAlpha", "alpha-one", 1) with { Settings = invalidSettings.Settings };
        await Assert.ThrowsAsync<ChatConfigurationException>(() => app.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "alpha-one", invalidConnection)));
        await Assert.ThrowsAsync<ChatConfigurationException>(() => app.RunAsync("admin", (s, a) => s.PutDestinationAsync(a, "beta", "duplicate", new(0, "tenant", "channel", "same-address", "group"))));
        await app.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, scope, "source", valid));
        await app.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "beta", ChatPluginTestFactory.Connection("FakeBeta", "beta", 1, "q")));
        using (var dbScope = app.Services.CreateScope())
        {
            var db = dbScope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            db.StorySpawns.Add(new StorySpawnRow { Id = "copy", StoryId = "copy", RepoId = "repo-p" }); await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => app.RunAsync("owner-p", (s, a) => s.PutPluginAsync(a, "p", "copy",
            ChatPluginTestFactory.Plugin(enabled: false) with { CopyFromSpawnId = "shared" })));
        Assert.Equal(0, (await app.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "copy"))).Revision);
    }

    [Fact]
    public async Task Http_Subscription_Update_Delete_And_Connection_Retirement_Use_Current_Revisions()
    {
        await using var app = new ChatPluginTestFactory(); await app.SeedAsync(); await app.ConnectionsAsync();
        using var owner = await app.PairedClientAsync("owner-p");
        using var admin = await app.PairedClientAsync("admin");
        (await owner.PutAsJsonAsync(ParentPath, ChatPluginTestFactory.Plugin())).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync(ParentPath + "/subscriptions/sub", new ChatSubscriptionWrite(0, 1, "dest-alpha-one", "topic"))).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync(ParentPath + "/subscriptions/sub", new ChatSubscriptionWrite(1, 2, "dest-alpha-one", "topic", Paused: true, EventKinds: []))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync(ParentPath + "/subscriptions/sub?expectedRevision=1&parentRevision=3")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(ParentPath + "/subscriptions/sub?expectedRevision=2&parentRevision=3")).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<ChatPluginView>(ParentPath))!.Subscriptions);
        (await owner.PutAsJsonAsync(ParentPath + "/subscriptions/replacement", new ChatSubscriptionWrite(0, 4, "dest-alpha-one", "topic"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync("/api/chat/connections/alpha-one?expectedRevision=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/chat/connections/alpha-one?expectedRevision=1")).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<ChatPluginView>(ParentPath))!.Subscriptions);
        Assert.Equal(2, (await owner.GetFromJsonAsync<ChatConnectionView[]>("/api/chat/connections?productId=p"))!.Length);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/chat/connections/alpha-one")).StatusCode);
    }
}