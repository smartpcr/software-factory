// -----------------------------------------------------------------------
// <copyright file="TelegramProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat.Telegram;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed class TelegramProvider(IHttpClientFactory clients) : IChatProviderPlugin
{
    public const string ClientName = "chat-telegram-provider";
    private readonly ConcurrentDictionary<string, TelegramSession> sessions = new(StringComparer.Ordinal);
    public ChatProviderDescriptor Descriptor { get; } = new("telegram", "Telegram", 1, [1],
        new(ChatReceptionMode.LongPolling, true, true, true, true, false, true, ChatConversationRetirement.RemoteClose, 4096, 32768, 64),
        [new("bot-token", "Bot token", true)]);

    public ChatSettingsValidation ValidateSettings(int version, JsonElement settings)
    {
        if (version != 1 || settings.ValueKind != JsonValueKind.Object) return new(false, "unsupported-telegram-settings");
        foreach (var field in settings.EnumerateObject())
            if (field.Name != "longPollSeconds" || field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var value) || value is < 1 or > 25) return new(false, "unsupported-telegram-settings");
        return ChatSettingsValidation.Success;
    }

    public async ValueTask<ChatProviderResult<IChatConnectionSession>> OpenSessionAsync(ChatConnectionConfiguration connection, IChatSecretStore secrets, CancellationToken ct = default)
    {
        if (connection.ProviderId != "telegram" || !this.ValidateSettings(connection.SettingsVersion, connection.Settings).Valid
            || !connection.Secrets.TryGetValue("bot-token", out var reference)) return Fail("invalid-telegram-connection");
        string? token;
        try { token = await secrets.ReadAsync(reference, ct); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Fail("telegram-secret-unavailable"); }
        if (token is null || token.Length > 256 || !Regex.IsMatch(token, @"\A[0-9]+:[A-Za-z0-9_-]{20,}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return Fail("telegram-token-required");
        var api = new TelegramBotApi(clients, token);
        var identity = await api.CallAsync("getMe", new { }, ct);
        if (!identity.Succeeded) return new(null, identity.Failure);
        if (!TelegramBotApi.True(identity.Value, "is_bot") || TelegramBotApi.String(identity.Value, "id") != connection.InstallationId
            || TelegramBotApi.String(identity.Value, "username") is not { Length: >= 5 and <= 32 } username
            || !username.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return Fail("telegram-identity-mismatch");
        var webhook = await api.CallAsync("getWebhookInfo", new { }, ct);
        if (!webhook.Succeeded) return new(null, webhook.Failure);
        if (TelegramBotApi.String(webhook.Value, "url") is not "") return Fail("telegram-webhook-conflict");
        var pollSeconds = connection.Settings.TryGetProperty("longPollSeconds", out var poll) ? poll.GetInt32() : 25;
        var session = new TelegramSession(connection, api, token, username, pollSeconds,
            () => this.sessions.TryRemove(connection.InstallationId, out _));
        if (!this.sessions.TryAdd(connection.InstallationId, session)) return Fail("duplicate-telegram-bot");
        return ChatProviderResult<IChatConnectionSession>.Success(session);
    }

    private static ChatProviderResult<IChatConnectionSession> Fail(string code) => ChatProviderResult<IChatConnectionSession>.Fail(ChatFailureKind.InvalidConfiguration, code);
}