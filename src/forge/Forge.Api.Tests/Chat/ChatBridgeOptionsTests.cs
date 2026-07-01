// -----------------------------------------------------------------------
// <copyright file="ChatBridgeOptionsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using Forge.Api.Services.Chat;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>
/// Phase 95 stage 1.1 — options binding + token resolution.
/// </summary>
public class ChatBridgeOptionsTests
{
    [Fact]
    public void Binds_From_Config_Section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forge:ChatBridge:Enabled"] = "true",
                ["Forge:ChatBridge:Platform"] = "telegram",
                ["Forge:ChatBridge:MaxAttempts"] = "7",
                ["Forge:ChatBridge:OutboundTickInterval"] = "00:00:03",
                ["Forge:ChatBridge:Telegram:BotToken"] = "json-token",
                ["Forge:ChatBridge:Telegram:LongPollTimeoutSec"] = "30",
                ["Forge:ChatBridge:Telegram:AllowedChatIds:0"] = "111",
                ["Forge:ChatBridge:Telegram:AllowedChatIds:1"] = "222",
            })
            .Build();

        var opt = new ChatBridgeOptions();
        config.GetSection(ChatBridgeOptions.SectionName).Bind(opt);

        Assert.True(opt.Enabled);
        Assert.Equal("telegram", opt.Platform);
        Assert.Equal(7, opt.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(3), opt.OutboundTickInterval);
        Assert.Equal("json-token", opt.Telegram.BotToken);
        Assert.Equal(30, opt.Telegram.LongPollTimeoutSec);
        Assert.Equal(new List<long> { 111, 222 }, opt.Telegram.AllowedChatIds);
    }

    [Fact]
    public void Missing_Section_Yields_Safe_Defaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var opt = new ChatBridgeOptions();
        config.GetSection(ChatBridgeOptions.SectionName).Bind(opt);

        Assert.False(opt.Enabled);
        Assert.Equal("telegram", opt.Platform);
        Assert.Equal(5, opt.MaxAttempts);
        Assert.Empty(opt.Telegram.AllowedChatIds);
        Assert.Equal(string.Empty, opt.Telegram.BotToken);
    }

    [Fact]
    public void Env_Var_Overrides_AppSettings_For_BotToken()
    {
        const string EnvKey = "Forge__ChatBridge__Telegram__BotToken";
        Environment.SetEnvironmentVariable(EnvKey, "env-token");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Simulates appsettings.json — must LOSE to the env var.
                    ["Forge:ChatBridge:Telegram:BotToken"] = "json-token",
                })
                .AddEnvironmentVariables()
                .Build();

            var opt = new ChatBridgeOptions();
            config.GetSection(ChatBridgeOptions.SectionName).Bind(opt);

            Assert.Equal("env-token", opt.Telegram.BotToken);
            Assert.Equal("env-token", ChatTokenResolver.Resolve(opt));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKey, null);
        }
    }

    [Fact]
    public void Resolve_Returns_Configured_Token_Trimmed()
    {
        var opt = new ChatBridgeOptions();
        opt.Telegram.BotToken = "  padded-token  ";
        Assert.Equal("padded-token", ChatTokenResolver.Resolve(opt));
    }

    [Fact]
    public void Blank_Token_Resolves_Null_When_No_Blob()
    {
        var opt = new ChatBridgeOptions();
        opt.Telegram.BotToken = "   ";

        // The DPAPI fallback only applies when the blob exists. On a machine
        // without the blob (the normal test host), a blank token resolves to
        // null — the "bridge stays idle" signal, not a crash.
        if (!File.Exists(ChatTokenResolver.BlobPath()))
        {
            Assert.Null(ChatTokenResolver.Resolve(opt));
        }
    }
}
