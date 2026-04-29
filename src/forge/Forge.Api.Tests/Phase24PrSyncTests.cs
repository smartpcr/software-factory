// -----------------------------------------------------------------------
// <copyright file="Phase24PrSyncTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 24 — PR sync + comment lifecycle tests.
/// </summary>
public class Phase24PrSyncTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static PrRow SeedPr(ForgeDbContext db, string id)
    {
        var pr = new PrRow
        {
            Id = id,
            Col = "open",
            Epic = "E1",
            EpicColor = "blue",
            Workstream = "ws/test",
            Base = "main",
            Branch = "ws/test",
            Title = "Test PR",
        };
        db.Prs.Add(pr);
        db.SaveChanges();
        return pr;
    }

    // ── 1. Migration ──────────────────────────────────────────────────────

    [Fact]
    public void Phase24Migration_Runs_Without_Error()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* trigger boot + migration */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        Assert.NotNull(db);
    }

    [Fact]
    public async Task PrComments_And_History_Tables_Exist_After_Boot()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        await using var cmd1 = conn.CreateCommand();
        cmd1.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='pr_comments';";
        Assert.Equal("pr_comments", await cmd1.ExecuteScalarAsync());

        await using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='pr_comment_history';";
        Assert.Equal("pr_comment_history", await cmd2.ExecuteScalarAsync());
    }

    [Fact]
    public async Task PrRow_New_Columns_Have_Correct_Defaults_After_Boot()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-defaults");

        using var scope2 = f.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var row = await db2.Prs.AsNoTracking().FirstAsync(p => p.Id == "p24-defaults");
        Assert.True(row.IsActive);
        Assert.Equal("idle", row.LastSyncStatus);
    }

    // ── 2. PR State Mapping ───────────────────────────────────────────────

    [Theory]
    [InlineData("ado", "Active", "open")]
    [InlineData("ado", "Completed", "completed")]
    [InlineData("ado", "Abandoned", "abandoned")]
    [InlineData("github", "open", "open")]
    [InlineData("github", "merged", "completed")]
    [InlineData("github", "completed", "completed")]
    [InlineData("github", "closed", "abandoned")]
    public void MapUpstreamState_Returns_Expected(string provider, string upstream, string expected)
    {
        var result = PrSyncJob.MapUpstreamState(provider, upstream);
        Assert.Equal(expected, result);
    }

    // ── 3. Comment CRUD ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateComment_Returns_Created_With_Comment()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-crud");

        var resp = await client.PostAsJsonAsync("/api/prs/p24-crud/comments", new { body = "Hello world" }, JsonOpts);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("Hello world", doc.RootElement.GetProperty("body").GetString());
        Assert.Equal("p24-crud", doc.RootElement.GetProperty("prId").GetString());
    }

    [Fact]
    public async Task ListComments_Returns_Comments_For_PR()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-list");

        await client.PostAsJsonAsync("/api/prs/p24-list/comments", new { body = "Comment 1" }, JsonOpts);
        await client.PostAsJsonAsync("/api/prs/p24-list/comments", new { body = "Comment 2" }, JsonOpts);

        var resp = await client.GetAsync("/api/prs/p24-list/comments");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetComment_Returns_Comment_With_History()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-get");

        var createResp = await client.PostAsJsonAsync("/api/prs/p24-get/comments", new { body = "Get me" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        var resp = await client.GetAsync($"/api/prs/p24-get/comments/{commentId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("Get me", doc.RootElement.GetProperty("comment").GetProperty("body").GetString());
        Assert.True(doc.RootElement.TryGetProperty("history", out _));
    }

    [Fact]
    public async Task EditComment_Updates_Body()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-edit");

        var createResp = await client.PostAsJsonAsync("/api/prs/p24-edit/comments", new { body = "Original" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/prs/p24-edit/comments/{commentId}");
        req.Content = JsonContent.Create(new { body = "Updated" }, options: JsonOpts);
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("Updated", doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Comments_Are_Isolated_Between_PRs()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-iso-1");
        SeedPr(db, "p24-iso-2");

        await client.PostAsJsonAsync("/api/prs/p24-iso-1/comments", new { body = "PR1 only" }, JsonOpts);
        await client.PostAsJsonAsync("/api/prs/p24-iso-2/comments", new { body = "PR2 only" }, JsonOpts);

        var resp1 = await client.GetAsync("/api/prs/p24-iso-1/comments");
        var doc1 = JsonDocument.Parse(await resp1.Content.ReadAsStringAsync());
        Assert.Equal(1, doc1.RootElement.GetProperty("total").GetInt32());

        var resp2 = await client.GetAsync("/api/prs/p24-iso-2/comments");
        var doc2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync());
        Assert.Equal(1, doc2.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task CreateComment_On_NonExistent_PR_Returns_404()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        var resp = await client.PostAsJsonAsync("/api/prs/p24-nonexistent/comments", new { body = "Fail" }, JsonOpts);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── 4. Authority Enforcement (Q24.5) ──────────────────────────────────

    [Fact]
    public async Task Resolve_By_Agent_Returns_403()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-auth-res");

        var createResp = await client.PostAsJsonAsync("/api/prs/p24-auth-res/comments", new { body = "Auth test" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        using var agentClient = f.CreateClient();
        agentClient.DefaultRequestHeaders.Add("X-Forge-User", "agent:bot1");
        var resp = await agentClient.PostAsync($"/api/prs/p24-auth-res/comments/{commentId}/resolve", null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Resolve_By_Human_Returns_200_And_Sets_Resolved()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-resolve");

        var createResp = await client.PostAsJsonAsync("/api/prs/p24-resolve/comments", new { body = "Resolve me" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        var resp = await client.PostAsync($"/api/prs/p24-resolve/comments/{commentId}/resolve", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("resolved", doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Reopen_By_Agent_Returns_403()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-auth-reo");

        var createResp = await client.PostAsJsonAsync("/api/prs/p24-auth-reo/comments", new { body = "Reopen auth" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        await client.PostAsync($"/api/prs/p24-auth-reo/comments/{commentId}/resolve", null);

        using var agentClient = f.CreateClient();
        agentClient.DefaultRequestHeaders.Add("X-Forge-User", "agent:bot1");
        var resp = await agentClient.PostAsync($"/api/prs/p24-auth-reo/comments/{commentId}/reopen", null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Reopen_By_Human_Returns_200_And_Sets_Active()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-reopen");

        var createResp = await client.PostAsJsonAsync("/api/prs/p24-reopen/comments", new { body = "Reopen me" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        await client.PostAsync($"/api/prs/p24-reopen/comments/{commentId}/resolve", null);
        var resp = await client.PostAsync($"/api/prs/p24-reopen/comments/{commentId}/reopen", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("active", doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Resolve_Already_Resolved_Is_NoOp_No_Duplicate_History()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-noop-res");

        var createResp = await client.PostAsJsonAsync("/api/prs/p24-noop-res/comments", new { body = "NoOp" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        await client.PostAsync($"/api/prs/p24-noop-res/comments/{commentId}/resolve", null);
        var resp = await client.PostAsync($"/api/prs/p24-noop-res/comments/{commentId}/resolve", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope2 = f.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var resolvedCount = await db2.PrCommentHistory.CountAsync(
            h => h.CommentId == commentId && h.Kind == "resolved");
        Assert.Equal(1, resolvedCount);
    }

    [Fact]
    public async Task Reopen_Already_Active_Is_NoOp_No_Duplicate_History()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-noop-reo");

        var createResp = await client.PostAsJsonAsync("/api/prs/p24-noop-reo/comments", new { body = "NoOp reopen" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        var resp = await client.PostAsync($"/api/prs/p24-noop-reo/comments/{commentId}/reopen", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope2 = f.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var reopenedCount = await db2.PrCommentHistory.CountAsync(
            h => h.CommentId == commentId && h.Kind == "reopened");
        Assert.Equal(0, reopenedCount);
    }

    // ── 5. RequiresAttention ──────────────────────────────────────────────

    [Fact]
    public async Task RequiresAttention_Persists_When_Set_On_Human_Active_Comment()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-attn");

        var comment = new PrCommentRow
        {
            PrId = "p24-attn",
            ProviderCommentId = $"local-{Guid.NewGuid():N}",
            ThreadId = Guid.NewGuid().ToString(),
            AuthorKind = "human",
            Body = "Needs attention",
            State = "active",
            RequiresAttention = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.PrComments.Add(comment);
        await db.SaveChangesAsync();

        using var scope2 = f.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var read = await db2.PrComments.AsNoTracking().FirstAsync(c => c.Id == comment.Id);
        Assert.True(read.RequiresAttention);
        Assert.Equal("human", read.AuthorKind);
        Assert.Equal("active", read.State);
    }

    [Fact]
    public async Task Resolving_Clears_RequiresAttention()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-clr-attn");

        var createResp = await client.PostAsJsonAsync(
            "/api/prs/p24-clr-attn/comments", new { body = "Attn" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        // Manually flag RequiresAttention = true so resolve can clear it.
        var comment = await db.PrComments.FirstAsync(c => c.Id == commentId);
        comment.RequiresAttention = true;
        await db.SaveChangesAsync();

        var resp = await client.PostAsync($"/api/prs/p24-clr-attn/comments/{commentId}/resolve", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("requiresAttention").GetBoolean());
    }

    [Fact]
    public async Task Reopening_Sets_RequiresAttention_True()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-reo-attn");

        var createResp = await client.PostAsJsonAsync(
            "/api/prs/p24-reo-attn/comments", new { body = "Attn reopen" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        await client.PostAsync($"/api/prs/p24-reo-attn/comments/{commentId}/resolve", null);
        var resp = await client.PostAsync($"/api/prs/p24-reo-attn/comments/{commentId}/reopen", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("requiresAttention").GetBoolean());
    }

    // ── 6. Comment History ────────────────────────────────────────────────

    [Fact]
    public async Task Creating_Comment_Adds_Opened_History_Row()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-hist-cre");

        var createResp = await client.PostAsJsonAsync(
            "/api/prs/p24-hist-cre/comments", new { body = "History" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        using var scope2 = f.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var history = await db2.PrCommentHistory
            .Where(h => h.CommentId == commentId)
            .ToListAsync();

        Assert.Single(history);
        Assert.Equal("opened", history[0].Kind);
    }

    [Fact]
    public async Task Resolving_Adds_Resolved_History_Row()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-hist-res");

        var createResp = await client.PostAsJsonAsync(
            "/api/prs/p24-hist-res/comments", new { body = "Resolve" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        await client.PostAsync($"/api/prs/p24-hist-res/comments/{commentId}/resolve", null);

        using var scope2 = f.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var resolved = await db2.PrCommentHistory
            .Where(h => h.CommentId == commentId && h.Kind == "resolved")
            .ToListAsync();

        Assert.Single(resolved);
    }

    [Fact]
    public async Task Reopening_Adds_Reopened_History_Row()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-hist-reo");

        var createResp = await client.PostAsJsonAsync(
            "/api/prs/p24-hist-reo/comments", new { body = "Reopen" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        await client.PostAsync($"/api/prs/p24-hist-reo/comments/{commentId}/resolve", null);
        await client.PostAsync($"/api/prs/p24-hist-reo/comments/{commentId}/reopen", null);

        using var scope2 = f.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var reopened = await db2.PrCommentHistory
            .Where(h => h.CommentId == commentId && h.Kind == "reopened")
            .ToListAsync();

        Assert.Single(reopened);
    }

    [Fact]
    public async Task Editing_Adds_Edited_History_Row()
    {
        using var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "human@test.com");

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        SeedPr(db, "p24-hist-edt");

        var createResp = await client.PostAsJsonAsync(
            "/api/prs/p24-hist-edt/comments", new { body = "Original" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/prs/p24-hist-edt/comments/{commentId}");
        req.Content = JsonContent.Create(new { body = "Edited" }, options: JsonOpts);
        await client.SendAsync(req);

        using var scope2 = f.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var edited = await db2.PrCommentHistory
            .Where(h => h.CommentId == commentId && h.Kind == "edited")
            .ToListAsync();

        Assert.Single(edited);
    }

    // ── 7. Constants ──────────────────────────────────────────────────────

    [Fact]
    public void SyncConstants_PrSyncIntervalSeconds_Is_1200() =>
        Assert.Equal(1200, SyncConstants.PrSyncIntervalSeconds);

    [Fact]
    public void SyncConstants_PrSyncMaxParallel_Is_8() =>
        Assert.Equal(8, SyncConstants.PrSyncMaxParallel);

    [Fact]
    public void SyncConstants_PrCommentPageSize_Is_100() =>
        Assert.Equal(100, SyncConstants.PrCommentPageSize);

    // ── 8. PrRow Defaults ─────────────────────────────────────────────────

    [Fact]
    public void PrRow_Default_IsActive_Is_True()
    {
        var row = new PrRow();
        Assert.True(row.IsActive);
    }

    [Fact]
    public void PrRow_Default_LastSyncStatus_Is_Idle()
    {
        var row = new PrRow();
        Assert.Equal("idle", row.LastSyncStatus);
    }

    // ── 9. Worktree Cleanup Path ──────────────────────────────────────────

    [Fact]
    public void Worktree_Cleanup_Path_Follows_Convention()
    {
        var cloneRoot = @"E:\repos\my-project";
        var workItemId = "42";
        var path = Path.Combine(cloneRoot, ".worktree", $"ws-{workItemId}");
        Assert.Equal(@"E:\repos\my-project\.worktree\ws-42", path);
    }
}
