// -----------------------------------------------------------------------
// <copyright file="TelegramSliceLiveExercise.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net.Http.Json;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>Explicit opt-in only. Never turns an unavailable live credential into a passing mock test.</summary>
public sealed class TelegramLiveFactAttribute : FactAttribute
{
    public TelegramLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FORGE_CHAT_LIVE_EXERCISE_PROFILE")))
            this.Skip = "Requires an explicitly supplied new-test-bot profile; see docs/chat/chat-plugin-stage1s-validation.md.";
    }
}

public sealed class TelegramSliceLiveExercise
{
    private sealed record Profile(bool NewTestBotConfirmed, bool IsolatedDestinationConfirmed, string BotId, string ChannelId,
        string TokenEnvironmentVariable, string RecordPath);

    [TelegramLiveFact]
    public async Task New_Bot_Introduction_And_Metadata_Alert_Record_Real_Receipts()
    {
        var profile = JsonSerializer.Deserialize<Profile>(await File.ReadAllTextAsync(
            Environment.GetEnvironmentVariable("FORGE_CHAT_LIVE_EXERCISE_PROFILE")!), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(profile.NewTestBotConfirmed && profile.IsolatedDestinationConfirmed, "Explicit new-bot and isolated-destination confirmations are required.");
        Assert.StartsWith("FORGE_CHAT_TEST_", profile.TokenEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(profile.TokenEnvironmentVariable)), "The referenced test credential is unavailable.");
        Assert.True(Path.IsPathFullyQualified(profile.RecordPath), "RecordPath must be an explicit absolute output path.");
        Directory.CreateDirectory(Path.GetDirectoryName(profile.RecordPath)!);
        // Reserve the record BEFORE sending. A rerun cannot overwrite evidence or silently repeat a completed exercise.
        await using var output = new FileStream(profile.RecordPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(output);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { status = "started", utc = DateTimeOffset.UtcNow, provider = "telegram", mode = "live-http" }));
        await writer.FlushAsync();

        await using var app = new LiveFactory(); await app.SeedAsync();
        await app.RunAsync("admin", (s, a) => s.PutConnectionAsync(a, "live-test-bot", new(0, "telegram", profile.BotId, "New Telegram test bot", true,
            1, ChatPluginTestFactory.EmptySettings, new Dictionary<string, ChatSecretReference> { ["bot-token"] = new("environment", profile.TokenEnvironmentVariable) }, ["p"])));
        await app.RunAsync("admin", (s, a) => s.PutDestinationAsync(a, "live-test-bot", "live-test-channel", new(0, profile.BotId, profile.ChannelId, "Isolated test destination", "group")));
        using var client = await app.PairedClientAsync("owner-p");
        const string path = "/api/products/p/story-spawns/shared/plugins/chat/notification-setup";
        Assert.Equal("off", (await client.GetFromJsonAsync<ChatNotificationSetupView>(path))!.State);
        using var save = await client.PutAsJsonAsync(path, new ChatNotificationSetupWrite(0, true, "live-test-channel"));
        Assert.True(save.IsSuccessStatusCode, "The test bot/destination could not be verified. No introduction was sent.");
        using (var scope = app.Services.CreateScope())
        {
            var slice = scope.ServiceProvider.GetRequiredService<ChatTelegramSliceService>();
            Assert.Equal(1, await slice.RunOutboundOnceAsync(CancellationToken.None));
            Assert.True(await slice.EnqueueAsync(new("p", "repo-p", "child-0", 1, "Excluded title", "Excluded question text", null, "shared"), CancellationToken.None));
            Assert.Equal(1, await slice.RunOutboundOnceAsync(CancellationToken.None));
        }
        using var refreshed = await app.PairedClientAsync("owner-p");
        Assert.Equal("ready", (await refreshed.GetFromJsonAsync<ChatNotificationSetupView>(path))!.State);
        using var other = await app.PairedClientAsync("owner-q");
        Assert.Equal("off", (await other.GetFromJsonAsync<ChatNotificationSetupView>(path.Replace("/products/p/", "/products/q/", StringComparison.Ordinal)))!.State);
        using (var scope = app.Services.CreateScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<ControlDbContext>().ChatNotifications.AsNoTracking().ToListAsync();
            Assert.Equal(2, rows.Count); Assert.All(rows, n => Assert.Equal("sent", n.Status));
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = "passed", utc = DateTimeOffset.UtcNow, provider = "telegram", mode = "live-http",
                botId = profile.BotId, channelId = profile.ChannelId, selectedProduct = "p", selectedSpawn = "shared", otherProductState = "off",
                receipts = rows.Select(n => new { n.Kind, n.ChatMessageId, n.SentAt, n.Status }),
                uiExercise = "Record the browser/manual row exercise separately; this record proves the real API/outbox/provider path.",
            }));
        }
    }

    private sealed class LiveFactory : ChatPluginTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddOptions<ChatPluginOptions>().PostConfigure<IForgeDbPaths>((options, paths) =>
            {
                options.TelegramSlice.Enabled = true; options.TelegramSlice.NewTestBotConfirmed = true;
                options.TelegramSlice.IsolatedProfileRoot = paths.Folder;
            }));
        }
    }
}