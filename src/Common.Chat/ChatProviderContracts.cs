// -----------------------------------------------------------------------
// <copyright file="ChatProviderContracts.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat;

using System.Text.Json;

public enum ChatReceptionMode { None, LongPolling, Socket }
public enum ChatConversationPolicy { Flat, Topic, Thread }
public enum ChatConversationRetirement { LocalOnly, RemoteClose }
public enum ChatFailureKind { NotSupported, InvalidConfiguration, Unauthorized, RateLimited, Unavailable, InvalidPayload, UnknownOutcome }

public sealed record ChatProviderCapabilities(
    ChatReceptionMode Reception, bool TextReplies, bool Buttons, bool Edits,
    bool Topics, bool Threads, bool DestinationDiscovery,
    ChatConversationRetirement Retirement, int MaxTextCharacters, int MaxPayloadBytes, int MaxActionBytes);

public sealed record ChatCredentialRequirement(string Id, string DisplayName, bool Required);
public sealed record ChatProviderDescriptor(string Id, string DisplayName, int ContractVersion,
    IReadOnlyList<int> SettingsVersions, ChatProviderCapabilities Capabilities,
    IReadOnlyList<ChatCredentialRequirement> Credentials);

/// <summary>Only references cross the configuration boundary; credentials are resolved by the session.</summary>
public sealed record ChatSecretReference(string Store, string Key);
public sealed record ChatSecretStoreStatus(bool Available, bool ExternallyManaged, string? Reason = null);
public interface IChatSecretStore
{
    string Id { get; }
    ChatSecretStoreStatus Status { get; }
    ValueTask<string?> ReadAsync(ChatSecretReference reference, CancellationToken ct = default);
    ValueTask<ChatSecretReference> WriteAsync(string key, string value, CancellationToken ct = default);
    ValueTask DeleteAsync(ChatSecretReference reference, CancellationToken ct = default);
}

public sealed record ChatConnectionConfiguration(string Id, string ProviderId, string InstallationId,
    int SettingsVersion, long Revision, JsonElement Settings, IReadOnlyDictionary<string, ChatSecretReference> Secrets);

/// <summary>All external IDs, including message timestamps, are opaque, case-sensitive strings.</summary>
public sealed record ChatAddress(string ConnectionId, string ExternalTenantId, string ChannelId, string? ConversationId = null);
public sealed record ChatDestination(ChatAddress Address, string DisplayName, string Kind);
public sealed record ChatDestinationPage(IReadOnlyList<ChatDestination> Items, string? NextCursor);
public sealed record ChatConversation(ChatAddress Address, string? RootMessageId);
public sealed record ChatMessageReference(ChatAddress Address, string MessageId);
public sealed record ChatSemanticAction(string Label, string Token, string Kind);
public sealed record ChatSemanticNotification(string Id, string Kind, string Title, string Body,
    long Revision, IReadOnlyList<ChatSemanticAction> Actions, string? QuestionReference = null, bool ForceReply = false);
public sealed record ChatSendReceipt(IReadOnlyList<ChatMessageReference> Messages);
public sealed record ChatProviderFailure(ChatFailureKind Kind, string Code, TimeSpan? RetryAfter = null);

/// <summary>Failures contain safe codes, never raw provider exceptions, URLs, or credential values.</summary>
public sealed record ChatProviderResult<T>(T? Value, ChatProviderFailure? Failure)
{
    public bool Succeeded => this.Failure is null && this.Value is not null;
    public static ChatProviderResult<T> Success(T value) => new(value, null);
    public static ChatProviderResult<T> Fail(ChatFailureKind kind, string code, TimeSpan? retryAfter = null)
        => new(default, new(kind, code, retryAfter));
}

public sealed record ChatSettingsValidation(bool Valid, string? Code = null)
{
    public static ChatSettingsValidation Success { get; } = new(true);
}

public sealed record ChatInboundEnvelope(string ProviderId, string ConnectionId, string DeliveryKey,
    ChatAddress Address, string SenderId, string Kind, string? MessageId, string? ReplyToMessageId,
    string? Text, string? ActionToken, DateTimeOffset ReceivedAt, int Version = 1,
    string? CallbackQueryId = null, string? Command = null, string? CommandArgument = null);
public enum ChatInboundAcceptance { Accepted, Duplicate, Retry }

public interface IChatInboundSink
{
    /// <summary>Accepted/Duplicate means durable receipt. Retry must not be acknowledged upstream.</summary>
    ValueTask<ChatInboundAcceptance> AcceptAsync(ChatInboundEnvelope envelope, CancellationToken ct = default);
}

public interface IChatProviderPlugin
{
    ChatProviderDescriptor Descriptor { get; }
    ChatSettingsValidation ValidateSettings(int version, JsonElement settings);
    ValueTask<ChatProviderResult<IChatConnectionSession>> OpenSessionAsync(
        ChatConnectionConfiguration connection, IChatSecretStore secrets, CancellationToken ct = default);
}

/// <summary>One immutable connection revision. Unsupported operations explicitly return NotSupported.</summary>
public interface IChatConnectionSession : IAsyncDisposable
{
    string ConnectionId { get; }
    string ReceptionKey { get; }
    ValueTask<ChatProviderResult<ChatDestination>> ValidateDestinationAsync(ChatAddress address, CancellationToken ct = default);
    ValueTask<ChatProviderResult<ChatDestinationPage>> DiscoverAsync(string? cursor, CancellationToken ct = default);
    ValueTask<ChatProviderResult<ChatConversation>> EnsureConversationAsync(ChatDestination destination,
        ChatConversationPolicy policy, string operationKey, string title, CancellationToken ct = default);
    ValueTask<ChatProviderResult<bool>> RetireConversationAsync(ChatConversation conversation, CancellationToken ct = default);
    ValueTask<ChatProviderResult<ChatSendReceipt>> SendAsync(ChatAddress address, ChatSemanticNotification notification, CancellationToken ct = default);
    ValueTask<ChatProviderResult<bool>> EditAsync(ChatMessageReference message, ChatSemanticNotification notification, CancellationToken ct = default);
    Task ReceiveAsync(IChatInboundSink sink, CancellationToken ct);
}

public sealed record ChatAcceptedCursor(string ConnectionId, string Cursor);
public interface IChatDurableBatchSink : IChatInboundSink
{
    /// <summary>Envelopes and their cursors commit together; an empty poll must not advance a cursor.</summary>
    ValueTask<ChatInboundAcceptance> AcceptBatchAsync(IReadOnlyList<ChatInboundEnvelope> envelopes,
        IReadOnlyList<ChatAcceptedCursor> cursors, CancellationToken ct = default);
}

public interface IChatResumableSession
{
    ValueTask RestoreCursorAsync(string? cursor, CancellationToken ct = default);
}