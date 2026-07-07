// -----------------------------------------------------------------------
// <copyright file="ChatTestDoubles.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net;
using System.Text;
using Common.Chat;
using Microsoft.Extensions.Options;

/// <summary>
/// Phase 95 — in-memory <see cref="IChatTransport"/> for tests: captures sends
/// + edits and lets a test inject inbound updates, so the whole bridge runs
/// without a live bot.
/// </summary>
public sealed class FakeChatTransport : IChatTransport
{
    private int nextMessageId = 1000;

    public List<(string ChatId, string Text, IReadOnlyList<ChatButton>? Buttons)> Sent { get; } = new();

    public List<(string ChatId, string MessageId, string Text)> Edits { get; } = new();

    public Queue<ChatInbound> Inbox { get; } = new();

    /// <summary>When set, every <see cref="SendAsync"/> returns a failure (retry-path tests).</summary>
    public bool FailSends { get; set; }

    /// <summary>Chat ids whose <see cref="SendAsync"/> throws (per-row isolation tests).</summary>
    public HashSet<string> ThrowOnChatId { get; } = new();

    /// <summary>Thread id passed to each <see cref="SendAsync"/> (parallel to <see cref="Sent"/>).</summary>
    public List<string?> SentThreadIds { get; } = new();

    /// <summary>Topics created via <see cref="CreateTopicAsync"/>.</summary>
    public List<(string ChatId, string Name)> TopicsCreated { get; } = new();

    private int nextThreadId = 500;

    public string Platform => "telegram";

    public Task<ChatSendResult> SendAsync(string chatId, string text, IReadOnlyList<ChatButton>? buttons, CancellationToken ct, string? threadId = null)
    {
        if (this.ThrowOnChatId.Contains(chatId))
        {
            throw new InvalidOperationException($"boom for {chatId}");
        }

        if (this.FailSends)
        {
            return Task.FromResult(ChatSendResult.Failure("forced failure"));
        }

        this.Sent.Add((chatId, text, buttons));
        this.SentThreadIds.Add(threadId);
        return Task.FromResult(ChatSendResult.Success((this.nextMessageId++).ToString()));
    }

    public Task<ChatSendResult> EditAsync(string chatId, string messageId, string text, IReadOnlyList<ChatButton>? buttons, CancellationToken ct)
    {
        this.Edits.Add((chatId, messageId, text));
        return Task.FromResult(ChatSendResult.Success(messageId));
    }

    public Task<string?> CreateTopicAsync(string chatId, string name, CancellationToken ct)
    {
        this.TopicsCreated.Add((chatId, name));
        return Task.FromResult<string?>((this.nextThreadId++).ToString());
    }

    public Task<IReadOnlyList<ChatInbound>> PollUpdatesAsync(long offset, CancellationToken ct)
    {
        var list = new List<ChatInbound>();
        while (this.Inbox.Count > 0)
        {
            list.Add(this.Inbox.Dequeue());
        }

        return Task.FromResult<IReadOnlyList<ChatInbound>>(list);
    }
}

/// <summary>Minimal <see cref="IOptionsMonitor{T}"/> that always returns one fixed value.</summary>
public sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => this.CurrentValue = value;

    public T CurrentValue { get; }

    public T Get(string? name) => this.CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>
/// <see cref="IHttpClientFactory"/> that hands out clients over one supplied
/// handler (without disposing it), so a stub/recording handler survives across
/// the transport's per-call <c>CreateClient</c>.
/// </summary>
public sealed class SingleHandlerHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler handler;

    public SingleHandlerHttpClientFactory(HttpMessageHandler handler) => this.handler = handler;

    public HttpClient CreateClient(string name) => new(this.handler, disposeHandler: false);
}

/// <summary>
/// Records the URL + request body of every outgoing call and returns whatever
/// <c>respond</c> produces (which may throw to simulate a network failure).
/// </summary>
public sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<string, HttpResponseMessage> respond;

    public RecordingHttpMessageHandler(Func<string, HttpResponseMessage> respond) => this.respond = respond;

    public List<string> Urls { get; } = new();

    public List<string> Bodies { get; } = new();

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        this.Urls.Add(request.RequestUri!.ToString());
        this.Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
        return this.respond(request.RequestUri!.ToString());
    }
}
