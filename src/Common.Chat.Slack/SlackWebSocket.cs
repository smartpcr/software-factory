// -----------------------------------------------------------------------
// <copyright file="SlackWebSocket.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Common.Chat.Slack;

using System.Net.WebSockets;
using System.Text.Json;

/// <summary>Transport seam for protocol fixtures. Production uses ClientWebSocket with bounded, fragmented JSON messages.</summary>
public interface ISlackWebSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);
    Task<JsonElement?> ReadAsync(CancellationToken ct);
    Task AcknowledgeAsync(string envelopeId, CancellationToken ct);
}

public interface ISlackWebSocketFactory
{
    ISlackWebSocket Create();
}

public sealed class SlackWebSocketFactory : ISlackWebSocketFactory
{
    public ISlackWebSocket Create() => new SlackWebSocket();
}

internal sealed class SlackWebSocket : ISlackWebSocket
{
    private readonly ClientWebSocket socket = new();
    public Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        this.socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        return this.socket.ConnectAsync(uri, ct);
    }
    public async Task<JsonElement?> ReadAsync(CancellationToken ct)
    {
        using var buffer = new MemoryStream(); var chunk = new byte[8192];
        ValueWebSocketReceiveResult received;
        do
        {
            received = await this.socket.ReceiveAsync(chunk.AsMemory(), ct);
            if (received.MessageType == WebSocketMessageType.Close) return null;
            if (received.MessageType != WebSocketMessageType.Text || buffer.Length + received.Count > 1024 * 1024)
                throw new InvalidOperationException("slack-socket-payload-invalid");
            buffer.Write(chunk, 0, received.Count);
        } while (!received.EndOfMessage);
        using var json = JsonDocument.Parse(buffer.ToArray()); return json.RootElement.Clone();
    }
    public Task AcknowledgeAsync(string envelopeId, CancellationToken ct)
        => this.socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { envelope_id = envelopeId }).AsMemory(), WebSocketMessageType.Text, true, ct).AsTask();
    public ValueTask DisposeAsync() { this.socket.Abort(); this.socket.Dispose(); return ValueTask.CompletedTask; }
}