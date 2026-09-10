// -----------------------------------------------------------------------
// <copyright file="ChatDeliveryEntities.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

/// <summary>Legacy columns remain readable by the compatibility engine. Plugin events have no provider target.</summary>
public partial class ChatNotificationRow
{
    public string ScopeKind { get; set; } = "legacy";
    public string? HostId { get; set; }
    public string? ArtifactKey { get; set; }
    public int CatalogVersion { get; set; }
    public int PayloadVersion { get; set; }
    public string? PayloadJson { get; set; }
    public long DomainRevision { get; set; }
    public string RouteState { get; set; } = "legacy";
    public string? RouteReason { get; set; }
}

public sealed class ChatDeliveryRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string NotificationId { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string BindingId { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string TargetJson { get; set; } = "{}";
    public string State { get; set; } = "pending";
    public int Attempts { get; set; }
    public long NextAttemptAt { get; set; }
    public long CreatedAt { get; set; }
    public long? TerminalAt { get; set; }
    public bool Actionable { get; set; }
    public long DomainRevision { get; set; }
    public string? OperationId { get; set; }
    public string? OwnerProcessInstanceId { get; set; }
    public long? RecoveryDeadline { get; set; }
    public string? LastError { get; set; }
}

public sealed class ChatDeliveryMessageRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DeliveryId { get; set; } = "";
    public string PartKey { get; set; } = "summary";
    public string? QuestionId { get; set; }
    public long ExpectedRevision { get; set; }
    public string? ActionKind { get; set; }
    public string? ConfirmationActorId { get; set; }
    public long? ExpiresAt { get; set; }
    public string? MessageId { get; set; }
    public string AddressJson { get; set; } = "{}";
    public string? RenderedJson { get; set; }
    public long RenderedRevision { get; set; }
    public long DesiredRevision { get; set; } = 1;
    public string State { get; set; } = "pending";
    public string EditState { get; set; } = "none";
    public bool ControlsRetired { get; set; }
    public string? OperationId { get; set; }
    public string? OwnerProcessInstanceId { get; set; }
    public long? RecoveryDeadline { get; set; }
    public long? TerminalAt { get; set; }
    public int Attempts { get; set; }
    public long NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class ChatInboxRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ConnectionId { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string DeliveryKey { get; set; } = "";
    public string? EnvelopeJson { get; set; }
    public long ReceivedAt { get; set; }
    public string State { get; set; } = "pending";
    public int Attempts { get; set; }
    public long NextAttemptAt { get; set; }
    public string? OwnerProcessInstanceId { get; set; }
    public long? RecoveryDeadline { get; set; }
    public long? TerminalAt { get; set; }
    public string? Result { get; set; }
    public string? ActionReceiptId { get; set; }
    public string? ResponseText { get; set; }
    public string? ContentScopesJson { get; set; }
    public long? CommandRevision { get; set; }
    public string ResponseState { get; set; } = "none";
    public string? ResponseMessageId { get; set; }
    public string? ResponseOwner { get; set; }
    public long? ResponseDeadline { get; set; }
    public long NextResponseAt { get; set; }
    public int ResponseAttempts { get; set; }
}

public sealed class ChatConnectionStateRow
{
    public string ConnectionId { get; set; } = "";
    public string? Cursor { get; set; }
    public long SettingsRevision { get; set; }
    public long? LastSendAt { get; set; }
    public long? LastReceiveAt { get; set; }
    public string Health { get; set; } = "starting";
    public string? Reason { get; set; }
    public long? RetiredAt { get; set; }
}

public sealed class ChatReceiverStateRow
{
    public string ProviderId { get; set; } = "";
    public string ReceptionKey { get; set; } = "";
    public string? OwnerProcessInstanceId { get; set; }
    public long? RecoveryDeadline { get; set; }
    public string State { get; set; } = "starting";
    public long CheckpointAt { get; set; }
    public string? DiagnosticCode { get; set; }
    public string? DiagnosticTenantId { get; set; }
    public long? DiagnosticAt { get; set; }
    public long DiagnosticCount { get; set; }
    public long? RetiredAt { get; set; }
}

/// <summary>Only a hash is stored. The token contains no target, credentials or authority.</summary>
public sealed class ChatActionTokenRow
{
    public string Hash { get; set; } = "";
    public string DeliveryMessageId { get; set; } = "";
    public string Action { get; set; } = "";
    public string? QuestionId { get; set; }
    public int? ChoiceIndex { get; set; }
    public long ExpectedRevision { get; set; }
    public string? ActorId { get; set; }
    public long ExpiresAt { get; set; }
    public string State { get; set; } = "active";
}

public sealed class ChatOperatorLinkRow
{
    public string Id { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ExternalTenantId { get; set; } = "";
    public string ExternalUserId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string EnrollmentRevision { get; set; } = "";
    public long CreatedAt { get; set; }
    public long? RevokedAt { get; set; }
}

public sealed class ChatLinkChallengeRow
{
    public string Hash { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public long ConnectionRevision { get; set; }
    public string UserId { get; set; } = "";
    public string ExternalTenantId { get; set; } = "";
    public string ExternalUserId { get; set; } = "";
    public long ExpiresAt { get; set; }
    public string State { get; set; } = "pending";
    public string? PreviousLinkId { get; set; }
}

/// <summary>Authoritative domain result in the product database, committed with transactional mutations.</summary>
public sealed class ChatActionReceiptRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProductId { get; set; } = "";
    public string ActionKey { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Action { get; set; } = "";
    public long ExpectedRevision { get; set; }
    public string ActorId { get; set; } = "";
    public string State { get; set; } = "completed";
    public string Result { get; set; } = "accepted";
    public string? OperationId { get; set; }
    public string? OwnerProcessInstanceId { get; set; }
    public long CreatedAt { get; set; }
    public long? TerminalAt { get; set; }
}