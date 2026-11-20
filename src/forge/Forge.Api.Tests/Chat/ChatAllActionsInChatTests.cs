// -----------------------------------------------------------------------
// <copyright file="ChatAllActionsInChatTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using Common.Chat;
using Forge.Api.Persistence;
using Forge.Api.Services.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// "All actions can be performed within the chat app" (2026-11-19): a halted story alerts
/// with Retry / Restart doc / Pin decision, questions can be dismissed, and pinning asks
/// for the decision text in a reply. Real delivery + inbound dispatch, fake providers.
/// </summary>
public sealed class ChatAllActionsInChatTests
{
    private const string HaltReason = "plan doc 'architecture' failed: architecture.md is already at its iteration cap (65/50) with verdict=sloppy.";

    [Fact]
    public async Task Halted_Story_Alerts_With_Retry_Restart_And_Pin_And_Resolves_When_The_Error_Clears()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(interactive: true, questions: 0);
        await SetHaltedAsync(f, HaltReason);
        await Reconcile(f); await f.TickAsync();

        var sent = f.Alpha.Sent.Last();
        Assert.Equal("Forge · Story halted", sent.Notification.Title);
        Assert.Contains("iteration cap", sent.Notification.Body);
        Assert.Equal(new[] { "spawn.retry", "artifact.restart", "artifact.pin" }, sent.Notification.Actions.Select(a => a.Kind));
        Assert.Equal(new[] { "🔁 Retry", "⚠ Restart doc", "📌 Pin decision" }, sent.Notification.Actions.Select(a => a.Label));

        // Same error on the next pass: no duplicate alert.
        var count = f.Alpha.Sent.Count;
        await Reconcile(f); await f.TickAsync();
        Assert.Equal(count, f.Alpha.Sent.Count);

        // Retry from chat clears the error exactly like the banner's Retry.
        Assert.Equal("accepted", await PressAsync(f, sent, "spawn.retry", "retry"));
        Assert.Null(await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").StorySpawns.Select(s => s.LastError).SingleAsync()));

        // Error gone: the alert is resolved and its buttons retired.
        await Reconcile(f);
        var halted = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.AsNoTracking()
            .Where(n => n.Kind == ChatEventCatalog.SpawnHalted).ToListAsync());
        Assert.All(halted, n => Assert.Equal("resolved", n.Status));
        Assert.Equal(0, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatActionTokens
            .CountAsync(t => t.State == "active" && (t.Action == "spawn.retry" || t.Action == "artifact.pin"))));
    }

    [Fact]
    public async Task Pin_Decision_Asks_For_A_Reply_And_Pins_It_For_The_Halted_Document()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(interactive: true, questions: 0);
        await SetHaltedAsync(f, HaltReason);
        await Reconcile(f); await f.TickAsync();
        var alert = f.Alpha.Sent.Last();

        Assert.Equal("pin-prompt-queued", await PressAsync(f, alert, "artifact.pin", "pin"));
        await f.TickAsync();
        var prompt = f.Alpha.Sent.Last();
        Assert.Contains("decision to pin", prompt.Notification.Body);
        Assert.True(prompt.Notification.ForceReply);
        Assert.Empty(prompt.Notification.Actions);

        var reply = new ChatInboundEnvelope("FakeAlpha", "alpha-one", "pin-reply", prompt.Address, "operator", "text", "pin-reply-msg",
            prompt.MessageId, "DefaultAction lives in the sidecar. Stop flipping.", null, f.Clock.GetUtcNow());
        await f.ReceiveAsync(reply); await f.WithAsync(sp => sp.GetRequiredService<ChatInboxService>().DispatchOnceAsync());
        Assert.Equal("pinned", await Result(f, "pin-reply"));

        var pins = await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").StorySpawns.Select(s => s.DecisionPinsJson).SingleAsync());
        Assert.Contains("DefaultAction lives in the sidecar", pins);
        Assert.Contains("plan-doc:shared:architecture.md", pins);
    }

    [Fact]
    public async Task Open_Question_Offers_Dismiss_And_Dismissing_Resolves_It()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(interactive: true);
        await f.EnqueueAsync(); await f.TickAsync();
        var question = f.Alpha.Sent.Last();
        Assert.Contains(question.Notification.Actions, a => a.Kind == "question.dismiss" && a.Label == "Dismiss");
        Assert.DoesNotContain(question.Notification.Actions, a => a.Kind == "artifact.pin"); // a workstream question has no plan doc

        Assert.Equal("dismissed", await PressAsync(f, question, "question.dismiss", "dismiss"));
        var open = await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").DeliverableQuestions.AsNoTracking()
            .Where(q => q.WorkItemId == "child-0").ToListAsync());
        Assert.DoesNotContain(open, q => string.IsNullOrEmpty(q.Answer));
    }

    [Fact]
    public void Plan_Doc_Halts_Name_Their_Document()
    {
        Assert.Equal("architecture", ChatScopeReconciler.HaltedArtifactKey(HaltReason));
        Assert.Null(ChatScopeReconciler.HaltedArtifactKey("implement failed: merge conflict"));
        Assert.Null(ChatScopeReconciler.HaltedArtifactKey("plan doc 'unknown-doc' failed: x"));
    }

    private static Task SetHaltedAsync(ChatRuntimeTestFactory f, string reason) => f.WithAsync(async sp =>
    {
        var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p");
        var spawn = await db.StorySpawns.SingleAsync(s => s.Id == "shared");
        spawn.LastError = reason; spawn.LastErrorAt = f.Clock.GetUtcNow();
        await db.SaveChangesAsync();
    });

    private static Task Reconcile(ChatRuntimeTestFactory f) => f.WithAsync(sp => sp.GetRequiredService<ChatScopeReconciler>().RunOnceAsync());

    private static async Task<string?> PressAsync(ChatRuntimeTestFactory f,
        (ChatAddress Address, ChatSemanticNotification Notification, string MessageId) sent, string kind, string key)
    {
        var token = sent.Notification.Actions.Single(a => a.Kind == kind).Token;
        var envelope = new ChatInboundEnvelope("FakeAlpha", "alpha-one", key, sent.Address, "operator", "action", "incoming-" + key, sent.MessageId, null, token, f.Clock.GetUtcNow());
        await f.ReceiveAsync(envelope); await f.WithAsync(sp => sp.GetRequiredService<ChatInboxService>().DispatchOnceAsync());
        return await Result(f, key);
    }

    private static Task<string?> Result(ChatRuntimeTestFactory f, string key)
        => f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.Where(i => i.DeliveryKey == key).Select(i => i.Result).SingleAsync());
}