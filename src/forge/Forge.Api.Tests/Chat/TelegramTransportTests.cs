// -----------------------------------------------------------------------
// <copyright file="TelegramTransportTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Net;
using Forge.Api.Services.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Phase 95 stage 1.3 — Telegram transport: request shaping, char cap, inbound
/// mapping, and failure tolerance.
/// </summary>
public class TelegramTransportTests
{
    private static TelegramTransport NewTransport(HttpMessageHandler handler, string token = "TESTTOKEN")
    {
        var opt = new ChatBridgeOptions { Enabled = true };
        opt.Telegram.BotToken = token;
        opt.Telegram.ApiBaseUrl = "https://api.telegram.org";
        return new TelegramTransport(
            new SingleHandlerHttpClientFactory(handler),
            new StaticOptionsMonitor<ChatBridgeOptions>(opt),
            NullLogger<TelegramTransport>.Instance);
    }

    [Fact]
    public async Task Send_With_Buttons_Emits_Inline_Keyboard()
    {
        var handler = new RecordingHttpMessageHandler(_ =>
            RecordingHttpMessageHandler.Json("{\"ok\":true,\"result\":{\"message_id\":42}}"));
        var transport = NewTransport(handler);

        var result = await transport.SendAsync(
            "999",
            "hello",
            new[] { new ChatButton("Yes", "cb:yes"), new ChatButton("No", "cb:no") },
            default);

        Assert.True(result.Ok);
        Assert.Equal("42", result.MessageId);
        Assert.Contains("/botTESTTOKEN/sendMessage", handler.Urls[0]);
        var body = handler.Bodies[0];
        Assert.Contains("inline_keyboard", body);
        Assert.Contains("cb:yes", body);
        Assert.Contains("Yes", body);
        Assert.Contains("\"chat_id\":\"999\"", body);
    }

    [Fact]
    public void Cap_Truncates_Over_Limit_With_Ui_Hint()
    {
        var capped = TelegramTransport.Cap(new string('x', 5000));
        Assert.True(capped.Length <= TelegramTransport.MaxMessageChars);
        Assert.EndsWith("open in UI", capped);

        Assert.Equal("short", TelegramTransport.Cap("short"));
    }

    [Fact]
    public void ParseUpdates_Maps_Text_Reply_And_Callback_Query()
    {
        const string Json =
            "{\"ok\":true,\"result\":[" +
            "{\"update_id\":10,\"message\":{\"message_id\":5,\"chat\":{\"id\":111},\"from\":{\"id\":222},\"text\":\"hi\",\"reply_to_message\":{\"message_id\":3}}}," +
            "{\"update_id\":11,\"callback_query\":{\"id\":\"c\",\"data\":\"a:foo\",\"from\":{\"id\":222},\"message\":{\"message_id\":7,\"chat\":{\"id\":111}}}}" +
            "]}";

        var updates = TelegramTransport.ParseUpdates(Json);

        Assert.Equal(2, updates.Count);

        var msg = updates[0];
        Assert.Equal(10, msg.UpdateId);
        Assert.Equal("111", msg.ChatId);
        Assert.Equal("222", msg.FromId);
        Assert.Equal("hi", msg.Text);
        Assert.Null(msg.CallbackData);
        Assert.Equal("3", msg.ReplyToMessageId);

        var cbq = updates[1];
        Assert.Equal(11, cbq.UpdateId);
        Assert.Equal("a:foo", cbq.CallbackData);
        Assert.Null(cbq.Text);
        Assert.Equal("111", cbq.ChatId);
        Assert.Equal("7", cbq.ReplyToMessageId);
    }

    [Fact]
    public async Task PollUpdates_Maps_Live_Response()
    {
        var handler = new RecordingHttpMessageHandler(_ =>
            RecordingHttpMessageHandler.Json(
                "{\"ok\":true,\"result\":[{\"update_id\":9,\"message\":{\"message_id\":1,\"chat\":{\"id\":5},\"from\":{\"id\":6},\"text\":\"yo\"}}]}"));
        var transport = NewTransport(handler);

        var updates = await transport.PollUpdatesAsync(offset: 0, default);

        Assert.Single(updates);
        Assert.Equal("yo", updates[0].Text);
        Assert.Contains("/getUpdates?offset=0", handler.Urls[0]);
    }

    [Fact]
    public async Task Send_On_Server_Error_Returns_Failure_Not_Throw()
    {
        var handler = new RecordingHttpMessageHandler(_ =>
            RecordingHttpMessageHandler.Json("{\"ok\":false,\"description\":\"boom\"}", HttpStatusCode.InternalServerError));
        var transport = NewTransport(handler);

        var result = await transport.SendAsync("1", "x", buttons: null, default);

        Assert.False(result.Ok);
        Assert.Contains("boom", result.Error);
    }

    [Fact]
    public async Task Send_On_Network_Throw_Returns_Failure_Not_Throw()
    {
        var handler = new RecordingHttpMessageHandler(_ => throw new HttpRequestException("net down"));
        var transport = NewTransport(handler);

        var result = await transport.SendAsync("1", "x", buttons: null, default);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Poll_On_Failure_Returns_Empty_Not_Throw()
    {
        var handler = new RecordingHttpMessageHandler(_ => throw new HttpRequestException("net down"));
        var transport = NewTransport(handler);

        var updates = await transport.PollUpdatesAsync(offset: 0, default);

        Assert.Empty(updates);
    }

    [Fact]
    public async Task Blank_Token_Send_Returns_Failure_Without_Calling_Network()
    {
        // No blob on the test host → blank token resolves to null → the send
        // short-circuits to a failure and never touches the handler.
        if (File.Exists(ChatTokenResolver.BlobPath()))
        {
            return; // machine has a real token blob; skip.
        }

        var handler = new RecordingHttpMessageHandler(_ => throw new HttpRequestException("should not be called"));
        var transport = NewTransport(handler, token: "   ");

        var result = await transport.SendAsync("1", "x", buttons: null, default);

        Assert.False(result.Ok);
        Assert.Empty(handler.Urls);
    }
}
