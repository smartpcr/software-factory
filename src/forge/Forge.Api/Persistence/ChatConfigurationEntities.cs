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

    /// <summary>
    /// When true every product, including products created later, is granted this
    /// connection. Grants are still written as <see cref="ChatConnectionProductRow"/>
    /// rows so every access check keeps one source of truth.
    /// </summary>
    public bool AllProducts { get; set; }
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
    public string ContentMode { get; set; } = "metadata";
    public int CatalogVersion { get; set; } = 1;
    public string EventKindsJson { get; set; } = "[]";
    public bool Paused { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset? RetiredAt { get; set; }

    /// <summary>
    /// <c>story</c>: one conversation for the story and all its workstreams.
    /// <c>workstream</c>: each workstream gets its own topic/thread on its first
    /// message; story-level events keep the story conversation.
    /// </summary>
    public string ConversationScope { get; set; } = ChatConversationScopes.Story;
}

/// <summary>Values of <see cref="ChatSubscriptionRow.ConversationScope"/>.</summary>
public static class ChatConversationScopes
{
    public const string Story = "story";
    public const string Workstream = "workstream";

    public static bool IsValid(string? value) => value is Story or Workstream;
}

/// <summary>
/// Host-wide default destinations. A story spawn with no chat settings of its own
/// is enabled from this list (copied, see <c>ChatHostDefaultService</c>).
/// </summary>
public sealed class ChatHostDefaultRow
{
    public string HostId { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>JSON array of <c>ChatRouteDraft</c>.</summary>
    public string RoutesJson { get; set; } = "[]";
    /// <summary>Stories created at or after this time are auto-enabled; older ones only via "apply to existing".</summary>
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class ChatConversationBindingRow
{
    public string Id { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public long SubscriptionRevision { get; set; }

    /// <summary>Workstream id for a per-workstream conversation; empty for the story conversation.</summary>
    public string ChildKey { get; set; } = "";

    /// <summary>Topic name / Slack root label chosen when the binding was created; null = provider default.</summary>
    public string? Title { get; set; }
    public long ActivationGeneration { get; set; }
    public string ConnectionId { get; set; } = "";
    public string ExternalTenantId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string? ConversationId { get; set; }
    public string? RootMessageId { get; set; }
    public string RootDesiredState { get; set; } = "active";
    public string RootRenderedState { get; set; } = "active";
    public long RootNextAttemptAt { get; set; }
    public long Revision { get; set; } = 1;
    public string ProvisioningState { get; set; } = "pending";
    public string RetirementState { get; set; } = "none";
    public long RetirementNextAttemptAt { get; set; }
    public string? OperationOwner { get; set; }
    public DateTimeOffset? RecoveryDeadline { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}