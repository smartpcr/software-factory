// -----------------------------------------------------------------------
// <copyright file="ChatDeliveryModel.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using Microsoft.EntityFrameworkCore;

internal static class ChatDeliveryModel
{
    public static void Configure(ModelBuilder mb)
    {
        ConfigureDelivery(mb); ConfigureReception(mb); ConfigureIdentity(mb);
    }

    public static void ConfigureDelivery(ModelBuilder mb)
    {
        var delivery = mb.Entity<ChatDeliveryRow>();
        delivery.ToTable("chat_deliveries").HasKey(x => x.Id);
        delivery.HasIndex(x => new { x.NotificationId, x.SubscriptionId }).IsUnique();
        delivery.HasIndex(x => new { x.State, x.NextAttemptAt, x.CreatedAt });
        delivery.HasOne<ChatNotificationRow>().WithMany().HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Restrict);
        delivery.HasOne<ChatConversationBindingRow>().WithMany().HasForeignKey(x => x.BindingId).OnDelete(DeleteBehavior.Restrict);
        delivery.Property(x => x.State).IsConcurrencyToken();
        var message = mb.Entity<ChatDeliveryMessageRow>();
        message.ToTable("chat_delivery_messages").HasKey(x => x.Id);
        message.HasIndex(x => new { x.DeliveryId, x.PartKey }).IsUnique();
        message.HasIndex(x => new { x.EditState, x.NextAttemptAt });
        message.HasIndex(x => x.MessageId);
        message.HasOne<ChatDeliveryRow>().WithMany().HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Restrict);
        message.Property(x => x.State).IsConcurrencyToken();
        mb.Entity<ChatNotificationRow>().HasIndex(x => new { x.Platform, x.DedupeKey }).IsUnique()
            .HasFilter("Platform = 'plugins'").HasDatabaseName("IX_chat_notifications_plugin_dedupe");
    }

    public static void ConfigureReception(ModelBuilder mb)
    {
        var inbox = mb.Entity<ChatInboxRow>();
        inbox.ToTable("chat_inbox").HasKey(x => x.Id);
        inbox.HasIndex(x => new { x.ConnectionId, x.DeliveryKey }).IsUnique();
        inbox.HasIndex(x => new { x.State, x.NextAttemptAt });
        inbox.Property(x => x.State).IsConcurrencyToken();
        mb.Entity<ChatConnectionStateRow>().ToTable("chat_connection_state").HasKey(x => x.ConnectionId);
        mb.Entity<ChatReceiverStateRow>().ToTable("chat_receiver_state").HasKey(x => new { x.ProviderId, x.ReceptionKey });
    }

    public static void ConfigureIdentity(ModelBuilder mb)
    {
        mb.Entity<ChatDeliveryMessageRow>().ToTable("chat_delivery_messages").HasKey(x => x.Id);
        var token = mb.Entity<ChatActionTokenRow>();
        token.ToTable("chat_action_tokens").HasKey(x => x.Hash);
        token.HasOne<ChatDeliveryMessageRow>().WithMany().HasForeignKey(x => x.DeliveryMessageId).OnDelete(DeleteBehavior.Restrict);
        token.HasIndex(x => x.ExpiresAt);
        mb.Entity<ChatOperatorLinkRow>().ToTable("chat_operator_links").HasKey(x => x.Id);
        mb.Entity<ChatOperatorLinkRow>().HasIndex(x => new { x.ProviderId, x.ExternalTenantId, x.ExternalUserId }).IsUnique().HasFilter("RevokedAt IS NULL");
        mb.Entity<ChatLinkChallengeRow>().ToTable("chat_link_challenges").HasKey(x => x.Hash);
        mb.Entity<ChatLinkChallengeRow>().Property(x => x.State).IsConcurrencyToken();
    }

    public static void ConfigureProduct(ModelBuilder mb)
    {
        var receipt = mb.Entity<ChatActionReceiptRow>();
        receipt.ToTable("chat_action_receipts").HasKey(x => x.Id);
        receipt.HasIndex(x => new { x.ProductId, x.ActionKey }).IsUnique();
        receipt.HasIndex(x => new { x.State, x.TerminalAt });
        receipt.Property(x => x.State).IsConcurrencyToken();
    }
}