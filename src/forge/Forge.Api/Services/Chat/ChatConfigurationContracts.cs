// -----------------------------------------------------------------------
// <copyright file="ChatConfigurationContracts.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Chat;

using System.Text;
using System.Text.Json;
using Common.Chat;
using Forge.Api.Persistence;

public sealed class ChatConfigurationException(string code, int status = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed record ChatConfigurationScope
{
    private ChatConfigurationScope(string kind, string? productId, string? spawnId, string? hostId)
        => (this.Kind, this.ProductId, this.SpawnId, this.HostId) = (kind, productId, spawnId, hostId);
    public string Kind { get; }
    public string? ProductId { get; }
    public string? SpawnId { get; }
    public string? HostId { get; }
    public string Key => this.Kind == "host" ? "host:" + Hex(this.HostId!) : "spawn:" + Hex(this.ProductId!) + ":" + Hex(this.SpawnId!) + ":chat";
    public static ChatConfigurationScope Spawn(string productId, string spawnId)
        => new("spawn", Required(productId), Required(spawnId), null);
    public static ChatConfigurationScope Host(string hostId) => new("host", null, null, Required(hostId));
    private static string Hex(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));
    private static string Required(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512
        ? value : throw new ChatConfigurationException("invalid-scope");
}

public sealed record ChatConnectionWrite(long ExpectedRevision, string ProviderId, string InstallationId, string DisplayName,
    bool Enabled, int SettingsVersion, JsonElement Settings,
    IReadOnlyDictionary<string, ChatSecretReference> SecretReferences, IReadOnlyList<string> ProductIds);
public sealed record ChatConnectionView(string Id, string ProviderId, string DisplayName, bool Enabled,
    long Revision, int SettingsVersion, string State, string? Reason);
public sealed record ChatConnectionDetails(string Id, string ProviderId, string InstallationId, string DisplayName, bool Enabled,
    long Revision, int SettingsVersion, JsonElement Settings, IReadOnlyDictionary<string, ChatSecretReference> SecretReferences,
    IReadOnlyList<string> ProductIds);
public sealed record ChatDestinationWrite(long ExpectedRevision, string ExternalTenantId, string ChannelId, string DisplayName, string Kind);
public sealed record ChatPluginWrite(long ExpectedRevision, bool Enabled, int SettingsVersion, JsonElement Settings,
    bool ActivateNewRun = false, string? CopyFromSpawnId = null);
public sealed record ChatSubscriptionWrite(long ExpectedRevision, long ParentRevision, string DestinationId, string ConversationPolicy,
    bool Interactive = false, bool Paused = false, int CatalogVersion = 1, IReadOnlyList<string>? EventKinds = null, string ContentMode = "metadata");
public sealed record ChatPluginView(string ProductId, string SpawnId, bool Enabled, long Revision,
    long ActivationGeneration, int SettingsVersion, string? CopyOrigin, string State, string? Reason,
    IReadOnlyList<ChatSubscriptionRow> Subscriptions);
public sealed record HostChatSetup(string HostId, string ScopeKey, string State, string? Reason, IReadOnlyList<ChatSubscriptionRow> Subscriptions);