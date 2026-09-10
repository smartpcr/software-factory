// -----------------------------------------------------------------------
// <copyright file="ChatPluginTestFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Persistence.Split;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal class ChatPluginTestFactory : ForgeWebApplicationFactory
{
    public ChatPluginTestFactory() : base(new Dictionary<string, string?>
    {
        ["Forge:Db:SplitEnabled"] = "true", ["Forge:Chat:Enabled"] = "true",
        ["Forge:Chat:Engine"] = "plugins", ["Forge:Chat:HostId"] = "test-host",
    }) { }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<IChatProviderPlugin>(new FakeChatProvider("FakeAlpha", topics: true));
            s.AddSingleton<IChatProviderPlugin>(new FakeChatProvider("FakeBeta", topics: false));
        });
    }

    public async Task SeedAsync()
    {
        foreach (var product in new[] { "p", "q" }) await this.SeedProductAsync(product);
        using (var scope = this.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SplitApplyService>().ApplyAsync(CancellationToken.None);
            await new RepoMoveToProductService(scope.ServiceProvider.GetRequiredService<IForgeDbPaths>()).ApplyAsync(CancellationToken.None);
        }
        SplitMarkerCache.ResetForTesting();
        foreach (var product in new[] { "p", "q" })
        {
            using var scope = this.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IProductDbContextFactory>().CreateFor(product);
            db.StorySpawns.Add(new StorySpawnRow { Id = "shared", StoryId = "story", RepoId = "repo-" + product, StoryTitle = product, Phase = "implement" });
            for (var i = 0; i < 32; i++) db.Workstreams.Add(new WorkstreamRow { Id = "child-" + i, RepoId = "repo-" + product, StorySpawnId = "shared", Status = "running" });
            await db.SaveChangesAsync();
        }
        await this.EnrollAsync("admin", true);
        await this.EnrollAsync("owner-p", productId: "p");
        await this.EnrollAsync("owner-q", productId: "q");
        await this.EnrollAsync("reader-p", productId: "p", role: "human");
        foreach (var id in new[] { "admin", "owner-p", "owner-q", "reader-p" }) this.Link(id, sender: id);
    }

    public async Task<T> RunAsync<T>(string user, Func<ChatConfigurationService, ForgeActor, Task<T>> run)
    {
        using var scope = this.Services.CreateScope();
        var actor = await scope.ServiceProvider.GetRequiredService<ForgeActorResolver>().FromProviderAsync("telegram", "123", user)
            ?? throw new InvalidOperationException("Missing test identity");
        return await run(scope.ServiceProvider.GetRequiredService<ChatConfigurationService>(), actor);
    }

    public async Task ConnectionsAsync()
    {
        foreach (var (id, provider) in new[] { ("alpha-one", "FakeAlpha"), ("alpha-two", "FakeAlpha"), ("beta", "FakeBeta") })
        {
            await this.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, id, Connection(provider, id)));
            await this.RunAsync("admin", (s, a) => s.PutDestinationAsync(a, id, "dest-" + id, new(0, "tenant", "channel", id, "group")));
        }
    }

    public static JsonElement EmptySettings => JsonSerializer.SerializeToElement(new { });
    public static ChatConnectionWrite Connection(string provider, string installation, long revision = 0, params string[] products)
        => new(revision, provider, installation, installation, true, 1, EmptySettings,
            new Dictionary<string, ChatSecretReference>(), products.Length == 0 ? new[] { "p", "q" } : products);
    public static ChatPluginWrite Plugin(long revision = 0, bool enabled = true, int version = 1)
        => new(revision, enabled, version, EmptySettings);
}