// -----------------------------------------------------------------------
// <copyright file="ChatConfigurationModel.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>The six additive control tables share their model between fresh DBs and Phase 98.</summary>
internal static class ChatConfigurationModel
{
    public static void Configure(ModelBuilder mb)
    {
        var plugin = mb.Entity<SpawnPluginRow>();
        plugin.ToTable("spawn_plugins", t => t.HasCheckConstraint("CK_spawn_plugins_versions", "Revision > 0 AND SettingsVersion > 0 AND ActivationGeneration >= 0"));
        plugin.HasKey(x => new { x.ProductId, x.SpawnId, x.PluginId });
        plugin.Property(x => x.Revision).IsConcurrencyToken();

        var connection = mb.Entity<ChatConnectionRow>();
        connection.ToTable("chat_connections", t => t.HasCheckConstraint("CK_chat_connections_version", "Revision > 0 AND SettingsVersion > 0 AND length(InstallationId) > 0"));
        connection.HasKey(x => x.Id);
        connection.Property(x => x.Revision).IsConcurrencyToken();
        connection.HasIndex(x => new { x.ProviderId, x.InstallationId }).IsUnique().HasFilter("Enabled = 1 AND DeletedAt IS NULL")
            .HasDatabaseName("IX_chat_connections_active_installation");

        var grant = mb.Entity<ChatConnectionProductRow>();
        grant.ToTable("chat_connection_products").HasKey(x => new { x.ConnectionId, x.ProductId });
        grant.HasOne<ChatConnectionRow>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);

        var destination = mb.Entity<ChatDestinationRow>();
        destination.ToTable("chat_destinations");
        destination.HasKey(x => x.Id);
        destination.Property(x => x.Revision).IsConcurrencyToken();
        destination.HasIndex(x => new { x.ConnectionId, x.ExternalTenantId, x.ChannelId }).IsUnique()
            .HasDatabaseName("IX_chat_destinations_address");
        destination.HasOne<ChatConnectionRow>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);

        var subscription = mb.Entity<ChatSubscriptionRow>();
        subscription.ToTable("chat_subscriptions", t =>
        {
            t.HasCheckConstraint("CK_chat_subscriptions_scope", """
                (ScopeKind = 'spawn' AND ProductId IS NOT NULL AND length(ProductId) > 0
                  AND SpawnId IS NOT NULL AND length(SpawnId) > 0 AND PluginId = 'chat' AND PluginId IS NOT NULL AND HostId IS NULL
                  AND ScopeKey = 'spawn:' || hex(CAST(ProductId AS BLOB)) || ':' || hex(CAST(SpawnId AS BLOB)) || ':chat')
                OR (ScopeKind = 'host' AND HostId IS NOT NULL AND length(HostId) > 0
                  AND ProductId IS NULL AND SpawnId IS NULL AND PluginId IS NULL
                  AND ScopeKey = 'host:' || hex(CAST(HostId AS BLOB)))
                """);
            t.HasCheckConstraint("CK_chat_subscriptions_versions", "Revision > 0 AND CatalogVersion > 0 AND ActivationGeneration >= 0");
            t.HasCheckConstraint("CK_chat_subscriptions_policy", "ConversationPolicy IN ('flat','topic','thread')");
        });
        subscription.HasKey(x => x.Id);
        subscription.Property(x => x.Revision).IsConcurrencyToken();
        subscription.HasIndex(x => new { x.ScopeKey, x.DestinationId, x.ConversationPolicy }).IsUnique().HasFilter("RetiredAt IS NULL")
            .HasDatabaseName("IX_chat_subscriptions_active_scope_destination");
        subscription.HasOne<ChatDestinationRow>().WithMany().HasForeignKey(x => x.DestinationId).OnDelete(DeleteBehavior.Restrict);

        var binding = mb.Entity<ChatConversationBindingRow>();
        binding.ToTable("chat_conversation_bindings");
        binding.HasKey(x => x.Id);
        binding.Property(x => x.Revision).IsConcurrencyToken();
        // One live conversation per (subscription revision, child). ChildKey is '' for the
        // story conversation, so story bindings stay unique (SQLite treats NULLs as distinct).
        binding.Property(x => x.ChildKey).HasDefaultValue("");
        binding.HasIndex(x => new { x.SubscriptionId, x.SubscriptionRevision, x.ChildKey }).IsUnique().HasFilter("RetiredAt IS NULL")
            .HasDatabaseName("IX_chat_conversation_bindings_active_child");
        binding.HasIndex(x => new { x.ConnectionId, x.ExternalTenantId, x.ChannelId, x.ConversationId });
        binding.HasOne<ChatSubscriptionRow>().WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        binding.HasOne<ChatConnectionRow>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);

        var hostDefault = mb.Entity<ChatHostDefaultRow>();
        hostDefault.ToTable("chat_host_defaults");
        hostDefault.HasKey(x => x.HostId);
        hostDefault.Property(x => x.Revision).IsConcurrencyToken();
    }
}