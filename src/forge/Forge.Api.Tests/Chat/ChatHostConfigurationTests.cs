// -----------------------------------------------------------------------
// <copyright file="ChatHostConfigurationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Services.Chat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

/// <summary>Isolated host-user configuration, setup persistence, and launcher-context restarts.</summary>
public class ChatHostConfigurationTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"forge-chat-config-{Guid.NewGuid():N}");
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "ChatFixtures");

    public ChatHostConfigurationTests() => Directory.CreateDirectory(this.folder);

    public void Dispose() => Directory.Delete(this.folder, recursive: true);

    private string WriteSettings(string name, string json)
    {
        var path = Path.Combine(this.folder, name);
        File.WriteAllText(path, json);
        return path;
    }

    private static (ChatBridgeOptions Bridge, ChatTransportOptions Transport) Bind(IConfiguration configuration)
    {
        var section = configuration.GetSection(ChatBridgeOptions.SectionName);
        return (section.Get<ChatBridgeOptions>() ?? new(), section.Get<ChatTransportOptions>() ?? new());
    }

    [Fact]
    public async Task Clean_Checkout_With_Preexisting_Dpapi_Blob_Leaves_Both_Pumps_Idle()
    {
        var blob = Path.Combine(this.folder, "chat-token.bin");
        File.WriteAllBytes(blob, ProtectedData.Protect(Encoding.UTF8.GetBytes("fixture-token"),
            null, DataProtectionScope.CurrentUser));
        using var config = new ConfigurationManager();
        config.AddJsonFile(Path.Combine(Fixtures, "appsettings.json"))
            .AddJsonFile(Path.Combine(Fixtures, "appsettings.Development.json"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Forge:ChatBridge:TokenBlobPath"] = blob });
        var (bridge, transport) = Bind(config);
        Assert.False(bridge.Enabled);
        Assert.False(bridge.AllowRemoteActions);
        Assert.False(transport.Enabled);
        Assert.Empty(transport.Telegram.BotToken);
        Assert.Empty(transport.Telegram.AllowedChatIds);
        Assert.Empty(transport.Telegram.AllowedUserIds);
        Assert.Equal(0, transport.Telegram.SupergroupChatId);
        Assert.Equal("fixture-token", ChatTokenResolver.Resolve(transport));

        bridge.OutboundTickInterval = TimeSpan.FromMilliseconds(10);
        var scopes = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var chat = new Mock<IChatTransport>(MockBehavior.Strict);
        using var outbound = new ChatBridgeService(scopes.Object, chat.Object,
            new StaticOptionsMonitor<ChatBridgeOptions>(bridge), new StaticOptionsMonitor<ChatTransportOptions>(transport),
            NullLogger<ChatBridgeService>.Instance);
        using var inbound = new ChatInboundPump(scopes.Object, chat.Object,
            new StaticOptionsMonitor<ChatTransportOptions>(transport), NullLogger<ChatInboundPump>.Instance);
        await outbound.StartAsync(default);
        await inbound.StartAsync(default);
        try { await Task.Delay(100); }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await outbound.StopAsync(timeout.Token);
            await inbound.StopAsync(timeout.Token);
        }

        scopes.VerifyNoOtherCalls();
        chat.VerifyNoOtherCalls();
    }

    [Fact]
    public void Missing_Host_File_Does_Not_Enable_Chat()
    {
        using var configuration = new ConfigurationManager();
        ChatBridgeConfiguration.AddHostConfiguration(configuration, Path.Combine(this.folder, "absent.json"));
        var (bridge, transport) = Bind(configuration);
        Assert.False(bridge.Enabled);
        Assert.False(bridge.AllowRemoteActions);
        Assert.False(transport.Enabled);
    }

    [Fact]
    public void Host_File_Overrides_Both_Json_Files_Then_Environment_And_Command_Line_Win()
    {
        var basePath = this.WriteSettings("base.json", """{"Forge":{"ChatBridge":{"Enabled":false,"MaxAttempts":1}}}""");
        var developmentPath = this.WriteSettings("dev.json", """{"Forge":{"ChatBridge":{"Enabled":false,"MaxAttempts":2}}}""");
        var localPath = this.WriteSettings("host.json", """{"Forge":{"ChatBridge":{"Enabled":true,"MaxAttempts":3}}}""");
        var prefix = $"CHAT_TEST_{Guid.NewGuid():N}_";
        var key = prefix + "Forge__ChatBridge__MaxAttempts";
        Environment.SetEnvironmentVariable(key, "4");
        try
        {
            using var config = new ConfigurationManager();
            config.AddJsonFile(basePath).AddJsonFile(developmentPath).AddEnvironmentVariables(prefix);
            ChatBridgeConfiguration.AddHostConfiguration(config, localPath);
            Assert.True(Bind(config).Bridge.Enabled);
            Assert.Equal(4, Bind(config).Bridge.MaxAttempts);

            config.AddCommandLine(new[] { "--Forge:ChatBridge:MaxAttempts", "5", "--Forge:ChatBridge:Enabled", "false" });
            Assert.Equal(5, Bind(config).Bridge.MaxAttempts);
            Assert.False(Bind(config).Bridge.Enabled);
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    [Fact]
    public void Invalid_Or_Relative_Host_Configuration_Fails_Instead_Of_Falling_Back()
    {
        using var config = new ConfigurationManager();
        Assert.Throws<ArgumentException>(() => ChatBridgeConfiguration.AddHostConfiguration(config, "relative.json"));
        var invalid = this.WriteSettings("invalid.json", "invalid json");
        Assert.Throws<InvalidDataException>(() => ChatBridgeConfiguration.AddHostConfiguration(config, invalid));
    }

    [Fact]
    public async Task Setup_OptIn_Survives_Two_Host_Starts_In_Normal_Launcher_Context()
    {
        var localPath = this.WriteSettings("host.json", """
            {"Forge":{"ChatBridge":{"Enabled":false,"MaxAttempts":9,"Telegram":{"BotToken":"stale-fixture-token"}}}}
            """);
        await this.RunPowerShellAsync("""
            . (Join-Path $env:CHAT_FIXTURES 'forge-chat-settings.ps1')
            if ((Get-ForgeChatSettingsPath) -ne $env:FORGE_CHAT_SETTINGS_PATH) { throw 'Settings path mismatch' }
            Save-ForgeTelegramSettings -ChatId -100123 -UserId 123 -ApiBaseUrl 'https://chat.invalid'
            """, localPath);

        // The helper saved only non-secret settings and retained unrelated outbox knobs.
        using var saved = JsonDocument.Parse(File.ReadAllText(localPath));
        Assert.Equal("", saved.RootElement.GetProperty("Forge").GetProperty("ChatBridge").GetProperty("Telegram").GetProperty("BotToken").GetString());

        for (var start = 0; start < 2; start++)
        {
            // Same startup context as run-forge-ui: Development + repo-root cwd,
            // which contains neither project appsettings file. No DB or live pumps.
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = new[] { "--environment", "Development" },
                ContentRootPath = this.folder,
            });
            // Keep the fixture independent of the operator's process environment.
            foreach (var source in builder.Configuration.Sources.OfType<EnvironmentVariablesConfigurationSource>().ToArray())
                builder.Configuration.Sources.Remove(source);
            ChatBridgeConfiguration.AddHostConfiguration(builder.Configuration, localPath);
            builder.WebHost.UseTestServer();
            builder.Services.Configure<ChatBridgeOptions>(builder.Configuration.GetSection(ChatBridgeOptions.SectionName));
            builder.Services.Configure<ChatTransportOptions>(builder.Configuration.GetSection(ChatBridgeOptions.SectionName));
            await using var app = builder.Build();
            await app.StartAsync();
            var bridge = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ChatBridgeOptions>>().Value;
            var transport = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ChatTransportOptions>>().Value;
            Assert.True(bridge.Enabled);
            Assert.True(transport.Enabled);
            Assert.False(bridge.AllowRemoteActions);
            Assert.Equal(9, bridge.MaxAttempts);
            Assert.Equal(new long[] { -100123 }, transport.Telegram.AllowedChatIds);
            Assert.Equal(-100123, transport.Telegram.SupergroupChatId);
            Assert.Equal(new long[] { 123 }, transport.Telegram.AllowedUserIds);
            Assert.Equal("https://chat.invalid", transport.Telegram.ApiBaseUrl);
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Scripts_Parse_And_Launcher_Preserves_Explicit_Chat_Environment()
    {
        await this.RunPowerShellAsync("""
            foreach ($name in @('forge-chat-settings.ps1', 'setup-telegram-bridge.ps1', 'run-forge-ui.ps1')) {
                $parseErrors = $null; $tokens = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    (Join-Path $env:CHAT_FIXTURES $name), [ref]$tokens, [ref]$parseErrors)
                if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
                if ($name -eq 'run-forge-ui.ps1') {
                    # Execute the actual apphost launch statement with a recording
                    # Start-Process stub. No production stop/start/build code runs.
                    $launch = @($ast.FindAll({ param($node)
                        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                        $node.Left.Extent.Text -eq '$serverProc'
                    }, $true))
                    if ($launch.Count -ne 1) { throw 'Expected one server launch statement' }
                    function Start-Process {
                        param($FilePath, $ArgumentList, $WorkingDirectory, $RedirectStandardOutput,
                              $RedirectStandardError, $WindowStyle, [switch]$PassThru)
                        if ($WorkingDirectory -ne $env:CHAT_TEST_ROOT -or
                            ($ArgumentList -join ' ') -ne '--environment Development' -or
                            $env:Forge__ChatBridge__Enabled -ne 'false' -or
                            $env:FORGE_CHAT_SETTINGS_PATH -ne (Join-Path $env:CHAT_TEST_ROOT 'unused.json')) {
                            throw 'Launcher configuration handoff changed'
                        }
                        return [pscustomobject]@{ Id = 123 }
                    }
                    $serverExe = 'fixture.exe'; $RepoRoot = $env:CHAT_TEST_ROOT
                    $serverLog = 'fixture.log'; $serverErr = 'fixture.err'
                    $env:Forge__ChatBridge__Enabled = 'false'
                    Invoke-Expression $launch[0].Extent.Text
                    if ($serverProc.Id -ne 123) { throw 'Launch statement not exercised' }
                    if ($ast.Extent.Text -match 'Export-ForgeConfigTree') { throw 'Stale chat environment exporter returned' }
                }
            }
            """, Path.Combine(this.folder, "unused.json"));
    }

    private async Task RunPowerShellAsync(string script, string settingsPath)
    {
        var scriptPath = this.WriteSettings($"fixture-{Guid.NewGuid():N}.ps1", "$ErrorActionPreference = 'Stop'\n" + script);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(scriptPath);
        start.Environment["CHAT_FIXTURES"] = Fixtures;
        start.Environment["CHAT_TEST_ROOT"] = this.folder;
        start.Environment["FORGE_CHAT_SETTINGS_PATH"] = settingsPath;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, (await output) + (await error));
    }
}
