// -----------------------------------------------------------------------
// <copyright file="ChatPluginEngineTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net;
using Common.Chat;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class ChatPluginEngineTests
{
    [Theory]
    [InlineData(null, "legacy", true, true)]
    [InlineData(null, "legacy", false, false)]
    [InlineData("false", "legacy", true, false)]
    [InlineData("true", "legacy", false, false)]
    [InlineData("true", "plugins", true, false)]
    [InlineData("false", "plugins", true, false)]
    [InlineData("true", "unknown", true, false)]
    public void Host_Switch_And_Engine_Gate_Both_Legacy_Pumps_Without_Changing_Opt_In_Defaults(string? enabled, string engine, bool legacy, bool expected)
    {
        using var app = new ForgeWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Forge:Chat:Enabled"] = enabled, ["Forge:Chat:Engine"] = engine,
            ["Forge:ChatBridge:Enabled"] = legacy.ToString(),
        });
        Assert.Equal(expected, app.Services.GetRequiredService<IOptions<ChatBridgeOptions>>().Value.Enabled);
        Assert.Equal(expected, app.Services.GetRequiredService<IOptions<ChatTransportOptions>>().Value.Enabled);
        Assert.False(ChatPluginInteractiveGate.IsAvailable);
    }

    [Fact]
    public async Task Duplicate_Providers_Disable_Registry_While_Forge_Host_And_Health_Still_Boot()
    {
        await using var factory = new ForgeWebApplicationFactory();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IChatProviderPlugin>(new FakeChatProvider("FakeAlpha", true));
            s.AddSingleton<IChatProviderPlugin>(new FakeChatProvider("FakeAlpha", false));
        }));
        using var client = app.AnonymousClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.False(app.Services.GetRequiredService<IChatProviderRegistry>().IsAvailable);
        Assert.Null(app.Services.GetRequiredService<IChatProviderRegistry>().Resolve("FakeAlpha"));
    }
}