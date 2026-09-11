// -----------------------------------------------------------------------
// <copyright file="ChatLegacySource.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using Common.Chat;

/// <summary>Unmodified startup inputs, separate from the effective engine. Read-only references never export a token.</summary>
public sealed class ChatLegacySource : IChatSecretStore
{
    public ChatLegacySource(IConfiguration configuration)
    {
        this.Bridge = configuration.GetSection(ChatBridgeOptions.SectionName).Get<ChatBridgeOptions>() ?? new();
        this.Transport = configuration.GetSection(ChatBridgeOptions.SectionName).Get<ChatTransportOptions>() ?? new();
        this.Transport.TokenBlobPath ??= ChatBridgeOptions.DefaultTokenBlobPath();
    }
    public ChatBridgeOptions Bridge { get; }
    internal ChatTransportOptions Transport { get; }
    public string Id => "legacy-host";
    public ChatSecretStoreStatus Status => new(true, true);
    public ChatSecretReference Reference => new(this.Id, !string.IsNullOrWhiteSpace(this.Transport.Telegram.BotToken) ? "configured" : "dpapi:" + this.Transport.TokenBlobPath);
    public bool CredentialAvailable => this.Reference.Key == "configured" || File.Exists(this.Transport.TokenBlobPath);
    public ValueTask<string?> ReadAsync(ChatSecretReference reference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(reference == this.Reference ? ChatTokenResolver.Resolve(this.Transport) : null);
    }
    public ValueTask<ChatSecretReference> WriteAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException("externally-managed-secret");
    public ValueTask DeleteAsync(ChatSecretReference reference, CancellationToken ct = default) => throw new NotSupportedException("externally-managed-secret");
}