// -----------------------------------------------------------------------
// <copyright file="SlackProviderPlugin.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat.Slack;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class SlackProviderPlugin(IHttpClientFactory clients, ISlackWebSocketFactory sockets, TimeProvider clock) : IChatProviderPlugin
{
    public const string ClientName = "chat-slack-provider";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, SlackSocketReceiver> apps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SlackRequestBudget> budgets = new(StringComparer.Ordinal);
    public ChatProviderDescriptor Descriptor { get; } = new("slack", "Slack", 1, [1],
        new(ChatReceptionMode.Socket, true, true, true, false, true, true, ChatConversationRetirement.LocalOnly, 3000, 32768, 2000),
        [new("bot-token", "Workspace bot token", true), new("app-token", "Socket Mode app token", true)]);

    public ChatSettingsValidation ValidateSettings(int version, JsonElement settings)
    {
        if (version != 1 || settings.ValueKind != JsonValueKind.Object || !SlackApiClient.Id(SlackApiClient.String(settings, "appId")))
            return new(false, "unsupported-slack-settings");
        foreach (var field in settings.EnumerateObject())
            if (field.Name != "appId" && (field.Name != "includePrivateChannels" || field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
                return new(false, "unsupported-slack-settings");
        return ChatSettingsValidation.Success;
    }

    public async ValueTask<ChatProviderResult<IChatConnectionSession>> OpenSessionAsync(ChatConnectionConfiguration connection, IChatSecretStore secrets, CancellationToken ct = default)
    {
        if (connection.ProviderId != "slack" || !this.ValidateSettings(connection.SettingsVersion, connection.Settings).Valid
            || !SlackApiClient.Id(connection.InstallationId)) return Fail("invalid-slack-connection");
        if (!connection.Secrets.TryGetValue("bot-token", out var botReference)) return Fail("slack-bot-token-required");
        if (!connection.Secrets.TryGetValue("app-token", out var appReference)) return Fail("slack-app-token-required");
        string? botToken; string? appToken;
        try { botToken = await secrets.ReadAsync(botReference, ct); appToken = await secrets.ReadAsync(appReference, ct); }
        catch (Exception ex) when (ex is not OutOfMemoryException && !ct.IsCancellationRequested) { return Fail("slack-secret-unavailable"); }
        if (!Token(botToken, "xoxb-")) return Fail("slack-bot-token-required");
        if (!Token(appToken, "xapp-")) return Fail("slack-app-token-required");
        var appId = SlackApiClient.String(connection.Settings, "appId")!;
        var api = new SlackApiClient(clients, botToken!, this.budgets.GetOrAdd("workspace:" + appId + ":" + connection.InstallationId, _ => new(clock)));
        var identity = await api.CallAsync("auth.test", new { }, ct);
        if (!identity.Succeeded) return new(null, identity.Failure);
        if (SlackApiClient.True(identity.Value, "is_enterprise_install")) return Fail("slack-enterprise-installation-unsupported");
        if (SlackApiClient.String(identity.Value, "team_id") != connection.InstallationId) return Fail("slack-workspace-mismatch");
        var user = SlackApiClient.String(identity.Value, "user_id"); var bot = SlackApiClient.String(identity.Value, "bot_id");
        if (!SlackApiClient.Id(user) || !SlackApiClient.Id(bot)) return Fail("slack-bot-installation-required");
        if (SlackApiClient.String(identity.Value, "app_id") is { } actualApp && actualApp != appId) return Fail("slack-app-installation-mismatch");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appToken!)));
        await this.gate.WaitAsync(ct);
        try
        {
            if (!this.apps.TryGetValue(appId, out var receiver))
            {
                receiver = new(appId, fingerprint, new(clients, appToken!, this.budgets.GetOrAdd("app:" + appId, _ => new(clock)), true), sockets, clock);
                var opened = await receiver.OpenAsync(ct);
                if (opened is not null) { await receiver.DisposeAsync(); return new(null, opened); }
                this.apps.Add(appId, receiver);
            }
            else if (receiver.TokenFingerprint != fingerprint) return Fail("slack-app-token-conflict");
            var session = new SlackConnectionSession(connection, user!, bot!, api, receiver, () => this.DetachAsync(appId, connection.InstallationId));
            if (!receiver.Members.TryAdd(connection.InstallationId, session)) return Fail("duplicate-slack-workspace");
            return ChatProviderResult<IChatConnectionSession>.Success(session);
        }
        finally { this.gate.Release(); }
    }
    private async ValueTask DetachAsync(string appId, string workspace)
    {
        await this.gate.WaitAsync();
        try
        {
            if (!this.apps.TryGetValue(appId, out var receiver)) return;
            receiver.Members.TryRemove(workspace, out _);
            if (receiver.Members.IsEmpty) { await receiver.DisposeAsync(); this.apps.Remove(appId); }
        }
        finally { this.gate.Release(); }
    }
    private static bool Token(string? value, string prefix) => value is { Length: > 10 and <= 512 } && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    private static ChatProviderResult<IChatConnectionSession> Fail(string code) => ChatProviderResult<IChatConnectionSession>.Fail(ChatFailureKind.InvalidConfiguration, code);
}