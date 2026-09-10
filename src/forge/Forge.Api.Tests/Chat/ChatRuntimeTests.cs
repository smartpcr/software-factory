// -----------------------------------------------------------------------
// <copyright file="ChatRuntimeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests.Chat;

using System.Text.Json;
using Common.Chat;
using Forge.Api.Auth;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Services.Chat;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class ChatRuntimeTests
{
    [Fact]
    public async Task Fanout_retries_only_failed_destination_and_keeps_successful_copy()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync();
        f.Beta.Failures["beta"] = new(ChatFailureKind.RateLimited, "unsafe token=never-store", TimeSpan.FromMinutes(1));
        await f.TickAsync();
        Assert.Equal(new[] { "sent", "retry" }, (await f.DeliveriesAsync()).Select(d => d.State));
        await f.TickAsync(); Assert.Equal(1, f.Alpha.SendCalls); Assert.Equal(1, f.Beta.SendCalls);
        f.Clock.Advance(TimeSpan.FromMinutes(1)); await f.TickAsync();
        Assert.Equal(1, f.Alpha.SendCalls); Assert.Equal(2, f.Beta.SendCalls);
        Assert.All(await f.DeliveriesAsync(), d => Assert.Equal("sent", d.State));
        Assert.False(await f.EnqueueAsync());
    }

    [Fact]
    public async Task Successful_send_checkpoint_failure_retries_only_local_storage()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync();
        f.Fault.Armed = "send"; await f.TickAsync();
        Assert.Equal(1, f.Alpha.SendCalls);
        Assert.Null(f.Fault.Armed);
        Assert.Contains((await f.DeliveriesAsync())[0].State, new[] { "sending", "sent" });
        await f.TickAsync(); Assert.Equal(1, f.Alpha.SendCalls);
        Assert.All(await f.DeliveriesAsync(), d => Assert.Equal("sent", d.State));
    }

    [Fact]
    public async Task Claim_failure_prevents_send_and_crash_does_not_replay_unknown_send()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync();
        f.Fault.Armed = "claim"; await f.TickAsync(); Assert.Equal(0, f.Alpha.SendCalls);
        await f.WithAsync(async sp =>
        {
            var db = sp.GetRequiredService<ControlDbContext>(); var delivery = await db.ChatDeliveries.SingleAsync(d => d.ConnectionId == "alpha-one");
            delivery.State = "sending"; delivery.OwnerProcessInstanceId = "dead-process";
            var part = await db.ChatDeliveryMessages.SingleAsync(m => m.DeliveryId == delivery.Id);
            part.State = "sending"; part.OwnerProcessInstanceId = "dead-process"; part.RecoveryDeadline = f.Clock.GetUtcNow().AddSeconds(60).ToUnixTimeMilliseconds();
            await db.SaveChangesAsync();
        });
        await f.TickAsync(); Assert.Equal(0, f.Alpha.SendCalls); Assert.Equal("uncertain", (await f.DeliveriesAsync())[0].State);
    }

    [Fact]
    public async Task Unknown_topic_creation_is_not_repeated()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(conversation: "topic"); await f.EnqueueAsync();
        f.Alpha.UncertainProvision = true;
        await f.TickAsync(); await f.TickAsync(); f.Clock.Advance(TimeSpan.FromMinutes(2)); await f.TickAsync();
        Assert.Equal(1, f.Alpha.ProvisionCalls); Assert.Equal(0, f.Alpha.SendCalls);
        Assert.Equal("uncertain", (await f.DeliveriesAsync())[0].State);
    }

    [Fact]
    public async Task Inbox_receipt_and_cursor_precede_ack_and_domain_work()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(interactive: true); await f.EnqueueAsync(); await f.TickAsync();
        var envelope = f.Answer(f.Alpha, "alpha-one", "delivery-1"); f.Fault.Armed = "inbox";
        Assert.Equal(ChatInboundAcceptance.Retry, await f.ReceiveAsync(envelope, "42"));
        Assert.Equal(0, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.CountAsync()));
        Assert.Equal(ChatInboundAcceptance.Accepted, await f.ReceiveAsync(envelope, "42"));
        Assert.Equal(ChatInboundAcceptance.Duplicate, await f.ReceiveAsync(envelope, "42"));
        Assert.Equal("42", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConnectionStates.Where(s => s.ConnectionId == "alpha-one").Select(s => s.Cursor).SingleAsync()));
        Assert.Null(await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").DeliverableQuestions.Select(q => q.Answer).SingleAsync()));
        Assert.Single(await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.ToListAsync()));
    }

    [Fact]
    public async Task Concurrent_provider_answers_commit_one_receipt_and_leave_other_questions_open()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(interactive: true, questions: 2); await f.EnqueueAsync(); await f.TickAsync(); await f.TickAsync();
        var alpha = f.Answer(f.Alpha, "alpha-one", "alpha-event");
        var beta = f.Answer(f.Beta, "beta", "beta-event");
        // Both providers deliver independently. Their normalized receipt keys are distinct; the domain key is shared.
        await Task.WhenAll(f.ReceiveAsync(alpha), f.ReceiveAsync(beta));
        await f.WithAsync(sp => sp.GetRequiredService<ChatInboxService>().DispatchOnceAsync());
        var results = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.Select(i => i.Result).ToListAsync());
        Assert.Contains("answered-more-remain", results); Assert.Contains("already-resolved", results);
        Assert.Equal(1, await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.CountAsync(r => r.Action == "question.answer")));
        await f.TickAsync();
        var parts = await f.MessagesAsync(); Assert.Equal(2, parts.Count(m => m.ControlsRetired)); Assert.Equal(2, parts.Count(m => !m.ControlsRetired));
        Assert.Equal(1, f.Alpha.Edits.Count); Assert.Equal(1, f.Beta.Edits.Count);
        Assert.All(f.Alpha.Edits.Concat(f.Beta.Edits), m => Assert.Empty(m.Actions));
    }

    [Fact]
    public async Task Mutation_before_control_checkpoint_recovers_from_product_receipt()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(interactive: true, questions: 2); await f.EnqueueAsync(); await f.TickAsync();
        await f.ReceiveAsync(f.Answer(f.Alpha, "alpha-one", "event")); f.Fault.Armed = "dispatch";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.WithAsync(sp => sp.GetRequiredService<ChatInboxService>().DispatchOnceAsync()));
        await f.WithAsync(sp => sp.GetRequiredService<ChatInboxService>().DispatchOnceAsync());
        Assert.Equal(1, await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.CountAsync(r => r.Action == "question.answer")));
        Assert.Equal("already-resolved", await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatInbox.Select(i => i.Result).SingleAsync()));
        await f.TickAsync(); Assert.NotEmpty(f.Alpha.Edits); Assert.NotEmpty(f.Beta.Edits);
    }

    [Fact]
    public async Task Web_answer_is_reconciled_to_all_provider_copies()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(interactive: true, questions: 2); await f.EnqueueAsync(); await f.TickAsync();
        var question = f.Alpha.Sent.Single().Notification.QuestionReference!;
        var actor = await f.ProviderActorAsync("123", "owner-p");
        using var actorScope = ForgeActorScope.Push(actor, "p"); using var productScope = ProductDbContextFactory.PushAsyncLocalSlug("p");
        await f.WithAsync(sp => sp.GetRequiredService<IOpenQuestionService>().AnswerAsync("child-0", [new(question, "web answer")], "owner-p", default));
        await f.TickAsync();
        Assert.Single(f.Alpha.Edits); Assert.Single(f.Beta.Edits);
        Assert.Equal(2, (await f.MessagesAsync()).Count(m => !m.ControlsRetired));
    }

    [Fact]
    public async Task Disabling_subscription_cancels_queued_copies_and_retires_sent_controls()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(interactive: true); await f.EnqueueAsync(); await f.TickAsync();
        var stale = f.Answer(f.Alpha, "alpha-one", "stale");
        var parent = await f.RunAsync("owner-p", (s, a) => s.GetPluginAsync(a, "p", "shared"));
        await f.RunAsync("owner-p", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Spawn("p", "shared"), "sub-alpha-one",
            new(1, parent.Revision, "dest-alpha-one", "flat", Paused: true)));
        await f.ReceiveAsync(stale); await f.WithAsync(sp => sp.GetRequiredService<ChatInboxService>().DispatchOnceAsync()); await f.TickAsync();
        Assert.Equal("cancelled", (await f.DeliveriesAsync())[0].State); Assert.Single(f.Alpha.Edits);
        Assert.Equal(0, await f.ReadAsync(sp => sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p").ChatActionReceipts.CountAsync()));
    }

    [Fact]
    public async Task Event_and_fanout_rollback_together_and_wrong_child_scope_is_visible()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(); f.Fault.Armed = "event";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.EnqueueAsync()); Assert.Empty(await f.DeliveriesAsync());
        Assert.Equal(0, await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.CountAsync(n => n.Platform == "plugins")));
        await f.ReadAsync(sp => sp.GetRequiredService<ChatEventService>().EnqueueAsync(new("open-questions", "wrong", "p", "shared", "missing-child")));
        var row = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.SingleAsync(n => n.Platform == "plugins"));
        Assert.Equal("host", row.ScopeKind); Assert.Equal("unresolved-scope", row.RouteReason); Assert.Empty(await f.DeliveriesAsync());
    }

    [Fact]
    public async Task Planning_questions_route_before_child_creation_only_with_persisted_ownership()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync();
        const string WorkItem = "plan-doc:shared:architecture.md";
        await f.WithAsync(async sp => { var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p"); db.DeliverableQuestions.Add(new() { WorkItemId = WorkItem, ExternalId = "plan-q", Text = "Plan", Iter = 1 }); await db.SaveChangesAsync(); });
        await f.ReadAsync(sp => sp.GetRequiredService<ChatEventService>().EnqueueAsync(new("open-questions", "plan", "p", "shared", WorkItem)));
        Assert.Equal(2, (await f.DeliveriesAsync()).Count);
        var row = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.SingleAsync(n => n.Platform == "plugins"));
        Assert.Equal("architecture", row.ArtifactKey); Assert.Null(row.WorkstreamId);
    }

    [Fact]
    public async Task New_revision_supersedes_blocking_retry_and_old_tokens()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(interactive: true); await f.EnqueueAsync();
        f.Alpha.Failures["alpha-one"] = new(ChatFailureKind.Unavailable, "temporary", TimeSpan.FromMinutes(5)); await f.TickAsync();
        await f.EnqueueAsync("new", 2); await f.TickAsync();
        Assert.Equal(2, f.Alpha.SendCalls);
        Assert.Contains(await f.DeliveriesAsync(), d => d.ConnectionId == "alpha-one" && d.State == "cancelled");
    }

    [Fact]
    public async Task Host_route_is_permanent_and_unknown_kinds_do_not_match_old_filters()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync();
        await f.RunAsync("admin", (s, a) => s.PutSubscriptionAsync(a, ChatConfigurationScope.Host("test-host"), "host-beta", new(0, 0, "dest-beta", "flat")));
        await f.ReadAsync(sp => sp.GetRequiredService<ChatEventService>().EnqueueAsync(new("queue-strain", "queue", "q", "shared")));
        await f.ReadAsync(sp => sp.GetRequiredService<ChatEventService>().EnqueueAsync(new("future-kind", "future")));
        var rows = await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.Where(n => n.Platform == "plugins").ToListAsync());
        Assert.All(rows, n => { Assert.Equal("host", n.ScopeKind); Assert.Equal("test-host", n.HostId); });
        Assert.Equal("unsupported-kind", rows.Single(n => n.Kind == "future-kind").RouteReason);
        Assert.Single(await f.DeliveriesAsync());
    }

    [Fact]
    public async Task Reconciler_distinguishes_missing_parent_from_unavailable_product_database()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync();
        await f.WithAsync(async sp => { var db = sp.GetRequiredService<IProductDbContextFactory>().CreateFor("p"); await db.StorySpawns.ExecuteDeleteAsync(); });
        await f.WithAsync(sp => sp.GetRequiredService<ChatScopeReconciler>().RunOnceAsync());
        Assert.All(await f.DeliveriesAsync(), d => Assert.Equal("cancelled", d.State));
        Assert.All(await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatConversationBindings.ToListAsync()), b => Assert.Equal("orphaned", b.ProvisioningState));
    }

    [Fact]
    public async Task Retention_scrubs_content_but_keeps_unknown_external_operations()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync(); await f.EnqueueAsync();
        f.Alpha.Failures["alpha-one"] = new(ChatFailureKind.UnknownOutcome, "token=secret"); await f.TickAsync();
        f.Clock.Advance(TimeSpan.FromDays(91)); await f.WithAsync(sp => sp.GetRequiredService<ChatRetentionService>().RunOnceAsync());
        Assert.Contains(await f.DeliveriesAsync(), d => d.State == "uncertain");
        Assert.All(await f.ReadAsync(sp => sp.GetRequiredService<ControlDbContext>().ChatNotifications.Where(n => n.Platform == "plugins").ToListAsync()), n => { Assert.Empty(n.Body); Assert.Null(n.PayloadJson); });
        Assert.All(await f.MessagesAsync(), m => Assert.Null(m.RenderedJson));
    }

    [Fact]
    public async Task Supervisor_cancels_changed_revision_restores_cursor_and_coalesces_health()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync();
        var supervisor = f.Services.GetRequiredService<ChatConnectionSupervisor>();
        await supervisor.RefreshAsync(default); await Task.Delay(30);
        var original = f.Alpha.Sessions["alpha-one"];
        Assert.Equal(1, original.ReceiveCalls);
        var runtime = f.Services.GetRequiredService<ChatRuntimeState>(); var writes = runtime.CompletedWrites;
        await supervisor.RefreshAsync(default); Assert.Equal(writes, runtime.CompletedWrites);
        await f.WithAsync(async sp => { var db = sp.GetRequiredService<ControlDbContext>(); var c = await db.ChatConnections.SingleAsync(c => c.Id == "alpha-one"); c.Revision++; await db.SaveChangesAsync(); });
        await supervisor.RefreshAsync(default); Assert.True(original.Disposed); Assert.NotSame(original, f.Alpha.Sessions["alpha-one"]);
        await supervisor.DisposeAsync();
    }

    [Fact]
    public async Task Named_host_mutex_excludes_second_runtime_even_after_sixty_seconds()
    {
        await using var f = new ChatRuntimeTestFactory(); await f.SetupAsync();
        var first = f.Services.GetRequiredService<ChatRuntimeState>(); Assert.True(first.EnsureHostOwnership());
        using var second = new ChatRuntimeState(f.Services.GetRequiredService<IForgeDbPaths>());
        Assert.False(second.EnsureHostOwnership()); f.Clock.Advance(TimeSpan.FromMinutes(2)); Assert.False(second.EnsureHostOwnership());
    }

    [Fact]
    public void Legacy_payload_conversion_is_versioned_and_never_creates_current_action_authority()
    {
        var row = new ChatNotificationRow { Title = "Legacy", Body = "token=secret", ActionsJson = ChatActions.Serialize([new("Retry", "act|retry")]) };
        var payload = ChatPayloadConverter.Read(row)!; Assert.Equal(1, payload.Version); Assert.DoesNotContain("secret", payload.Body);
        Assert.Equal("legacy-reference", Assert.Single(payload.LegacyActions).Kind);
        row.PayloadVersion = 12; Assert.Null(ChatPayloadConverter.Read(row));
    }
}