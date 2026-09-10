// -----------------------------------------------------------------------
// <copyright file="TelegramNotificationProvider.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Common.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>Stage 1S adapter. Flat, plain-text notifications only; no receiver or legacy credentials.</summary>
public sealed class TelegramNotificationProvider(IHttpClientFactory clients) : IChatProviderPlugin
{
    public const string ClientName = "chat-telegram-notifications";
    public ChatProviderDescriptor Descriptor { get; } = new("telegram", "Telegram", 1, [1],
        new(ChatReceptionMode.None, false, false, false, false, false, false, ChatConversationRetirement.LocalOnly, 4096, 16384, 0),
        [new("bot-token", "Test bot token", true)]);

    public ChatSettingsValidation ValidateSettings(int version, JsonElement settings)
        => version == 1 && settings.ValueKind == JsonValueKind.Object && !settings.EnumerateObject().Any()
            ? ChatSettingsValidation.Success : new(false, "unsupported-telegram-settings");

    public async ValueTask<ChatProviderResult<IChatConnectionSession>> OpenSessionAsync(
        ChatConnectionConfiguration connection, IChatSecretStore secrets, CancellationToken ct = default)
    {
        if (connection.ProviderId != "telegram" || !this.ValidateSettings(connection.SettingsVersion, connection.Settings).Valid
            || !connection.Secrets.TryGetValue("bot-token", out var reference))
            return ChatProviderResult<IChatConnectionSession>.Fail(ChatFailureKind.InvalidConfiguration, "invalid-telegram-connection");
        var token = await secrets.ReadAsync(reference, ct);
        if (token is null || token.Length > 256 || !Regex.IsMatch(token, @"^\d+:[A-Za-z0-9_-]{20,}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return ChatProviderResult<IChatConnectionSession>.Fail(ChatFailureKind.InvalidConfiguration, "test-bot-token-required");
        var session = new Session(connection, clients, token);
        var identity = await session.CheckIdentityAsync(ct);
        if (!identity) { await session.DisposeAsync(); return ChatProviderResult<IChatConnectionSession>.Fail(ChatFailureKind.Unauthorized, "test-bot-identity-unverified"); }
        return ChatProviderResult<IChatConnectionSession>.Success(session);
    }

    private sealed class Session(ChatConnectionConfiguration configuration, IHttpClientFactory clients, string token) : IChatConnectionSession
    {
        private readonly TelegramTransport transport = new(new NamedClientFactory(clients),
            new FixedOptions(new ChatTransportOptions { Enabled = true, TokenBlobPath = null, Telegram = new() { BotToken = token } }),
            NullLogger<TelegramTransport>.Instance);
        private bool disposed;
        public string ConnectionId => configuration.Id;
        public string ReceptionKey => "telegram:" + configuration.InstallationId;

        public async Task<bool> CheckIdentityAsync(CancellationToken ct)
        {
            var me = await this.ReadAsync("getMe", new { }, ct);
            return me is { } result && result.TryGetProperty("id", out var id) && id.ToString() == configuration.InstallationId
                && result.TryGetProperty("is_bot", out var bot) && bot.ValueKind == JsonValueKind.True;
        }

        public async ValueTask<ChatProviderResult<ChatDestination>> ValidateDestinationAsync(ChatAddress address, CancellationToken ct = default)
        {
            if (!this.Matches(address)) return Unsupported<ChatDestination>();
            var result = await this.ReadAsync("getChat", new { chat_id = address.ChannelId }, ct);
            // Require the canonical ID, so a mutable @username cannot silently retarget saved delivery.
            if (result is not { } chat || !chat.TryGetProperty("id", out var id) || id.ToString() != address.ChannelId
                || !chat.TryGetProperty("type", out var type) || type.GetString() is not ("private" or "group" or "supergroup" or "channel"))
                return ChatProviderResult<ChatDestination>.Fail(ChatFailureKind.Unauthorized, "telegram-destination-unverified");
            return ChatProviderResult<ChatDestination>.Success(new(address, "Telegram destination", type.GetString()!));
        }

        public async ValueTask<ChatProviderResult<ChatSendReceipt>> SendAsync(ChatAddress address, ChatSemanticNotification notification, CancellationToken ct = default)
        {
            if (!this.Matches(address) || notification.Actions.Count != 0) return Unsupported<ChatSendReceipt>();
            var text = notification.Title + "\n\n" + notification.Body;
            if (text.Length > 4096) return ChatProviderResult<ChatSendReceipt>.Fail(ChatFailureKind.InvalidPayload, "notification-too-long");
            // The host generated/redacted this metadata. Reject a known credential even if a caller bypasses that policy.
            text = text.Replace(token, "[redacted]", StringComparison.Ordinal);
            var result = await this.transport.SendAsync(address.ChannelId, text, null, ct);
            return result.Ok && !string.IsNullOrWhiteSpace(result.MessageId)
                ? ChatProviderResult<ChatSendReceipt>.Success(new([new(address, result.MessageId)]))
                : ChatProviderResult<ChatSendReceipt>.Fail(ChatFailureKind.UnknownOutcome, "telegram-send-unconfirmed");
        }

        public ValueTask<ChatProviderResult<ChatConversation>> EnsureConversationAsync(ChatDestination destination, ChatConversationPolicy policy,
            string operationKey, string title, CancellationToken ct = default)
            => ValueTask.FromResult(policy == ChatConversationPolicy.Flat && this.Matches(destination.Address)
                ? ChatProviderResult<ChatConversation>.Success(new(destination.Address, null)) : Unsupported<ChatConversation>());
        public ValueTask<ChatProviderResult<bool>> RetireConversationAsync(ChatConversation conversation, CancellationToken ct = default)
            => ValueTask.FromResult(this.Matches(conversation.Address) ? ChatProviderResult<bool>.Success(true) : Unsupported<bool>());
        public ValueTask<ChatProviderResult<ChatDestinationPage>> DiscoverAsync(string? cursor, CancellationToken ct = default)
            => ValueTask.FromResult(Unsupported<ChatDestinationPage>());
        public ValueTask<ChatProviderResult<bool>> EditAsync(ChatMessageReference message, ChatSemanticNotification notification, CancellationToken ct = default)
            => ValueTask.FromResult(Unsupported<bool>());
        public Task ReceiveAsync(IChatInboundSink sink, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() { this.disposed = true; return ValueTask.CompletedTask; }
        private bool Matches(ChatAddress address) => !this.disposed && address.ConnectionId == this.ConnectionId
            && address.ExternalTenantId == configuration.InstallationId && address.ConversationId is null;
        private static ChatProviderResult<T> Unsupported<T>() => ChatProviderResult<T>.Fail(ChatFailureKind.NotSupported, "notifications-only");

        private async Task<JsonElement?> ReadAsync(string method, object body, CancellationToken ct)
        {
            try
            {
                using var client = clients.CreateClient(ClientName);
                using var response = await client.PostAsJsonAsync($"https://api.telegram.org/bot{token}/{method}", body, ct);
                if (!response.IsSuccessStatusCode) return null;
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                return json.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                    && json.RootElement.TryGetProperty("result", out var result) ? result.Clone() : null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return null; } // Never log provider bodies, token-bearing URLs or exceptions.
        }
    }

    private sealed class NamedClientFactory(IHttpClientFactory clients) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => clients.CreateClient(ClientName);
    }
    private sealed class FixedOptions(ChatTransportOptions value) : IOptionsMonitor<ChatTransportOptions>
    {
        public ChatTransportOptions CurrentValue => value;
        public ChatTransportOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ChatTransportOptions, string?> listener) => null;
    }
}

/// <summary>Externally managed slice credentials. No plaintext token is written to a DB, response, or legacy blob.</summary>
public sealed class ChatTestEnvironmentSecrets : IChatSecretStore
{
    public string Id => "environment";
    public ChatSecretStoreStatus Status { get; } = new(true, true);
    public ValueTask<string?> ReadAsync(ChatSecretReference reference, CancellationToken ct = default)
        => ValueTask.FromResult(reference.Store == this.Id && reference.Key.StartsWith("FORGE_CHAT_TEST_", StringComparison.Ordinal)
            && reference.Key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? Environment.GetEnvironmentVariable(reference.Key) : null);
    public ValueTask<ChatSecretReference> WriteAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException("externally-managed-secret");
    public ValueTask DeleteAsync(ChatSecretReference reference, CancellationToken ct = default) => throw new NotSupportedException("externally-managed-secret");
}