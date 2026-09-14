// -----------------------------------------------------------------------
// <copyright file="SlackProviderFixture.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Common.Chat;
using Common.Chat.Slack;
using Xunit;

internal sealed class SlackProviderFixture : HttpMessageHandler, IHttpClientFactory, IChatSecretStore, ISlackWebSocketFactory
{
    public const string Key = TelegramProviderFixture.Key;
    private int sequence;
    public SlackProviderPlugin Provider { get; }
    public ConcurrentQueue<(string Workspace, string Method, JsonElement Body)> Calls { get; } = new();
    public ConcurrentQueue<SlackFixtureSocket> Sockets { get; } = new();
    public Func<string, string, JsonElement, CancellationToken, Task<HttpResponseMessage?>>? Override { get; set; }
    public Func<int, SlackFixtureSocket>? CreateSocket { get; set; }
    public Dictionary<string, string?> Secrets { get; } = new();
    public SlackProviderFixture(TimeProvider? clock = null) { this.Provider = new(this, this, clock ?? new SlackFixtureClock()); }
    public HttpClient CreateClient(string name) => new(this, false) { Timeout = TimeSpan.FromSeconds(10) };
    public string Id => "slack-fixture";
    public ChatSecretStoreStatus Status => new(true, true);
    public ValueTask<string?> ReadAsync(ChatSecretReference reference, CancellationToken ct = default)
        => ValueTask.FromResult(this.Secrets.TryGetValue(reference.Key, out var value) ? value : reference.Key.StartsWith("app-", StringComparison.Ordinal)
            ? "xapp-" + reference.Key[4..] + "-fixturecredential" : "xoxb-" + reference.Key[4..] + "-fixturecredential");
    public ValueTask<ChatSecretReference> WriteAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask DeleteAsync(ChatSecretReference reference, CancellationToken ct = default) => throw new NotSupportedException();
    public static JsonElement Settings(bool privateChannels = true, string appId = "A1") => JsonSerializer.SerializeToElement(new { appId, includePrivateChannels = privateChannels });
    public ChatConnectionConfiguration Configuration(string connection = "slack-one", string workspace = "T1", bool privateChannels = true) => new(connection, "slack", workspace, 1, 1,
        Settings(privateChannels), new Dictionary<string, ChatSecretReference> { ["bot-token"] = new(this.Id, "bot-" + workspace), ["app-token"] = new(this.Id, "app-A1") });
    public async Task<SlackConnectionSession> OpenAsync(string connection = "slack-one", string workspace = "T1", bool privateChannels = true)
    {
        var opened = await this.Provider.OpenSessionAsync(this.Configuration(connection, workspace, privateChannels), this);
        Assert.Null(opened.Failure); return Assert.IsType<SlackConnectionSession>(opened.Value);
    }
    public static ChatAddress Address(string connection = "slack-one", string workspace = "T1", string? thread = "1700000000.000001", string channel = "C1") => new(connection, workspace, channel, thread);
    public static ChatSemanticNotification Notification(string body = "Choose an answer") => new("notification", "open-questions", "Forge", body, 1, [new("Answer", Key, "question.answer")]);
    public ISlackWebSocket Create()
    {
        var socket = this.CreateSocket?.Invoke(this.Sockets.Count) ?? new SlackFixtureSocket(); this.Sockets.Enqueue(socket); return socket;
    }
    public static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    public static HttpResponseMessage Error(string error) => Json(new { ok = false, error });
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Assert.Equal("https", request.RequestUri!.Scheme); Assert.Equal("slack.com", request.RequestUri.Host);
        Assert.Empty(request.RequestUri.Query); Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        var workspace = request.Headers.Authorization.Parameter!.Split('-')[1]; var method = request.RequestUri.Segments[^1];
        var raw = await request.Content!.ReadAsStringAsync(ct);
        var mediaType = request.Content.Headers.ContentType!.MediaType;
        if (method is "conversations.info" or "conversations.list" or "apps.connections.open")
            Assert.Equal("application/x-www-form-urlencoded", mediaType);
        else Assert.Equal("application/json", mediaType);
        var body = mediaType == "application/x-www-form-urlencoded"
            ? JsonSerializer.SerializeToElement(raw.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(field => field.Split('=', 2)).ToDictionary(field => WebUtility.UrlDecode(field[0]), field =>
                {
                    var value = WebUtility.UrlDecode(field[1]);
                    return field[0] switch { "limit" => (object)int.Parse(value, CultureInfo.InvariantCulture), "exclude_archived" => bool.Parse(value), _ => value };
                }))
            : JsonSerializer.Deserialize<JsonElement>(string.IsNullOrEmpty(raw) ? "{}" : raw);
        this.Calls.Enqueue((workspace, method, body));
        if (method == "chat.update")
        {
            Assert.True(body.GetProperty("as_user").GetBoolean());
            Assert.All(body.EnumerateObject(), field => Assert.Contains(field.Name, new[] { "channel", "text", "blocks", "parse", "link_names", "ts", "as_user" }));
        }
        if (this.Override is not null && await this.Override(workspace, method, body, ct) is { } replaced) return replaced;
        var ts = "1700000000." + Interlocked.Increment(ref this.sequence).ToString("D6");
        return method switch
        {
            "auth.test" => Json(new { ok = true, team_id = workspace, user_id = "UBOT" + workspace, bot_id = "B" + workspace }),
            "apps.connections.open" => Json(new { ok = true, url = "wss://wss.slack.com/link/?ticket=fixture-secret" }),
            "conversations.info" => Json(new { ok = true, channel = Channel(body.GetProperty("channel").GetString()!) }),
            "conversations.list" => Json(new { ok = true, channels = new[] { Channel("C1"), Channel("G1", true) }, response_metadata = new { next_cursor = "" } }),
            "chat.postMessage" => Json(new { ok = true, channel = body.GetProperty("channel").GetString(), ts }),
            "chat.postEphemeral" => Json(new { ok = true, message_ts = ts }),
            "chat.update" => Json(new { ok = true, channel = body.GetProperty("channel").GetString(), ts = body.GetProperty("ts").GetString() }),
            _ => throw new InvalidOperationException("Unexpected Slack method: " + method),
        };
    }
    public static object Channel(string id = "C1", bool privateChannel = false, bool member = true, bool archived = false, bool shared = false)
        => new { id, name = "forge-" + id, is_member = member, is_private = privateChannel, is_archived = archived, is_ext_shared = shared };
    public static JsonElement Event(string id, string workspace = "T1", string? thread = "1700000000.000001", string text = "hello", string channel = "C1", string sender = "U1",
        string? subtype = null, string? bot = null, string? envelopeId = null, string? authorizer = null, string app = "A1", string? userTeam = null)
        => JsonSerializer.SerializeToElement(new
        {
            type = "events_api", envelope_id = envelopeId ?? "envelope-" + id,
            payload = new { api_app_id = app, team_id = workspace, event_id = id,
                authorizations = new[] { new { team_id = workspace, user_id = authorizer ?? "UBOT" + workspace, is_bot = true, is_enterprise_install = false } },
                @event = new { type = "message", channel, channel_type = channel.StartsWith('G') ? "group" : "channel", user = sender, text, ts = "1700000001.000100", thread_ts = thread, subtype, bot_id = bot, user_team = userTeam } },
        });
    public static JsonElement Button(string id, string messageId, string key = Key, string workspace = "T1", string sender = "U1", string? thread = "1700000000.000001", string channel = "C1", string actionId = "forge_0")
        => JsonSerializer.SerializeToElement(new
        {
            type = "interactive", envelope_id = id,
            payload = new { type = "block_actions", api_app_id = "A1", team = new { id = workspace }, user = new { id = sender, team_id = workspace },
                channel = new { id = channel }, container = new { type = "message", channel_id = channel, message_ts = messageId },
                message = new { ts = messageId, thread_ts = thread, user = "UBOT" + workspace, bot_id = "B" + workspace },
                actions = new[] { new { type = "button", action_id = actionId, value = key } } },
        });
    // Slack omits the message body from ephemeral-message interactions.
    // https://docs.slack.dev/messaging/#ephemeral-messages
    public static JsonElement EphemeralButton(string id, string key, string workspace = "T1", string sender = "U1")
        => JsonSerializer.SerializeToElement(new
        {
            type = "interactive", envelope_id = id,
            payload = new { type = "block_actions", api_app_id = "A1", team = new { id = workspace }, user = new { id = sender, team_id = workspace },
                channel = new { id = "C1" }, container = new { type = "message", channel_id = "C1", message_ts = "1700000000.000100", is_ephemeral = true },
                actions = new[] { new { type = "button", action_id = "forge_identity_link", value = key } } },
        });
    internal static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}

