// -----------------------------------------------------------------------
// <copyright file="ChatBridgeEndpointsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net;
using System.Net.Http.Json;
using Common.Chat;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// Phase 95 stage 1.4 — the admin <c>POST /api/chat/test-send</c> smoke endpoint:
/// role gate, disabled guard, and a fake-transport round-trip.
/// </summary>
public class ChatBridgeEndpointsTests
{
    private static WebApplicationFactory<Program> WithFakeTransport(ForgeWebApplicationFactory factory, FakeChatTransport fake) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChatTransport>();
            services.AddSingleton<IChatTransport>(fake);
        }));

    [Fact]
    public async Task TestSend_As_HostAdmin_Sends_Via_Transport()
    {
        var fake = new FakeChatTransport();
        await using var factory = new ForgeWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Forge:ChatBridge:Enabled"] = "true",
            ["Forge:ChatBridge:Telegram:AllowedChatIds:0"] = "4242",
        });

        var app = WithFakeTransport(factory, fake);
        await app.EnrollAsync("test-operator", admin: true);
        using var client = await app.PairedClientAsync("test-operator");

        var resp = await client.PostAsJsonAsync("/api/chat/test-send", new { text = "ping" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Single(fake.Sent);
        Assert.Equal("4242", fake.Sent[0].ChatId);
        Assert.Equal("ping", fake.Sent[0].Text);
    }

    [Fact]
    public async Task TestSend_When_Disabled_Returns_Conflict()
    {
        // Default factory: appsettings has Forge:ChatBridge:Enabled=false.
        await using var factory = new ForgeWebApplicationFactory();
        await factory.EnrollAsync("test-operator", admin: true);
        using var client = await factory.PairedClientAsync("test-operator");

        var resp = await client.PostAsJsonAsync("/api/chat/test-send", new { chatId = "1", text = "ping" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("disabled", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestSend_As_NonPO_Returns_Forbidden()
    {
        await using var factory = new ForgeWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Forge:ChatBridge:Enabled"] = "true",
        });

        // Seed a non-PO user so the role gate resolves a concrete "eng" role
        // (rather than the single-user bootstrap pass-through).
        using (var scope = factory.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            control.Users.Add(new UserRow
            {
                Id = "u-eng",
                Email = "eng@example.com",
                Role = "eng",
            });
            await control.SaveChangesAsync();
        }

        await factory.EnrollAsync("u-eng");
        using var client = await factory.PairedClientAsync("u-eng");

        var resp = await client.PostAsJsonAsync("/api/chat/test-send", new { chatId = "1", text = "ping" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
