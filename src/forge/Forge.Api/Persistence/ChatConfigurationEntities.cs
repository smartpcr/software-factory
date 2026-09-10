// -----------------------------------------------------------------------
// <copyright file="ChatConfigurationEntities.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

public sealed class SpawnPluginRow
{
    public string ProductId { get; set; } = "";
    public string SpawnId { get; set; } = "";
    public string PluginId { get; set; } = "chat";
    public string RunIdentity { get; set; } = "";
    public bool Enabled { get; set; }
    public int SettingsVersion { get; set; } = 1;
    public string SettingsJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
    public long ActivationGeneration { get; set; }
    public string? CopyOrigin { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}

public sealed class ChatConnectionRow
{
    public string Id { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string InstallationId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; }
    public int SettingsVersion { get; set; } = 1;
    public string SettingsJson { get; set; } = "{}";
    public string SecretReferencesJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class ChatConnectionProductRow
{
    public string ConnectionId { get; set; } = "";
    public string ProductId { get; set; } = "";
}

public sealed class ChatDestinationRow
{
    public string Id { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string ExternalTenantId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string AccessState { get; set; } = "unverified";
    public DateTimeOffset? ValidatedAt { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class ChatSubscriptionRow
{
    public string Id { get; set; } = "";
    public string ScopeKind { get; set; } = "spawn";
    public string ScopeKey { get; set; } = "";
    public string? ProductId { get; set; }
    public string? SpawnId { get; set; }
    public string? PluginId { get; set; }
    public string? HostId { get; set; }
    public long ActivationGeneration { get; set; }
    public string DestinationId { get; set; } = "";
    public string ConversationPolicy { get; set; } = "flat";
    public bool Interactive { get; set; }
    public int CatalogVersion { get; set; } = 1;
    public string EventKindsJson { get; set; } = "[]";
    public bool Paused { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset? RetiredAt { get; set; }
}

public sealed class ChatConversationBindingRow
{
    public string Id { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public long SubscriptionRevision { get; set; }
    public long ActivationGeneration { get; set; }
    public string ConnectionId { get; set; } = "";
    public string ExternalTenantId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string? ConversationId { get; set; }
    public string? RootMessageId { get; set; }
    public long Revision { get; set; } = 1;
    public string ProvisioningState { get; set; } = "pending";
    public string? OperationOwner { get; set; }
    public DateTimeOffset? RecoveryDeadline { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}