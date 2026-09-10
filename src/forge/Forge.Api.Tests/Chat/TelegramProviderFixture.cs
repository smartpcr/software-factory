// -----------------------------------------------------------------------
// <copyright file="TelegramProviderFixture.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Common.Chat;
using Common.Chat.Telegram;

internal sealed class TelegramProviderFixture : HttpMessageHandler, IHttpClientFactory, IChatSecretStore
{
    public const string Key = "a1_ABCDEFGHIJKLMNOPQRSTUV";
    public ConcurrentQueue<(string Bot, string Method, JsonElement Body)> Calls { get; } = new();
    public ConcurrentQueue<JsonElement> Updates { get; } = new();
    public Func<string, string, JsonElement, CancellationToken, Task<HttpResponseMessage?>>? Override { get; set; }
    public TelegramProvider Provider { get; }
    private int sequence = 100;
    public TelegramProviderFixture() { this.Provider = new(this); }
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
    public string Id => "fixture";
    public ChatSecretStoreStatus Status => new(true, true);
    public ValueTask<string?> ReadAsync(ChatSecretReference reference, CancellationToken ct = default) => ValueTask.FromResult<string?>(reference.Key + ":ABCDEFGHIJKLMNOPQRSTUVWXYZ_123456789");
    public ValueTask<ChatSecretReference> WriteAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask DeleteAsync(ChatSecretReference reference, CancellationToken ct = default) => throw new NotSupportedException();
    public ChatConnectionConfiguration Configuration(string connection = "bot-one", string bot = "12345") => new(connection, "telegram", bot, 1, 1,
        JsonSerializer.SerializeToElement(new { longPollSeconds = 1 }), new Dictionary<string, ChatSecretReference> { ["bot-token"] = new(this.Id, bot) });
    public async Task<TelegramSession> OpenAsync(string connection = "bot-one", string bot = "12345") => (TelegramSession)(await this.Provider.OpenSessionAsync(this.Configuration(connection, bot), this)).Value!;
    public static ChatAddress Address(string connection = "bot-one", string bot = "12345", string? topic = "7") => new(connection, bot, "-100", topic);
    public static ChatSemanticNotification Notification(string body = "Choose an answer") => new("n", "open-questions", "Forge", body, 1, [new("Answer", Key, "question.answer")]);
    public static HttpResponseMessage Ok(object result) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, result })) };
    public static HttpResponseMessage Error(int status, string description, int retry = 2) => new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(new { ok = false, error_code = status, description, parameters = new { retry_after = retry } })) };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var segments = request.RequestUri!.AbsolutePath.Split('/'); var bot = segments[1][3..].Split(':')[0]; var method = segments[2];
        var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct)); this.Calls.Enqueue((bot, method, body));
        if (this.Override is not null && await this.Override(bot, method, body, ct) is { } replaced) return replaced;
        return method switch
        {
            "getMe" => Ok(new { id = long.Parse(bot), is_bot = true, username = "forge_test_bot" }),
            "getWebhookInfo" => Ok(new { url = "" }),
            "getChat" => body.GetProperty("chat_id").GetString() == "55" ? Ok(new { id = 55, type = "private" }) : Ok(new { id = -100, type = "supergroup", is_forum = true }),
            "getChatMember" => Ok(new { status = "administrator", can_manage_topics = true }),
            "getUpdates" => Ok(this.Updates.TryDequeue(out var update) ? new[] { update } : []),
            "createForumTopic" => Ok(new { message_thread_id = 7 }),
            "sendMessage" => Ok(new { message_id = Interlocked.Increment(ref this.sequence) }),
            "editMessageText" or "answerCallbackQuery" or "closeForumTopic" => Ok(true),
            _ => throw new InvalidOperationException("Unexpected fixture method: " + method),
        };
    }
    public static JsonElement Message(int update, string? text = "hello", string? commandBot = null, int? topic = 7)
        => JsonSerializer.SerializeToElement(new { update_id = update, message = new { message_id = 20 + update, message_thread_id = topic,
            from = new { id = 42, is_bot = false }, chat = new { id = -100, type = "supergroup", is_forum = true }, text = commandBot is null ? text : "/status@" + commandBot } });
    public static JsonElement Callback(int update) => JsonSerializer.SerializeToElement(new { update_id = update,
        callback_query = new { id = "callback-" + update, data = Key, from = new { id = 42, is_bot = false },
            message = new { message_id = 101, message_thread_id = 7, chat = new { id = -100, type = "supergroup", is_forum = true } } } });
}
internal sealed class TelegramBatchSink : IChatDurableBatchSink
{
    public List<ChatInboundEnvelope> Envelopes { get; } = [];
    public List<ChatAcceptedCursor> Cursors { get; } = [];
    public ChatInboundAcceptance Result { get; set; } = ChatInboundAcceptance.Accepted;
    public ValueTask<ChatInboundAcceptance> AcceptAsync(ChatInboundEnvelope envelope, CancellationToken ct = default) => throw new InvalidOperationException("Batch required");
    public ValueTask<ChatInboundAcceptance> AcceptBatchAsync(IReadOnlyList<ChatInboundEnvelope> envelopes, IReadOnlyList<ChatAcceptedCursor> cursors, CancellationToken ct = default)
    { this.Envelopes.AddRange(envelopes); this.Cursors.AddRange(cursors); return ValueTask.FromResult(this.Result); }
}