internal sealed class SlackFixtureSocket : ISlackWebSocket
{
    private readonly Channel<JsonElement?> incoming = Channel.CreateUnbounded<JsonElement?>();
    public ConcurrentQueue<string> Acknowledged { get; } = new();
    public Func<string, CancellationToken, Task>? BeforeAck { get; set; }
    public bool Disposed { get; private set; }
    public Uri? ConnectedUri { get; private set; }
    public SlackFixtureSocket(string app = "A1") => this.Push(JsonSerializer.SerializeToElement(new { type = "hello", connection_info = new { app_id = app } }));
    public void Push(JsonElement? frame) => this.incoming.Writer.TryWrite(frame);
    public Task ConnectAsync(Uri uri, CancellationToken ct) { ct.ThrowIfCancellationRequested(); this.ConnectedUri = uri; return Task.CompletedTask; }
    public async Task<JsonElement?> ReadAsync(CancellationToken ct) => await this.incoming.Reader.ReadAsync(ct);
    public async Task AcknowledgeAsync(string envelopeId, CancellationToken ct)
    { if (this.BeforeAck is not null) await this.BeforeAck(envelopeId, ct); this.Acknowledged.Enqueue(envelopeId); }
    public ValueTask DisposeAsync() { this.Disposed = true; this.incoming.Writer.TryComplete(); return ValueTask.CompletedTask; }
}

internal sealed class SlackFixtureClock : TimeProvider
{
    private long ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddSeconds(Interlocked.Increment(ref this.ticks));
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => TimeProvider.System.CreateTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.FromMilliseconds(1), period);
}

internal sealed class SlackFixtureSink : IChatInboundSink, IChatReceptionDiagnosticSink
{
    public ConcurrentQueue<ChatInboundEnvelope> Envelopes { get; } = new();
    public ConcurrentQueue<string> Diagnostics { get; } = new();
    public ChatInboundAcceptance Result { get; set; } = ChatInboundAcceptance.Accepted;
    public Func<CancellationToken, Task>? BeforeAccept { get; set; }
    public async ValueTask<ChatInboundAcceptance> AcceptAsync(ChatInboundEnvelope envelope, CancellationToken ct = default)
    { if (this.BeforeAccept is not null) await this.BeforeAccept(ct); this.Envelopes.Enqueue(envelope); return this.Result; }
    public ValueTask<ChatInboundAcceptance> AcceptDiagnosticAsync(string code, string? externalTenantId = null, CancellationToken ct = default)
    { this.Diagnostics.Enqueue(code); return ValueTask.FromResult(this.Result); }
}