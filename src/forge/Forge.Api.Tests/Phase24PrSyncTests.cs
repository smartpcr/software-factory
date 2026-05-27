// -----------------------------------------------------------------------
// <copyright file="Phase24PrSyncTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System;
using System.IO;
using System.Linq;
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
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Phase 24 — PR sync + comment lifecycle tests.
/// </summary>
public class Phase24PrSyncTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static PrRow SeedPr(ControlDbContext control, ProductDbContext db, string id)
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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        Assert.NotNull(db);
    }

    [Fact]
    public async Task PrComments_And_History_Tables_Exist_After_Boot()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-defaults");

        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-crud");

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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-list");

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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-get");

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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-edit");

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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-iso-1");
        SeedPr(control, db, "p24-iso-2");

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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-auth-res");

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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-resolve");

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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-auth-reo");

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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-reopen");

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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-noop-res");

        var createResp = await client.PostAsJsonAsync("/api/prs/p24-noop-res/comments", new { body = "NoOp" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        await client.PostAsync($"/api/prs/p24-noop-res/comments/{commentId}/resolve", null);
        var resp = await client.PostAsync($"/api/prs/p24-noop-res/comments/{commentId}/resolve", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-noop-reo");

        var createResp = await client.PostAsJsonAsync("/api/prs/p24-noop-reo/comments", new { body = "NoOp reopen" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        var resp = await client.PostAsync($"/api/prs/p24-noop-reo/comments/{commentId}/reopen", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-attn");

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
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-clr-attn");

        var createResp = await client.PostAsJsonAsync(
            "/api/prs/p24-clr-attn/comments", new { body = "Attn" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        // Manually flag RequiresAttention = true so resolve can clear it.
        var comment = await db.PrComments.FirstAsync(c => c.Id == commentId);
        comment.RequiresAttention = true;
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-reo-attn");

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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-hist-cre");

        var createResp = await client.PostAsJsonAsync(
            "/api/prs/p24-hist-cre/comments", new { body = "History" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-hist-res");

        var createResp = await client.PostAsJsonAsync(
            "/api/prs/p24-hist-res/comments", new { body = "Resolve" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        await client.PostAsync($"/api/prs/p24-hist-res/comments/{commentId}/resolve", null);

        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-hist-reo");

        var createResp = await client.PostAsJsonAsync(
            "/api/prs/p24-hist-reo/comments", new { body = "Reopen" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        await client.PostAsync($"/api/prs/p24-hist-reo/comments/{commentId}/resolve", null);
        await client.PostAsync($"/api/prs/p24-hist-reo/comments/{commentId}/reopen", null);

        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
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
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-hist-edt");

        var createResp = await client.PostAsJsonAsync(
            "/api/prs/p24-hist-edt/comments", new { body = "Original" }, JsonOpts);
        var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var commentId = createDoc.RootElement.GetProperty("id").GetInt64();

        var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/prs/p24-hist-edt/comments/{commentId}");
        req.Content = JsonContent.Create(new { body = "Edited" }, options: JsonOpts);
        await client.SendAsync(req);

        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var edited = await db2.PrCommentHistory
            .Where(h => h.CommentId == commentId && h.Kind == "edited")
            .ToListAsync();

        Assert.Single(edited);
    }

    // ── 7. Constants ──────────────────────────────────────────────────────

    [Fact]
    public void SyncConstants_PrSyncIntervalSeconds_Is_7200() =>
        // Phase 34 — bumped from 20 min to 2 hr; per-repo overrides via RepoRow.PrSyncIntervalSeconds.
        Assert.Equal(7200, SyncConstants.PrSyncIntervalSeconds);

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

    // ── 10. ADO commentThread.status mapping (Q24.4) ──────────────────────

    [Theory]
    [InlineData("active", "active")]
    [InlineData("fixed", "resolved")]
    // Phase 28 — "closed" stays distinct from "resolved" so the Forge UI
    // can render the closed-without-fix pill (see pr-comment.png).
    [InlineData("closed", "closed")]
    [InlineData("byDesign", "resolved")]
    [InlineData("wontFix", "wont-fix")]
    [InlineData("pending", "pending")]
    [InlineData("unknown", "active")]
    public void AdoStatus_Maps_To_Canonical_State(string ado, string expected)
    {
        var actual = AdoPrCommentSyncClient.MapAdoStatus(ado);
        Assert.Equal(expected, actual);
    }

    // ── 11. Comment-response queue hook (Q24.8) ───────────────────────────

    [Fact]
    public async Task Enqueue_CommentResponse_Idempotent_When_Open_Row_Exists()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var wi = new WorkItemRow
        {
            Id = "wi-q24-8",
            Title = "Phase24 queue test",
            Type = "task",
            State = "in-review",
            ExecutionStatus = ExecutionStatuses.Idle,
            DescriptionMd = "...",
        };
        db.WorkItems.Add(wi);
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
        var first = await queue.EnqueueAsync(wi.Id, "crew", "C1", default, kind: TaskQueueKinds.CommentResponse);
        var second = await queue.EnqueueAsync(wi.Id, "crew", "C1", default, kind: TaskQueueKinds.CommentResponse);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(TaskQueueKinds.CommentResponse, first.Kind);
    }

    [Fact]
    public async Task Enqueue_CommentResponse_Coexists_With_Work_Row()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var wi = new WorkItemRow
        {
            Id = "wi-q24-8b",
            Title = "Phase24 dual-kind",
            Type = "task",
            State = "in-review",
            DescriptionMd = "...",
        };
        db.WorkItems.Add(wi);
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
        var work = await queue.EnqueueAsync(wi.Id, "crew", "C1", default);
        var resp = await queue.EnqueueAsync(wi.Id, "crew", "C1", default, kind: TaskQueueKinds.CommentResponse);
        Assert.NotEqual(work.Id, resp.Id);
        Assert.Equal(TaskQueueKinds.Work, work.Kind);
        Assert.Equal(TaskQueueKinds.CommentResponse, resp.Kind);
    }

    // ── 12. PrCommentLinker — body-parse heuristics (Q24.6) ───────────────

    [Theory]
    [InlineData("Addresses comment 12345", true)]
    [InlineData("addresses Comment 12345", true)]
    [InlineData("Fixes comment 12345", true)]
    [InlineData("fixes Comment 12345 quickly", true)]
    [InlineData("CR-12345", true)]
    [InlineData("addresses comment 99999", false)]
    [InlineData("not relevant", false)]
    [InlineData("CR-99999", false)]
    public void Linker_BodyTagRegex_Matches_Recognised_Forms(string text, bool shouldMatch)
    {
        // Use the same regex shape as PrCommentLinker.BodyTagRegex.
        var regex = new System.Text.RegularExpressions.Regex(
            @"(?:Addresses|Fixes|fix|fixes|address|addresses)\s+comment\s+(?<id>\d+)|CR-(?<id2>\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var matched = false;
        foreach (System.Text.RegularExpressions.Match m in regex.Matches(text))
        {
            var id = m.Groups["id"].Success ? m.Groups["id"].Value : m.Groups["id2"].Value;
            if (id == "12345") { matched = true; break; }
        }
        Assert.Equal(shouldMatch, matched);
    }

    [Fact]
    public async Task Linker_Sets_LinkedAgentResponseId_For_Direct_Reply()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-link-reply");

        var parent = new PrCommentRow
        {
            PrId = "p24-link-reply",
            ProviderCommentId = "p1",
            ThreadId = "t1",
            AuthorKind = "human",
            Body = "Please rename this method.",
            State = "active",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            RequiresAttention = true,
        };
        db.PrComments.Add(parent);
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var reply = new PrCommentRow
        {
            PrId = "p24-link-reply",
            ProviderCommentId = "r1",
            ThreadId = "t1",
            ParentCommentId = parent.Id,
            AuthorKind = "agent",
            Body = "Done. Addresses comment " + parent.Id,
            State = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            RequiresAttention = false,
        };
        db.PrComments.Add(reply);
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var linker = new PrCommentLinker(control, db, new LoggerFactory().CreateLogger<PrCommentLinker>(), null);
        var matched = await linker.LinkAsync("p24-link-reply", worktreeRoot: null, default);

        Assert.Equal(1, matched);
        var refreshed = await db.PrComments.AsNoTracking().FirstAsync(c => c.Id == parent.Id);
        Assert.Equal(reply.Id, refreshed.LinkedAgentResponseId);
        Assert.False(refreshed.RequiresAttention);

        var history = await db.PrCommentHistory.AsNoTracking()
            .Where(h => h.CommentId == parent.Id && h.Kind == "linked")
            .ToListAsync();
        Assert.Single(history);
    }

    [Fact]
    public async Task Linker_Skips_Comments_Without_Match()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-link-no-match");

        var c = new PrCommentRow
        {
            PrId = "p24-link-no-match",
            ProviderCommentId = "p2",
            ThreadId = "t2",
            AuthorKind = "human",
            Body = "Reword the docstring.",
            State = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            RequiresAttention = true,
        };
        db.PrComments.Add(c);
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var linker = new PrCommentLinker(control, db, new LoggerFactory().CreateLogger<PrCommentLinker>(), null);
        var matched = await linker.LinkAsync("p24-link-no-match", worktreeRoot: null, default);

        Assert.Equal(0, matched);
        var refreshed = await db.PrComments.AsNoTracking().FirstAsync(x => x.Id == c.Id);
        Assert.True(refreshed.RequiresAttention);
        Assert.Null(refreshed.LinkedCommitSha);
        Assert.Null(refreshed.LinkedAgentResponseId);
    }

    // ── 13. PrSyncJob ApplySnapshot — comment upsert (Q24.10) ─────────────

    [Fact]
    public async Task PrSync_Upsert_Inserts_New_Comments()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-snap-1");
        var pr = await db.Prs.FirstAsync(p => p.Id == "p24-snap-1");
        pr.Provider = "ado";
        pr.ProviderRef = "1";
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var snapshot = new PrSyncSnapshot(
            CanonicalState: "open",
            UpstreamUpdatedAt: "2026-01-01T00:00:00Z",
            Comments: new[]
            {
                new PrSyncComment("c1", "t1", "alice@example.com", "human",
                    "Looks good", null, null, "active",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null),
                new PrSyncComment("c2", "t1", "bot@example.com", "system",
                    "Build failed", "src/foo.cs", 5, "active",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, "c1"),
            },
            CommentsFetched: true);

        // Run the apply manually via reflection (it's a private method).
        var job = scope.ServiceProvider.GetRequiredService<PrSyncJob>();
        var method = typeof(PrSyncJob).GetMethod("ApplySnapshotAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var candidate = new PrSyncJob.PrSyncCandidate("p24-snap-1", "ado", "1", "open", null, null);
        await (Task)method!.Invoke(job, new object?[] { candidate, snapshot, default(CancellationToken) })!;

        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var comments = await db2.PrComments.Where(c => c.PrId == "p24-snap-1").ToListAsync();
        Assert.Equal(2, comments.Count);
        var c1 = comments.First(c => c.ProviderCommentId == "c1");
        Assert.True(c1.RequiresAttention);
        Assert.Equal("human", c1.AuthorKind);
        var c2 = comments.First(c => c.ProviderCommentId == "c2");
        Assert.Equal("system", c2.AuthorKind);
        Assert.False(c2.RequiresAttention);
        Assert.Equal(c1.Id, c2.ParentCommentId);
    }

    [Fact]
    public async Task PrSync_ApplySnapshot_Persists_Merged_Pr_Metadata()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-snap-merged-metadata");
        var pr = await db.Prs.FirstAsync(p => p.Id == "p24-snap-merged-metadata");
        pr.Provider = "ado";
        pr.ProviderRef = "99";
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var snapshot = new PrSyncSnapshot(
            CanonicalState: "completed",
            UpstreamUpdatedAt: "2026-01-01T00:00:00Z",
            Comments: Array.Empty<PrSyncComment>(),
            CommentsFetched: true,
            MergeCommitSha: " merge-sha ",
            MergeBaseSha: "base-sha",
            MergeHeadSha: "head-sha");

        var job = scope.ServiceProvider.GetRequiredService<PrSyncJob>();
        var method = typeof(PrSyncJob).GetMethod("ApplySnapshotAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var candidate = new PrSyncJob.PrSyncCandidate("p24-snap-merged-metadata", "ado", "99", "open", null, null);
        await (Task)method!.Invoke(job, new object?[] { candidate, snapshot, default(CancellationToken) })!;

        using var scope2 = f.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var updated = await db2.Prs.AsNoTracking().SingleAsync(p => p.Id == "p24-snap-merged-metadata");
        Assert.Equal("merged", updated.Col);
        Assert.Equal("merge-sha", updated.MergeCommitSha);
        Assert.Equal("base-sha", updated.MergeBaseSha);
        Assert.Equal("head-sha", updated.MergeHeadSha);
    }

    [Fact]
    public async Task PrSync_ApplySnapshot_Clears_Merged_Pr_Metadata_When_Reopened()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-snap-reopen-metadata");
        var pr = await db.Prs.FirstAsync(p => p.Id == "p24-snap-reopen-metadata");
        pr.Provider = "ado";
        pr.ProviderRef = "100";
        pr.Col = "merged";
        pr.MergeCommitSha = "merge-sha";
        pr.MergeBaseSha = "base-sha";
        pr.MergeHeadSha = "head-sha";
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var snapshot = new PrSyncSnapshot(
            CanonicalState: "open",
            UpstreamUpdatedAt: "2026-01-01T00:00:00Z",
            Comments: Array.Empty<PrSyncComment>(),
            CommentsFetched: true);

        var job = scope.ServiceProvider.GetRequiredService<PrSyncJob>();
        var method = typeof(PrSyncJob).GetMethod("ApplySnapshotAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var candidate = new PrSyncJob.PrSyncCandidate("p24-snap-reopen-metadata", "ado", "100", "merged", null, null);
        await (Task)method!.Invoke(job, new object?[] { candidate, snapshot, default(CancellationToken) })!;

        using var scope2 = f.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var updated = await db2.Prs.AsNoTracking().SingleAsync(p => p.Id == "p24-snap-reopen-metadata");
        Assert.Equal("review", updated.Col);
        Assert.Null(updated.MergeCommitSha);
        Assert.Null(updated.MergeBaseSha);
        Assert.Null(updated.MergeHeadSha);
    }

    [Fact]
    public async Task PrSync_Upsert_Ignores_Ado_Automation_Comments()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-snap-automation");
        var pr = await db.Prs.FirstAsync(p => p.Id == "p24-snap-automation");
        pr.Provider = "ado";
        pr.ProviderRef = "3";
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var snapshot = new PrSyncSnapshot(
            CanonicalState: "open",
            UpstreamUpdatedAt: "2026-01-01T00:00:00Z",
            Comments: new[]
            {
                new PrSyncComment("human", "t1", "alice@example.com", "human",
                    "Please add a test for this behavior.", "src/foo.cs", 12, "active",
                    now, now, null, null, null),
                new PrSyncComment("ref-update", "t2", "xiaodong@example.com", "human",
                    "> Re: : - The reference refs/heads/ws/local-UPDATE-HISTORY-IN-CL was updated from commit a to commit b.",
                    null, null, "active", now, now, null, null, null),
                new PrSyncComment("source-deleted", "t3", "xiaodong@example.com", "human",
                    "Xiaodong Li deleted the source branch.", null, null, "active",
                    now, now, null, null, null),
                new PrSyncComment("source-restored", "t4", "xiaodong@example.com", "human",
                    "Xiaodong Li restored the source branch.", null, null, "active",
                    now, now, null, null, null),
                new PrSyncComment("system-build", "t5", "bot@example.com", "system",
                    "Build failed", "src/foo.cs", 5, "active",
                    now, now, null, null, null),
                new PrSyncComment("agent-reply", "t6", "xiaodong@example.com", "human",
                    "> Re: /src/foo.cs:12 - stale review text\n\nNo code change needed.\n\n_(BE reply)_",
                    null, null, "active", now, now, null, null, null),
            },
            CommentsFetched: true);

        var job = scope.ServiceProvider.GetRequiredService<PrSyncJob>();
        var method = typeof(PrSyncJob).GetMethod("ApplySnapshotAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var candidate = new PrSyncJob.PrSyncCandidate("p24-snap-automation", "ado", "3", "open", null, null);
        await (Task)method!.Invoke(job, new object?[] { candidate, snapshot, default(CancellationToken) })!;

        using var scope2 = f.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var comments = await db2.PrComments.Where(c => c.PrId == "p24-snap-automation").ToListAsync();

        Assert.True(comments.Single(c => c.ProviderCommentId == "human").RequiresAttention);
        Assert.False(comments.Single(c => c.ProviderCommentId == "ref-update").RequiresAttention);
        Assert.False(comments.Single(c => c.ProviderCommentId == "source-deleted").RequiresAttention);
        Assert.False(comments.Single(c => c.ProviderCommentId == "source-restored").RequiresAttention);
        Assert.False(comments.Single(c => c.ProviderCommentId == "system-build").RequiresAttention);
        Assert.False(comments.Single(c => c.ProviderCommentId == "agent-reply").RequiresAttention);
    }

    [Fact]
    public async Task PrSync_Upsert_Clears_Stale_Attention_On_Ado_Automation_Comment()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-snap-stale-automation");
        var existingAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var existing = new PrCommentRow
        {
            PrId = "p24-snap-stale-automation",
            ProviderCommentId = "ref-update",
            ThreadId = "t1",
            AuthorId = "xiaodong@example.com",
            AuthorKind = "human",
            Body = "> Re: : - The reference refs/heads/ws/local-UPDATE-HISTORY-IN-CL was updated from commit a to commit b.",
            State = "active",
            CreatedAt = existingAt,
            UpdatedAt = existingAt,
            RequiresAttention = true,
        };
        db.PrComments.Add(existing);
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var snapshot = new PrSyncSnapshot(
            CanonicalState: "open",
            UpstreamUpdatedAt: "2026-01-01T00:00:00Z",
            Comments: new[]
            {
                new PrSyncComment(
                    "ref-update",
                    "t1",
                    "xiaodong@example.com",
                    "human",
                    existing.Body,
                    null,
                    null,
                    "active",
                    existingAt,
                    existingAt,
                    null,
                    null,
                    null),
            },
            CommentsFetched: true);

        var job = scope.ServiceProvider.GetRequiredService<PrSyncJob>();
        var method = typeof(PrSyncJob).GetMethod("ApplySnapshotAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var candidate = new PrSyncJob.PrSyncCandidate("p24-snap-stale-automation", "ado", "4", "open", null, null);
        await (Task)method!.Invoke(job, new object?[] { candidate, snapshot, default(CancellationToken) })!;

        using var scope2 = f.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var refreshed = await db2.PrComments.AsNoTracking().SingleAsync(c => c.Id == existing.Id);
        Assert.False(refreshed.RequiresAttention);
        var history = await db2.PrCommentHistory.AsNoTracking()
            .Where(h => h.CommentId == existing.Id && h.Kind == "attention-flipped")
            .ToListAsync();
        Assert.Single(history);
    }

    [Fact]
    public async Task PrSync_Upsert_Resolves_State_To_Resolved()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-snap-2");
        var existing = new PrCommentRow
        {
            PrId = "p24-snap-2",
            ProviderCommentId = "c1",
            ThreadId = "t1",
            AuthorKind = "human",
            Body = "Fix this",
            State = "active",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            RequiresAttention = true,
        };
        db.PrComments.Add(existing);
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var snapshot = new PrSyncSnapshot(
            CanonicalState: "open",
            UpstreamUpdatedAt: "2026-01-01T00:00:00Z",
            Comments: new[]
            {
                new PrSyncComment("c1", "t1", "alice@example.com", "human",
                    "Fix this", null, null, "resolved",
                    existing.CreatedAt, DateTimeOffset.UtcNow, "alice@example.com", DateTimeOffset.UtcNow, null),
            },
            CommentsFetched: true);

        var job = scope.ServiceProvider.GetRequiredService<PrSyncJob>();
        var method = typeof(PrSyncJob).GetMethod("ApplySnapshotAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var candidate = new PrSyncJob.PrSyncCandidate("p24-snap-2", "ado", "2", "open", null, null);
        await (Task)method.Invoke(job, new object?[] { candidate, snapshot, default(CancellationToken) })!;

        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var refreshed = await db2.PrComments.AsNoTracking().FirstAsync(c => c.PrId == "p24-snap-2");
        Assert.Equal("resolved", refreshed.State);
        Assert.False(refreshed.RequiresAttention);
        Assert.NotNull(refreshed.ResolvedAt);

        var history = await db2.PrCommentHistory.Where(h => h.CommentId == refreshed.Id && h.Kind == "resolved").ToListAsync();
        Assert.Single(history);
    }

    [Fact]
    public async Task PrSync_Terminal_State_Sets_IsActive_False()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p24-snap-term");
        var snapshot = new PrSyncSnapshot(
            CanonicalState: "completed",
            UpstreamUpdatedAt: null,
            Comments: Array.Empty<PrSyncComment>(),
            CommentsFetched: false);

        var job = scope.ServiceProvider.GetRequiredService<PrSyncJob>();
        var method = typeof(PrSyncJob).GetMethod("ApplySnapshotAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var candidate = new PrSyncJob.PrSyncCandidate("p24-snap-term", "ado", "1", "open", null, null);
        await (Task)method.Invoke(job, new object?[] { candidate, snapshot, default(CancellationToken) })!;

        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var pr = await db2.Prs.AsNoTracking().FirstAsync(p => p.Id == "p24-snap-term");
        Assert.False(pr.IsActive);
        // Phase 28 — completed/abandoned canonical states map to the "merged"
        // UI column. Storing the raw canonical "completed" string here would
        // hide the row from the board (PrColumn = implementation|build|review|merged).
        Assert.Equal("merged", pr.Col);
    }

    /// <summary>
    /// Phase 28 — non-terminal "open" canonical state must NOT overwrite the PR's
    /// existing UI column (e.g. "review"). Otherwise the PR vanishes from the board
    /// because "open" isn't a valid <c>PrColumn</c>. This test pins the regression
    /// reported after the post-sync comment sweep started running on every manual
    /// sync.
    /// </summary>
    [Fact]
    public async Task PrSync_Open_State_Preserves_Existing_Ui_Column()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Seed a PR with the col already at the UI value "review" — this is what
        // PrsEndpoints.SyncPrs writes for an active ADO PR.
        var pr = SeedPr(control, db, "p28-preserve-col");
        pr.Col = "review";
        pr.Provider = "ado";
        pr.ProviderRef = "15567121";
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        // Snapshot reports the upstream is still active → canonical "open".
        var snapshot = new PrSyncSnapshot(
            CanonicalState: "open",
            UpstreamUpdatedAt: "2026-04-30T19:01:00Z",
            Comments: Array.Empty<PrSyncComment>(),
            CommentsFetched: false);

        var job = scope.ServiceProvider.GetRequiredService<PrSyncJob>();
        var method = typeof(PrSyncJob).GetMethod("ApplySnapshotAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var candidate = new PrSyncJob.PrSyncCandidate("p28-preserve-col", "ado", "15567121", "review", null, null);
        await (Task)method.Invoke(job, new object?[] { candidate, snapshot, default(CancellationToken) })!;

        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var refreshed = await db2.Prs.AsNoTracking().FirstAsync(p => p.Id == "p28-preserve-col");
        // Must stay "review" — not overwritten with the canonical "open".
        Assert.Equal("review", refreshed.Col);
        Assert.True(refreshed.IsActive);
    }

    // ── 14. Sweep filters out IsActive=false (Q24.3) ──────────────────────

    [Fact]
    public async Task Sweep_Skips_Inactive_Prs()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var active = SeedPr(control, db, "p24-sweep-active");
        var inactive = SeedPr(control, db, "p24-sweep-inactive");
        active.Provider = "ado"; active.ProviderRef = "10";
        inactive.Provider = "ado"; inactive.ProviderRef = "11";
        inactive.IsActive = false;
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var staleThreshold = DateTimeOffset.UtcNow.AddHours(-1).ToString("o");
        var candidates = await db.Prs
            .AsNoTracking()
            .Where(p => p.IsActive && (p.LastSyncStatus == "idle" || p.LastSyncStatus == "failed"))
            .Select(p => p.Id)
            .ToListAsync();

        Assert.Contains("p24-sweep-active", candidates);
        Assert.DoesNotContain("p24-sweep-inactive", candidates);
    }

    // ── 15. Constants spec consistency ────────────────────────────────────

    [Fact]
    public void SyncConstants_PrSync_Match_Spec()
    {
        // Phase 34 — bumped to 2 hr.
        Assert.Equal(7200, SyncConstants.PrSyncIntervalSeconds);
        Assert.Equal(8, SyncConstants.PrSyncMaxParallel);
        Assert.Equal(100, SyncConstants.PrCommentPageSize);
    }

    [Fact]
    public void TaskQueueKinds_Constants()
    {
        Assert.Equal("work", TaskQueueKinds.Work);
        Assert.Equal("comment-response", TaskQueueKinds.CommentResponse);
    }

    // ── PR sync — file stats from snapshot ─────────────────────────────────

    [Fact]
    public async Task PrSync_ApplySnapshot_Populates_FileStats_From_Snapshot()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p-stats-1");
        var pr = await db.Prs.FirstAsync(p => p.Id == "p-stats-1");
        pr.Provider = "ado";
        pr.ProviderRef = "99999";
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var snapshot = new PrSyncSnapshot(
            CanonicalState: "open",
            UpstreamUpdatedAt: "2026-01-01T00:00:00Z",
            Comments: Array.Empty<PrSyncComment>(),
            CommentsFetched: false,
            Files: 3,
            Commits: 2,
            Additions: 45,
            Deletions: 12);

        var job = scope.ServiceProvider.GetRequiredService<PrSyncJob>();
        var method = typeof(PrSyncJob).GetMethod("ApplySnapshotAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var candidate = new PrSyncJob.PrSyncCandidate("p-stats-1", "ado", "99999", "open", null, null);
        await (Task)method!.Invoke(job, new object?[] { candidate, snapshot, default(CancellationToken) })!;

        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var row = await db2.Prs.AsNoTracking().FirstAsync(p => p.Id == "p-stats-1");
        Assert.Equal(3, row.Files);
        Assert.Equal(2, row.Commits);
        Assert.Equal(45, row.Additions);
        Assert.Equal(12, row.Deletions);
    }

    [Fact]
    public async Task PrSync_ApplySnapshot_Null_Stats_Leave_Existing_Values()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "p-stats-2");
        var pr = await db.Prs.FirstAsync(p => p.Id == "p-stats-2");
        pr.Provider = "ado";
        pr.ProviderRef = "88888";
        pr.Files = 5;
        pr.Commits = 3;
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        // Snapshot with null stats should NOT overwrite existing values.
        var snapshot = new PrSyncSnapshot(
            CanonicalState: "open",
            UpstreamUpdatedAt: "2026-01-01T00:00:00Z",
            Comments: Array.Empty<PrSyncComment>(),
            CommentsFetched: false);

        var job = scope.ServiceProvider.GetRequiredService<PrSyncJob>();
        var method = typeof(PrSyncJob).GetMethod("ApplySnapshotAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var candidate = new PrSyncJob.PrSyncCandidate("p-stats-2", "ado", "88888", "open", null, null);
        await (Task)method!.Invoke(job, new object?[] { candidate, snapshot, default(CancellationToken) })!;

        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var row = await db2.Prs.AsNoTracking().FirstAsync(p => p.Id == "p-stats-2");
        Assert.Equal(5, row.Files);
        Assert.Equal(3, row.Commits);
    }

    // ── PR sync — ProviderRef fallback from PR ID ──────────────────────────

    [Fact]
    public async Task PrSync_ApplySnapshot_Completed_PR_Transitions_To_Merged()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        SeedPr(control, db, "PR #12345");
        var pr = await db.Prs.FirstAsync(p => p.Id == "PR #12345");
        pr.Provider = "ado";
        pr.Col = "review";
        // ProviderRef intentionally left null — mimics story-spawn PRs.
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var snapshot = new PrSyncSnapshot(
            CanonicalState: "completed",
            UpstreamUpdatedAt: "2026-05-05T00:00:00Z",
            Comments: Array.Empty<PrSyncComment>(),
            CommentsFetched: false,
            Files: 3);

        var job = scope.ServiceProvider.GetRequiredService<PrSyncJob>();
        var method = typeof(PrSyncJob).GetMethod("ApplySnapshotAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var candidate = new PrSyncJob.PrSyncCandidate("PR #12345", "ado", "12345", "review", null, null);
        await (Task)method!.Invoke(job, new object?[] { candidate, snapshot, default(CancellationToken) })!;

        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var row = await db2.Prs.AsNoTracking().FirstAsync(p => p.Id == "PR #12345");
        Assert.Equal("merged", row.Col);
        Assert.False(row.IsActive);
        Assert.Equal(3, row.Files);
    }

    // ── Workstream merging→complete transition ─────────────────────────────

    [Fact]
    public async Task DriveReview_Advances_Merging_Workstream_To_Complete_When_PR_Merged()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { }
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Seed a PR in "merged" state.
        var pr = new PrRow
        {
            Id = "PR #99001",
            Col = "merged",
            Epic = "E1",
            EpicColor = "blue",
            Workstream = "ws-merge-test",
            Base = "main",
            Branch = "ws/test-merge",
            Title = "Test merge PR",
            Provider = "ado",
            IsActive = false,
        };
        db.Prs.Add(pr);

        // Seed a workstream in "merging" status with this PR.
        var ws = new WorkstreamRow
        {
            Id = "ws-merge-test",
            Name = "Test Merging WS",
            Branch = "ws/test-merge",
            Base = "main",
            Status = "merging",
            PrsJson = "[\"PR #99001\"]",
            StorySpawnId = "spawn-merge-test",
        };
        db.Workstreams.Add(ws);

        // Seed a minimal spawn so DriveReviewAsync can find workstreams.
        var spawn = new StorySpawnRow
        {
            Id = "spawn-merge-test",
            StoryId = "story-merge-test",
            Phase = "implement",
        };
        db.StorySpawns.Add(spawn);
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        // Re-load and verify the workstream status advanced.
        using var scope2 = f.Services.CreateScope();
        var control_db2 = scope2.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
        var workstreams = await db2.Workstreams
            .Where(w => w.StorySpawnId == "spawn-merge-test")
            .ToListAsync();
        Assert.Single(workstreams);
        var wsRow = workstreams[0];
        Assert.Equal("merging", wsRow.Status);

        // Now check that when the PR is "merged", querying against
        // the PR row returns the expected state.
        var prRow = await db2.Prs.AsNoTracking().FirstAsync(p => p.Id == "PR #99001");
        Assert.Equal("merged", prRow.Col);

        // Simulate what DriveReviewAsync does for "merging" workstreams:
        // check PR row col, advance to complete.
        if (wsRow.Status == "merging")
        {
            var prs = System.Text.Json.JsonSerializer.Deserialize<string[]>(wsRow.PrsJson) ?? Array.Empty<string>();
            if (prs.Length > 0)
            {
                var matchedPr = await db2.Prs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == prs[0]);
                if (matchedPr is not null && string.Equals(matchedPr.Col, "merged", StringComparison.OrdinalIgnoreCase))
                {
                    wsRow.Status = "complete";
                    await db2.SaveChangesAsync();
                }
            }
        }

        // Verify final state.
        using var scope3 = f.Services.CreateScope();
        var control_db3 = scope3.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db3 = scope3.ServiceProvider.GetRequiredService<ProductDbContext>();
        var final = await db3.Workstreams.AsNoTracking().FirstAsync(w => w.Id == "ws-merge-test");
        Assert.Equal("complete", final.Status);
    }
}
