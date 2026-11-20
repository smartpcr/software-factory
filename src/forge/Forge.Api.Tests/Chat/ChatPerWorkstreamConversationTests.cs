// -----------------------------------------------------------------------
// <copyright file="ChatPerWorkstreamConversationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// docs/chat/chat-per-workstream-conversations.md — "set up once" (All-products
/// connections, host default) and one topic/thread per workstream. Test ids U1–U7
/// refer to section C of that document.
/// </summary>
public sealed class ChatPerWorkstreamConversationTests
{
    // ── U1 · D1: All-products connections ──────────────────────────────────

    [Fact]
    public async Task U1_AllProducts_Connection_Is_Granted_To_Every_Product_Including_New_Ones()
    {
        await using var f = new ChatPluginTestFactory(); await f.SeedAsync();

        // Ticked "All products" while listing only p: q is granted too.
        await f.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "everyone",
            ChatPluginTestFactory.Connection("FakeAlpha", "everyone", 0, "p") with { AllProducts = true }));
        // A product-limited connection stays limited.
        await f.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "only-p", ChatPluginTestFactory.Connection("FakeAlpha", "only-p", 0, "p")));

        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var everyProduct = (await control.Products.AsNoTracking().Select(p => p.Id).ToListAsync()).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Contains("q", everyProduct);
        Assert.Equal(everyProduct, await Grants(control, "everyone"));
        Assert.Equal(new[] { "p" }, await Grants(control, "only-p"));
        Assert.True((await control.ChatConnections.SingleAsync(c => c.Id == "everyone")).AllProducts);

        // A product created later receives the All-products connection, not the limited one.
        control.Products.Add(new ProductRow { Id = "later", Name = "Later", Slug = "later", CreatedAt = DateTimeOffset.UtcNow });
        Assert.Equal(1, await ChatConfigurationService.GrantAllProductsConnectionsAsync(control, "later"));
        await control.SaveChangesAsync();
        Assert.Contains("later", await Grants(control, "everyone"));
        Assert.DoesNotContain("later", await Grants(control, "only-p"));
        // Idempotent: a second call stages nothing.
        Assert.Equal(0, await ChatConfigurationService.GrantAllProductsConnectionsAsync(control, "later"));
    }

    [Fact]
    public async Task U1_A_Connection_Migrated_From_The_Legacy_Bridge_Can_Be_Resaved_With_Its_Unchanged_Token_Reference()
    {
        await using var f = new ChatPluginTestFactory(); await f.SeedAsync();
        await f.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "legacy", ChatPluginTestFactory.Connection("telegram", "legacy", 0, "p")));
        // The legacy import stores a file-path reference that new references may not use.
        var legacyRef = new ChatSecretReference("legacy-host", @"dpapi:C:\Users\op\AppData\Local\Forge\chat-token.bin");
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var row = await control.ChatConnections.SingleAsync(c => c.Id == "legacy");
            row.SecretReferencesJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, ChatSecretReference> { ["bot-token"] = legacyRef });
            await control.SaveChangesAsync();
        }
        var revision = (await f.RunAsync("admin", (s, a) => s.GetConnectionAsync(a, "legacy"))).Revision;
        var refs = new Dictionary<string, ChatSecretReference> { ["bot-token"] = legacyRef };

        // Unchanged reference: the save (here, granting All products) succeeds.
        var saved = await f.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "legacy",
            ChatPluginTestFactory.Connection("telegram", "legacy", revision, "p") with { SecretReferences = refs, AllProducts = true }));
        Assert.True(saved.AllProducts);
        // A changed reference is still validated.
        var bad = new Dictionary<string, ChatSecretReference> { ["bot-token"] = new("legacy-host", @"dpapi:C:\other.bin") };
        Assert.Equal("invalid-credential-reference", (await Assert.ThrowsAsync<ChatConfigurationException>(() => f.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "legacy",
            ChatPluginTestFactory.Connection("telegram", "legacy", saved.Revision, "p") with { SecretReferences = bad })))).Code);
    }

    // ── Prerequisite: products still living in the shared control DB ───────

    [Fact]
    public async Task Product_Without_Its_Own_Db_File_Is_Available_Only_When_Its_Rows_Live_In_The_Control_Db()
    {
        await using var f = new ChatPluginTestFactory(); await f.SeedAsync();
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<ChatScopeResolver>();

        // Created after the split and not moved (harness, 2026-11-19): no file, repo row in the control DB.
        var fresh = new ProductRow { Id = "fresh", Name = "Fresh", Slug = "fresh", CreatedAt = DateTimeOffset.UtcNow };
        var lost = new ProductRow { Id = "lost", Name = "Lost", Slug = "lost", CreatedAt = DateTimeOffset.UtcNow };
        control.Products.AddRange(fresh, lost);
        await control.SaveChangesAsync();
        var factory = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>();
        var fallback = factory.CreateFor("fresh");
        // The live control DB still holds a repos table (harness/release rows live there); the
        // test host's repo move dropped it, so recreate it from the EF model's definition.
        var reposDdl = fallback.Database.GenerateCreateScript().Split(';')
            .Select(statement => statement.Trim())
            .Single(statement => statement.StartsWith("CREATE TABLE \"repos\"", StringComparison.Ordinal));
        await fallback.Database.ExecuteSqlRawAsync(reposDdl.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal));
        fallback.Repos.Add(new RepoRow { Id = "repo-fresh", ProductId = "fresh", DisplayName = "fresh", Kind = "local", RemoteUrl = "file://fresh", DefaultBranch = "main" });
        await fallback.SaveChangesAsync();

        Assert.True(resolver.ProductAvailable(await control.Products.SingleAsync(p => p.Id == "p")), "own file");
        Assert.True(resolver.ProductAvailable(fresh), "rows in the shared control DB");
        // No file and no rows anywhere: unavailable, so its chat config is degraded, never retired as deleted.
        Assert.False(resolver.ProductAvailable(lost));
    }

    [Fact]
    public async Task U3_Host_Default_Enables_A_Shared_Db_Story_Only_Under_The_Product_That_Owns_Its_Repo()
    {
        // harness and release (2026-11-19) both lack DB files and read the shared control DB;
        // harness:DSH was enabled under BOTH until the sweep checked repo ownership.
        await using var f = new ChatPluginTestFactory(); await f.SeedAsync(); await f.ConnectionsAsync();
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            control.Products.AddRange(
                new ProductRow { Id = "owner", Name = "Owner", Slug = "owner", CreatedAt = DateTimeOffset.UtcNow },
                new ProductRow { Id = "neighbour", Name = "Neighbour", Slug = "neighbour", CreatedAt = DateTimeOffset.UtcNow });
            await control.SaveChangesAsync();
            foreach (var connection in new[] { "alpha-one", "beta" })
            {
                control.ChatConnectionProducts.Add(new ChatConnectionProductRow { ConnectionId = connection, ProductId = "owner" });
                control.ChatConnectionProducts.Add(new ChatConnectionProductRow { ConnectionId = connection, ProductId = "neighbour" });
            }
            await control.SaveChangesAsync();
            var shared = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor("owner");
            foreach (var table in new[] { "repos", "story_spawns" })
            {
                var ddl = shared.Database.GenerateCreateScript().Split(';').Select(x => x.Trim())
                    .Single(x => x.StartsWith($"CREATE TABLE \"{table}\"", StringComparison.Ordinal));
                await shared.Database.ExecuteSqlRawAsync(ddl.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal));
            }
            shared.Repos.Add(new RepoRow { Id = "repo-owner", ProductId = "owner", DisplayName = "o", Kind = "local", RemoteUrl = "file://o", DefaultBranch = "main" });
            shared.Repos.Add(new RepoRow { Id = "repo-neighbour", ProductId = "neighbour", DisplayName = "n", Kind = "local", RemoteUrl = "file://n", DefaultBranch = "main" });
            await shared.SaveChangesAsync();
        }

        await RunHostAsync(f, "admin", (s, a) => s.PutAsync(a, new(0, true, [new("dest-alpha-one", "topic", false)])));
        using (var scope = f.Services.CreateScope())
        {
            var shared = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor("owner");
            shared.StorySpawns.Add(new StorySpawnRow { Id = "owned-story", StoryId = "owner:STORY", RepoId = "repo-owner", StoryTitle = "s", Phase = "implement", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1) });
            await shared.SaveChangesAsync();
        }

        Assert.Equal(1, await RunHostAsync(f, "admin", (s, _) => s.SweepAsync()));
        using var check = f.Services.CreateScope();
        var plugins = await check.ServiceProvider.GetRequiredService<ControlDbContext>().SpawnPlugins.AsNoTracking()
            .Where(p => p.SpawnId == "owned-story").Select(p => p.ProductId).ToListAsync();
        Assert.Equal(new[] { "owner" }, plugins);
    }

    // ── U2 / U3 · D2, D3: host default ─────────────────────────────────────

    [Fact]
    public async Task U2_U3_Host_Default_Enables_New_And_Unconfigured_Stories_But_Never_Overrides_Operator_Settings()
    {
        await using var f = new ChatPluginTestFactory(); await f.SeedAsync(); await f.ConnectionsAsync();
        var route = new ChatRouteDraft("dest-alpha-one", "topic", false, ConversationScope: ChatConversationScopes.Workstream);

        // U2: host administrators only; revision checked; unknown destinations rejected.
        await Assert.ThrowsAsync<ForgeAccessDeniedException>(() => RunHostAsync(f, "owner-p", (s, a) => s.PutAsync(a, new(0, true, [route]))));
        var saved = await RunHostAsync(f, "admin", (s, a) => s.PutAsync(a, new(0, true, [route])));
        Assert.True(saved.Enabled); Assert.Equal(1, saved.Revision); Assert.NotNull(saved.ActivatedAt);
        Assert.Equal(409, (await Assert.ThrowsAsync<ChatConfigurationException>(() => RunHostAsync(f, "admin", (s, a) => s.PutAsync(a, new(0, true, [route]))))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ChatConfigurationException>(() => RunHostAsync(f, "admin",
            (s, a) => s.PutAsync(a, new(1, true, [route with { DestinationId = "missing" }]))))).Status);

        // The periodic sweep only enables stories created after activation; both "shared" spawns are older.
        Assert.Equal(0, await RunHostAsync(f, "admin", (s, _) => s.SweepAsync()));

        // An operator explicitly turns chat off for p's story.
        await f.RunAsync("owner-p", (s, a) => s.SaveSetupAsync(a, "p", "shared", new(0, false, []), default));

        // U3: "Apply to existing" enables q's unconfigured story and leaves p's explicit Off alone.
        Assert.Equal(1, await RunHostAsync(f, "admin", (s, a) => s.ApplyToExistingAsync(a)));
        var q = await f.RunAsync("owner-q", (s, a) => s.GetPluginAsync(a, "q", "shared"));
        Assert.True(q.Enabled); Assert.Equal(ChatHostDefaultService.Origin, q.CopyOrigin);
        Assert.Equal(ChatConversationScopes.Workstream, Assert.Single(q.Subscriptions).ConversationScope);
        var p = await f.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"));
        Assert.False(p.Enabled); Assert.Null(p.CopyOrigin);
        // Applying again refreshes the story still on the default (q) and still skips p's explicit Off.
        Assert.Equal(1, await RunHostAsync(f, "admin", (s, a) => s.ApplyToExistingAsync(a)));
        Assert.False((await f.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"))).Enabled);

        // A story spawned after activation is picked up by the sweep without opening its panel.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor("q");
            db.StorySpawns.Add(new StorySpawnRow { Id = "fresh", StoryId = "fresh-story", RepoId = "repo-q", StoryTitle = "fresh", Phase = "plan", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1) });
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await RunHostAsync(f, "admin", (s, _) => s.SweepAsync()));
        Assert.True((await f.RunAsync("owner-q", (s, a) => s.GetPluginAsync(a, "q", "fresh"))).Enabled);

        // Once the operator saves the story's own settings, the default no longer owns it.
        var fresh = await f.RunAsync("owner-q", (s, a) => s.SetupAsync(a, "q", "fresh", default));
        await f.RunAsync("owner-q", (s, a) => s.SaveSetupAsync(a, "q", "fresh", new(fresh.Plugin.Revision, true, [new("dest-alpha-one", "topic", false)]), default));
        Assert.Null((await f.RunAsync("owner-q", (s, a) => s.GetPluginAsync(a, "q", "fresh"))).CopyOrigin);
    }

    [Fact]
    public async Task U2_Per_Workstream_Scope_Requires_A_Topic_Or_Thread()
    {
        await using var f = new ChatPluginTestFactory(); await f.SeedAsync(); await f.ConnectionsAsync();
        var flat = new ChatRouteDraft("dest-alpha-one", "flat", false, ConversationScope: ChatConversationScopes.Workstream);
        Assert.Equal("unsupported-conversation-scope", (await Assert.ThrowsAsync<ChatConfigurationException>(() =>
            f.RunAsync("owner-p", (s, a) => s.SaveSetupAsync(a, "p", "shared", new(0, true, [flat]), default)))).Code);
        Assert.Equal("unsupported-conversation-scope", (await Assert.ThrowsAsync<ChatConfigurationException>(() =>
            RunHostAsync(f, "admin", (s, a) => s.PutAsync(a, new(0, true, [flat with { ConversationScope = "bogus", ConversationPolicy = "topic" }]))))).Code);
    }

    // ── U4 / U5 / U6 / U7 · D4–D7: one conversation per workstream ────────

    [Fact]
    public async Task U4_U6_U7_Each_Workstream_Gets_Its_Own_Conversation_Which_Closes_When_It_Finishes()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(conversation: "topic", questions: 0);
        await f.WithAsync(async sp =>
        {
            var control = sp.GetRequiredService<ControlDbContext>();
            // alpha-one is per-workstream; beta keeps the existing per-story behaviour.
            (await control.ChatSubscriptions.SingleAsync(s => s.DestinationId == "dest-alpha-one")).ConversationScope = ChatConversationScopes.Workstream;
            await control.SaveChangesAsync();
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            (await db.Workstreams.SingleAsync(w => w.Id == "child-0")).Name = "Gate runner";
            await db.SaveChangesAsync();
        });

        await Stall(f, "child-0", "s0"); await Stall(f, "child-1", "s1");
        for (var i = 0; i < 4; i++) await f.TickAsync();

        var alpha = await Bindings(f, "dest-alpha-one");
        Assert.Equal(new[] { "child-0", "child-1" }, alpha.Select(b => b.ChildKey).OrderBy(k => k));
        Assert.All(alpha, b => Assert.Equal("ready", b.ProvisioningState));
        Assert.Equal(2, alpha.Select(b => b.ConversationId).Distinct().Count());
        // Topic names identify the story and the workstream (name when known, else id).
        var gate = alpha.Single(b => b.ChildKey == "child-0");
        Assert.Equal("story · Gate runner", f.Alpha.Titles[gate.Id]);
        Assert.Equal("story · child-1", f.Alpha.Titles[alpha.Single(b => b.ChildKey == "child-1").Id]);
        // Each stall landed only in its own workstream's topic (D6: replies there are address-scoped to it).
        var alphaSent = f.Alpha.Sent.Where(s => s.Address.ConnectionId == "alpha-one").ToList();
        Assert.Equal(2, alphaSent.Count);
        Assert.Equal(alpha.Select(b => b.ConversationId).OrderBy(c => c), alphaSent.Select(s => s.Address.ConversationId).OrderBy(c => c));

        // Story-scoped subscriptions are unchanged: one conversation for the whole story.
        var beta = Assert.Single(await Bindings(f, "dest-beta"));
        Assert.Equal("", beta.ChildKey);

        // U4: a second alert for the same workstream reuses its conversation (no new topic).
        var provisioned = f.Alpha.ProvisionCalls;
        await Stall(f, "child-0", "s0-again", revision: 2);
        for (var i = 0; i < 3; i++) await f.TickAsync();
        Assert.Equal(provisioned, f.Alpha.ProvisionCalls);
        Assert.Equal(2, (await Bindings(f, "dest-alpha-one")).Count);

        // U7: child-0 finishes; the reconciler retires only its conversation and the delivery loop closes it.
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            (await db.Workstreams.SingleAsync(w => w.Id == "child-0")).Status = "complete";
            await db.SaveChangesAsync();
            await sp.GetRequiredService<ChatScopeReconciler>().RunOnceAsync();
        });
        await f.TickAsync();
        var after = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConversationBindings.AsNoTracking().ToListAsync());
        var closed = after.Single(b => b.Id == gate.Id);
        Assert.NotNull(closed.RetiredAt); Assert.Equal("closed", closed.RetirementState);
        Assert.Null(after.Single(b => b.ChildKey == "child-1").RetiredAt);
        Assert.Null(after.Single(b => b.Id == beta.Id).RetiredAt);

        // A later alert for the restarted workstream opens a fresh topic.
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
            (await db.Workstreams.SingleAsync(w => w.Id == "child-0")).Status = "running";
            await db.SaveChangesAsync();
        });
        await Stall(f, "child-0", "s0-restarted", revision: 3);
        for (var i = 0; i < 3; i++) await f.TickAsync();
        var reopened = (await Bindings(f, "dest-alpha-one")).Single(b => b.ChildKey == "child-0");
        Assert.NotEqual(gate.Id, reopened.Id);
    }

    [Fact]
    public async Task U5_A_Stuck_Alert_In_One_Workstream_Topic_Does_Not_Hold_Back_Another_Workstream()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(interactive: true, conversation: "topic", questions: 0);
        await f.WithAsync(async sp =>
        {
            var control = sp.GetRequiredService<ControlDbContext>();
            foreach (var sub in await control.ChatSubscriptions.ToListAsync()) sub.ConversationScope = ChatConversationScopes.Workstream;
            await control.SaveChangesAsync();
        });

        // child-0's topic creation has an unknown outcome: its delivery stays "uncertain" and is never retried blindly.
        f.Alpha.UncertainProvision = true;
        await Stall(f, "child-0", "s0");
        for (var i = 0; i < 2; i++) await f.TickAsync();
        f.Alpha.UncertainProvision = false;
        await Stall(f, "child-1", "s1");
        for (var i = 0; i < 3; i++) await f.TickAsync();

        var alpha = await Bindings(f, "dest-alpha-one");
        Assert.Equal("uncertain", alpha.Single(b => b.ChildKey == "child-0").ProvisioningState);
        Assert.Equal("ready", alpha.Single(b => b.ChildKey == "child-1").ProvisioningState);
        var child1 = alpha.Single(b => b.ChildKey == "child-1");
        Assert.Contains(f.Alpha.Sent, s => s.Address.ConnectionId == "alpha-one" && s.Address.ConversationId == child1.ConversationId);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Task Stall(ChatRuntimeTestFactory f, string workstream, string dedupe, long revision = 1)
        => f.ReadAsync(sp => sp.GetRequiredService<ChatEventService>()
            .EnqueueAsync(new("workstream-stalled", dedupe, "p", "shared", WorkstreamId: workstream, Revision: revision)));

    private static Task<List<ChatConversationBindingRow>> Bindings(ChatRuntimeTestFactory f, string destinationId)
        => f.ReadAsync(sp =>
        {
            var control = sp.GetRequiredService<ControlDbContext>();
            return control.ChatConversationBindings.AsNoTracking()
                .Where(b => b.RetiredAt == null && control.ChatSubscriptions.Any(s => s.Id == b.SubscriptionId && s.DestinationId == destinationId))
                .ToListAsync();
        });

    private static async Task<string[]> Grants(ControlDbContext control, string connectionId)
        => (await control.ChatConnectionProducts.AsNoTracking().Where(g => g.ConnectionId == connectionId).Select(g => g.ProductId).ToListAsync())
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();

    private static async Task<T> RunHostAsync<T>(ChatPluginTestFactory f, string user, Func<ChatHostDefaultService, ForgeActor, Task<T>> run)
    {
        using var scope = f.Services.CreateScope();
        var actor = await scope.ServiceProvider.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", user)
            ?? throw new InvalidOperationException("Missing test identity");
        return await run(scope.ServiceProvider.GetRequiredService<ChatHostDefaultService>(), actor);
    }
}