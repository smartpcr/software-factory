// -----------------------------------------------------------------------
// <copyright file="PairingCodeDeliveryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class PairingCodeDeliveryTests
{
    private const long ChatId = -1004373092754;
    private const string RemoteUi = "http://xd003:5181";

    private sealed class Recorder : IChatTransport
    {
        public List<(string ChatId, string Text)> Sent { get; } = [];
        public bool Fail { get; set; }
        public string Platform => "telegram";
        public Task<ChatSendResult> SendAsync(string chatId, string text, IReadOnlyList<ChatButton>? buttons, CancellationToken ct, string? threadId = null)
        {
            this.Sent.Add((chatId, text));
            return Task.FromResult(this.Fail ? ChatSendResult.Failure("boom") : ChatSendResult.Success("1"));
        }

        public Task<ChatSendResult> EditAsync(string chatId, string messageId, string text, IReadOnlyList<ChatButton>? buttons, CancellationToken ct)
            => Task.FromResult(ChatSendResult.Success(messageId));

        public Task<string?> CreateTopicAsync(string chatId, string name, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<ChatInbound>> PollUpdatesAsync(long offset, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ChatInbound>>([]);
    }

    private static WebApplicationFactory<Program> Bridge(ForgeWebApplicationFactory factory, Recorder chat, bool enabled = true)
        => factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.Configure<OperatorAuthenticationOptions>(o => o.RemoteBrowserOrigins = [RemoteUi]);
            s.Configure<ChatBridgeOptions>(o =>
            {
                o.Enabled = enabled;
                o.Telegram.AllowedChatIds = [ChatId];
            });
            s.AddSingleton<IChatTransport>(chat);
        }));

    private static HttpClient Remote(WebApplicationFactory<Program> app)
    {
        var client = app.AnonymousClient();
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", RemoteUi);
        return client;
    }

    [Fact]
    public async Task Code_Goes_To_Chat_And_Never_To_The_Requester()
    {
        var chat = new Recorder();
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = Bridge(factory, chat);
        await app.EnrollAsync("existing");
        using var client = Remote(app);
        await OperatorTestIdentity.RefreshCsrfAsync(client);

        using var response = await client.PostAsync("/api/operator/request-pairing-code", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);

        var body = await response.Content.ReadAsStringAsync();
        var sent = Assert.Single(chat.Sent);
        Assert.Equal(ChatId.ToString(), sent.ChatId);

        // The code exists in chat only. Nothing in the HTTP response can be redeemed.
        var code = sent.Text.Split("Forge pairing code: ")[1][..64];
        Assert.DoesNotContain(code, body, StringComparison.Ordinal);
        Assert.Contains(RemoteUi, sent.Text, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/operator/pair", new { challenge = code })).StatusCode);
        Assert.Equal("Paired", (await client.GetFromJsonAsync<JsonElement>("/api/operator/status")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_Flood_Cannot_Starve_The_Operator_Of_A_Usable_Code()
    {
        var chat = new Recorder();
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = Bridge(factory, chat);
        await app.EnrollAsync("existing");
        using var client = Remote(app);
        await OperatorTestIdentity.RefreshCsrfAsync(client);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/operator/request-pairing-code", null)).StatusCode);
        using var second = await client.PostAsync("/api/operator/request-pairing-code", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);

        // The first code stays valid, so the operator can still use what they received.
        var code = chat.Sent.Single().Text.Split("Forge pairing code: ")[1][..64];
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/operator/pair", new { challenge = code })).StatusCode);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("no-enrollment")]
    [InlineData("transport-down")]
    [InlineData("no-csrf")]
    [InlineData("untrusted-origin")]
    public async Task Delivery_Is_Refused_When_It_Cannot_Be_Trusted_Or_Completed(string scenario)
    {
        var chat = new Recorder { Fail = scenario == "transport-down" };
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = Bridge(factory, chat, enabled: scenario != "disabled");
        if (scenario != "no-enrollment") await app.EnrollAsync("existing");
        using var client = Remote(app);
        if (scenario != "no-csrf") await OperatorTestIdentity.RefreshCsrfAsync(client);
        if (scenario == "untrusted-origin")
        {
            client.DefaultRequestHeaders.Remove("Origin");
            client.DefaultRequestHeaders.Add("Origin", "http://attacker:5181");
        }

        using var response = await client.PostAsync("/api/operator/request-pairing-code", null);
        Assert.NotEqual(HttpStatusCode.Accepted, response.StatusCode);
        if (scenario is "disabled" or "no-enrollment") Assert.Empty(chat.Sent);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", RemoteUi);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/products")).StatusCode);
    }
}
