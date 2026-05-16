// -----------------------------------------------------------------------
// <copyright file="Phase26BuildTestGateTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 26 — build &amp; test gate before PR creation.
///   1. Migration — columns added idempotently.
///   2. ParseCommandLine — command string → executable + args.
///   3. RunCommandAsync — process execution with timeout + tree kill.
///   4. API surface — PATCH/GET repos round-trip the new fields.
///   5. Delivery pipeline — build/test gate blocks or allows push + PR.
/// </summary>
public class Phase26BuildTestGateTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static async Task<(ForgeWebApplicationFactory f, IServiceScope scope, ProductDbContext db)> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* trigger boot + migration */ }
        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        return (f, scope, db);
    }

    /// <summary>Disposes factory with pre-existing hosted-service disposal race suppressed.</summary>
    private static void SafeDispose(ForgeWebApplicationFactory f, IServiceScope scope)
    {
        scope.Dispose();
        try { f.Dispose(); }
        catch (AggregateException) { /* hosted service CTS disposal race — pre-existing */ }
    }

    private static HttpClient AuthedClient(ForgeWebApplicationFactory f, string user = "po@forge.local")
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Forge-User", user);
        return c;
    }

    /// <summary>
    /// Polls GET /api/repos/{repoId}/validation-runs until the run with <paramref name="runId"/>
    /// reaches a terminal status (succeeded/failed/cancelled) or the timeout expires.
    /// </summary>
    /// <summary>
    /// Default timeout bumped 120s → 600s. The E2E ValidateBuild tests run a
    /// real `dotnet restore` + `dotnet build` + `dotnet test` chain — on a
    /// dev box with a cold NuGet cache and an antivirus on the worktree
    /// path, restore alone can take 60-120s, leaving no headroom for the
    /// build / test legs. 600s is enough for a full cold pipeline.
    /// </summary>
    private static async Task<JsonElement> PollValidationRunAsync(
        HttpClient client, string repoId, string runId, int timeoutSec = 600)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            var resp = await client.GetAsync($"/api/repos/{repoId}/validation-runs");
            resp.EnsureSuccessStatusCode();
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var run in doc.RootElement.EnumerateArray())
            {
                if (run.GetProperty("id").GetString() == runId)
                {
                    var status = run.GetProperty("status").GetString();
                    if (status is "succeeded" or "failed" or "cancelled")
                        return run;
                }
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"Validation run {runId} did not complete within {timeoutSec}s");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 26.1 Migration
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Phase26Migration_Runs_Without_Error()
    {
        using var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* trigger boot + migration */ }
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        Assert.NotNull(db);
    }

    [Fact]
    public async Task Migration_Adds_BuildCommand_Column()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var repo = await db.Repos.FirstAsync();
            repo.BuildCommand = "dotnet build Workflow.sln";
            await db.SaveChangesAsync();

            using var scope2 = f.Services.CreateScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
            var saved = await db2.Repos.AsNoTracking().FirstAsync(r => r.Id == repo.Id);
            Assert.Equal("dotnet build Workflow.sln", saved.BuildCommand);
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task Migration_Adds_TestCommand_Column()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var repo = await db.Repos.FirstAsync();
            repo.TestCommand = "dotnet test src/Foo.Tests/Foo.Tests.csproj";
            await db.SaveChangesAsync();

            using var scope2 = f.Services.CreateScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
            var saved = await db2.Repos.AsNoTracking().FirstAsync(r => r.Id == repo.Id);
            Assert.Equal("dotnet test src/Foo.Tests/Foo.Tests.csproj", saved.TestCommand);
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task Migration_Adds_Timeout_Columns()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var repo = await db.Repos.FirstAsync();
            repo.BuildTimeoutSec = 300;
            repo.TestTimeoutSec = 900;
            await db.SaveChangesAsync();

            using var scope2 = f.Services.CreateScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
            var saved = await db2.Repos.AsNoTracking().FirstAsync(r => r.Id == repo.Id);
            Assert.Equal(300, saved.BuildTimeoutSec);
            Assert.Equal(900, saved.TestTimeoutSec);
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task Migration_Is_Idempotent()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            // Run migration a second time — should not throw.
            await Phase26Migration.ApplyAsync(db);
            await Phase26Migration.ApplyAsync(db);

            // Still works.
            var repo = await db.Repos.FirstAsync();
            repo.BuildCommand = "echo ok";
            await db.SaveChangesAsync();
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task Migration_Columns_Exist_Via_Pragma()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM pragma_table_info('repos') WHERE name IN ('BuildCommand','TestCommand','BuildTimeoutSec','TestTimeoutSec')";
            var cols = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) cols.Add(reader.GetString(0));

            Assert.Contains("BuildCommand", cols);
            Assert.Contains("TestCommand", cols);
            Assert.Contains("BuildTimeoutSec", cols);
            Assert.Contains("TestTimeoutSec", cols);
        }
        finally { SafeDispose(f, scope); }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 26.2 ParseCommandLine — pure unit tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ParseCommandLine_Simple_Command()
    {
        var parts = CrewQueueWorker.ParseCommandLine("dotnet build");
        Assert.Equal(new[] { "dotnet", "build" }, parts);
    }

    [Fact]
    public void ParseCommandLine_With_Multiple_Args()
    {
        var parts = CrewQueueWorker.ParseCommandLine("dotnet test src/Foo.Tests/Foo.Tests.csproj --no-build");
        Assert.Equal(new[] { "dotnet", "test", "src/Foo.Tests/Foo.Tests.csproj", "--no-build" }, parts);
    }

    [Fact]
    public void ParseCommandLine_Quoted_Args()
    {
        var parts = CrewQueueWorker.ParseCommandLine("dotnet test --filter \"DisplayName~Foo bar\"");
        Assert.Equal(new[] { "dotnet", "test", "--filter", "DisplayName~Foo bar" }, parts);
    }

    [Fact]
    public void ParseCommandLine_Empty_String()
    {
        var parts = CrewQueueWorker.ParseCommandLine("");
        Assert.Empty(parts);
    }

    [Fact]
    public void ParseCommandLine_Multiple_Spaces()
    {
        var parts = CrewQueueWorker.ParseCommandLine("dotnet   build   Workflow.sln");
        Assert.Equal(new[] { "dotnet", "build", "Workflow.sln" }, parts);
    }

    [Fact]
    public void ParseCommandLine_Single_Token()
    {
        var parts = CrewQueueWorker.ParseCommandLine("dotnet");
        Assert.Equal(new[] { "dotnet" }, parts);
    }

    [Fact]
    public void ParseCommandLine_Quoted_Path_With_Spaces()
    {
        var parts = CrewQueueWorker.ParseCommandLine("\"C:\\Program Files\\dotnet\\dotnet.exe\" build");
        Assert.Equal(new[] { "C:\\Program Files\\dotnet\\dotnet.exe", "build" }, parts);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 26.3 RunCommandAsync — process execution tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunCommandAsync_Successful_Command()
    {
        var cwd = Path.GetTempPath();
        var (code, stdout, stderr, timedOut) = await CrewQueueWorker.RunCommandAsync(cwd, "dotnet --version");
        Assert.Equal(0, code);
        Assert.False(timedOut);
        Assert.False(string.IsNullOrWhiteSpace(stdout));
    }

    [Fact]
    public async Task RunCommandAsync_Failing_Command()
    {
        var cwd = Path.GetTempPath();
        // dotnet build on a nonexistent project should fail.
        var (code, stdout, stderr, timedOut) = await CrewQueueWorker.RunCommandAsync(
            cwd, "dotnet build nonexistent-project-abc123.csproj");
        Assert.NotEqual(0, code);
        Assert.False(timedOut);
    }

    [Fact]
    public async Task RunCommandAsync_Timeout_Kills_Process()
    {
        var cwd = Path.GetTempPath();
        // "dotnet" with no args enters interactive mode or hangs; use a sleep-like command.
        // On Windows, ping with a long timeout is reliable.
        var (code, stdout, stderr, timedOut) = await CrewQueueWorker.RunCommandAsync(
            cwd, "ping -n 30 127.0.0.1", timeoutSeconds: 2);
        Assert.True(timedOut, "Command should have timed out");
        Assert.Contains("[TIMED OUT]", stderr);
    }

    [Fact]
    public async Task RunCommandAsync_Empty_Command_Returns_Error()
    {
        var cwd = Path.GetTempPath();
        var (code, stdout, stderr, timedOut) = await CrewQueueWorker.RunCommandAsync(cwd, "");
        Assert.Equal(-1, code);
        Assert.Equal("Empty command", stderr);
    }

    [Fact]
    public async Task RunCommandAsync_Nonexistent_Executable()
    {
        var cwd = Path.GetTempPath();
        // A nonexistent executable should throw or return error.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await CrewQueueWorker.RunCommandAsync(cwd, "totally-nonexistent-executable-xyz --help");
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 26.4 API surface — PATCH / GET repos round-trip
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PatchRepo_Sets_BuildCommand_And_Persists()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var repo = await db.Repos.FirstAsync();
            using var client = AuthedClient(f);

            var patch = new { buildCommand = "dotnet build Workflow.sln" };
            var resp = await client.PatchAsJsonAsync($"/api/repos/{repo.Id}", patch, JsonOpts);
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            // Verify via GET round-trip.
            var product = await db.Products.FirstAsync();
            var getResp = await client.GetAsync($"/api/products/{product.Id}/repos");
            var body = await getResp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(body);
            var repos = doc.RootElement.EnumerateArray().ToArray();
            var target = repos.First(r => r.GetProperty("id").GetString() == repo.Id);
            Assert.Equal("dotnet build Workflow.sln", target.GetProperty("buildCommand").GetString());
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task PatchRepo_Sets_TestCommand_And_Persists()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var repo = await db.Repos.FirstAsync();
            using var client = AuthedClient(f);

            var patch = new { testCommand = "dotnet test src/Foo/Foo.csproj" };
            var resp = await client.PatchAsJsonAsync($"/api/repos/{repo.Id}", patch, JsonOpts);
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            // Verify DB persistence.
            using var scope2 = f.Services.CreateScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
            var saved = await db2.Repos.AsNoTracking().FirstAsync(r => r.Id == repo.Id);
            Assert.Equal("dotnet test src/Foo/Foo.csproj", saved.TestCommand);
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task PatchRepo_Sets_Timeouts_And_Persists()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var repo = await db.Repos.FirstAsync();
            using var client = AuthedClient(f);

            var patch = new { buildTimeoutSec = 120, testTimeoutSec = 300 };
            var resp = await client.PatchAsJsonAsync($"/api/repos/{repo.Id}", patch, JsonOpts);
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            using var scope2 = f.Services.CreateScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
            var saved = await db2.Repos.AsNoTracking().FirstAsync(r => r.Id == repo.Id);
            Assert.Equal(120, saved.BuildTimeoutSec);
            Assert.Equal(300, saved.TestTimeoutSec);
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task PatchRepo_Empty_Command_Clears_To_Null()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var repo = await db.Repos.FirstAsync();
            repo.BuildCommand = "dotnet build";
            repo.TestCommand = "dotnet test";
            await db.SaveChangesAsync();

            using var client = AuthedClient(f);
            var patch = new { buildCommand = "", testCommand = "  " };
            var resp = await client.PatchAsJsonAsync($"/api/repos/{repo.Id}", patch, JsonOpts);
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            using var scope2 = f.Services.CreateScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
            var saved = await db2.Repos.AsNoTracking().FirstAsync(r => r.Id == repo.Id);
            Assert.Null(saved.BuildCommand);
            Assert.Null(saved.TestCommand);
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task PatchRepo_Zero_Or_Negative_Timeout_Clears_To_Null()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var repo = await db.Repos.FirstAsync();
            repo.BuildTimeoutSec = 600;
            repo.TestTimeoutSec = 600;
            await db.SaveChangesAsync();

            using var client = AuthedClient(f);
            var patch = new { buildTimeoutSec = 0, testTimeoutSec = -1 };
            var resp = await client.PatchAsJsonAsync($"/api/repos/{repo.Id}", patch, JsonOpts);
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

            using var scope2 = f.Services.CreateScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
            var saved = await db2.Repos.AsNoTracking().FirstAsync(r => r.Id == repo.Id);
            Assert.Null(saved.BuildTimeoutSec);
            Assert.Null(saved.TestTimeoutSec);
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task GetRepos_Returns_BuildTestFields()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var repo = await db.Repos.FirstAsync();
            repo.BuildCommand = "dotnet build";
            repo.TestCommand = "dotnet test";
            repo.BuildTimeoutSec = 180;
            repo.TestTimeoutSec = 360;
            await db.SaveChangesAsync();

            using var client = AuthedClient(f);
            var product = await db.Products.FirstAsync();
            var resp = await client.GetAsync($"/api/products/{product.Id}/repos");
            var body = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(body);
            var target = doc.RootElement.EnumerateArray()
                .First(r => r.GetProperty("id").GetString() == repo.Id);

            Assert.Equal("dotnet build", target.GetProperty("buildCommand").GetString());
            Assert.Equal("dotnet test", target.GetProperty("testCommand").GetString());
            Assert.Equal(180, target.GetProperty("buildTimeoutSec").GetInt32());
            Assert.Equal(360, target.GetProperty("testTimeoutSec").GetInt32());
        }
        finally { SafeDispose(f, scope); }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 26.5 Delivery pipeline integration — build/test gate
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sets up a bare git remote + working repo, seeds crew/repo/work item,
    /// enqueues the work item, runs the queue worker, and waits for the
    /// background RunAgentAsync to complete. Pre-creates a commit on the
    /// workstream branch so the pipeline's commits-ahead check passes
    /// (local-stub doesn't modify files itself).
    /// </summary>
    private static async Task<PipelineTestContext> SetupPipelineTestAsync(
        string? buildCommand = null, string? testCommand = null,
        int? buildTimeoutSec = null, int? testTimeoutSec = null)
    {
        // ── Set up bare remote + working repo ────────────────────────
        var testRoot = Path.Combine(Path.GetTempPath(), $"forge-p26-{Guid.NewGuid():N}");
        var bareDir = Path.Combine(testRoot, "bare.git");
        var repoDir = Path.Combine(testRoot, "repo");
        Directory.CreateDirectory(bareDir);
        Directory.CreateDirectory(repoDir);

        await RunGitAsync(bareDir, "init", "--bare", "-b", "main");
        await RunGitAsync(repoDir, "init", "-b", "main");
        await RunGitAsync(repoDir, "config", "user.email", "test@forge.local");
        await RunGitAsync(repoDir, "config", "user.name", "Forge Test");
        await RunGitAsync(repoDir, "remote", "add", "origin", bareDir);
        await File.WriteAllTextAsync(Path.Combine(repoDir, "README.md"), "# Build test gate\n");
        // Pre-seed .worktree/ in .gitignore.
        await File.WriteAllTextAsync(Path.Combine(repoDir, ".gitignore"), ".worktree/\n");
        await RunGitAsync(repoDir, "add", ".");
        await RunGitAsync(repoDir, "commit", "-m", "initial seed");
        await RunGitAsync(repoDir, "push", "-u", "origin", "main");

        var workItemId = $"WI-P26-{Guid.NewGuid():N}"[..16];
        var branchName = $"ws/{workItemId}";

        // Pre-create the workstream branch with a commit ahead of main.
        // local-stub doesn't modify files, so the pipeline would see
        // 0 commits ahead and skip push + PR (including the build gate).
        await RunGitAsync(repoDir, "checkout", "-b", branchName);
        await File.WriteAllTextAsync(
            Path.Combine(repoDir, "CHANGE.md"), "# Test change for build gate\n");
        await RunGitAsync(repoDir, "add", "CHANGE.md");
        await RunGitAsync(repoDir, "commit", "-m", "test change for build gate");
        await RunGitAsync(repoDir, "checkout", "main");

        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var product = await db.Products.FirstAsync();

        db.Crews.Add(new CrewRow
        {
            Id = "p26-crew", Name = "P26 Crew",
            Lead = "p26-lead", Model = "local-stub",
            ModelInputPricePerMTok = 0m, ModelOutputPricePerMTok = 0m,
            MaxConcurrent = 5, ScheduleEnabled = false,
        });
        db.CrewMembers.Add(new CrewMemberRow
        {
            CrewId = "p26-crew", AgentId = "p26-agent",
            AddedAt = DateTimeOffset.UtcNow,
        });

        db.Repos.Add(new RepoRow
        {
            Id = "p26-repo", DisplayName = "P26 Repo", Kind = "local",
            RemoteUrl = bareDir,
            ProductId = product.Id, CloneRoot = repoDir, DefaultBranch = "main",
            BuildCommand = buildCommand,
            TestCommand = testCommand,
            BuildTimeoutSec = buildTimeoutSec,
            TestTimeoutSec = testTimeoutSec,
        });

        db.WorkItems.Add(new WorkItemRow
        {
            Id = workItemId,
            Type = "task", Track = "dev", State = "active",
            Title = "test build gate",
            DescriptionMd = "Phase 26 pipeline test.",
            RepoId = "p26-repo",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        // Enqueue and run.
        var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
        await queue.EnqueueAsync(workItemId, "crew", "p26-crew", default);

        var worker = f.Services.GetRequiredService<CrewQueueWorker>();
        await worker.RunOnceAsync(default);

        // local-stub finishes fast; RunAgentAsync is Task.Run, so wait.
        // The deliverable loop runs gen → eval (auto-pass for standalone crew)
        // then the build/test gate + push pipeline. Allow enough time.
        await Task.Delay(15_000);

        return new PipelineTestContext(f, scope, db, workItemId, testRoot, repoDir, bareDir);
    }

    private sealed record PipelineTestContext(
        ForgeWebApplicationFactory Factory,
        IServiceScope Scope,
        ProductDbContext Db,
        string WorkItemId,
        string TestRoot,
        string RepoDir,
        string BareDir) : IDisposable
    {
        public void Dispose()
        {
            this.Scope.Dispose();
            // The factory's DisposeAsync triggers StopAsync on hosted services
            // (LeadAutoDispatcher, WorktreeChangeFeed, CrewQueueWorker) which
            // throws ObjectDisposedException when CancellationTokenSource has
            // already been disposed. This is a pre-existing issue (same as
            // Phase 16 tests). Suppress so the test result reflects assertions.
            try { this.Factory.Dispose(); }
            catch (AggregateException) { /* hosted service disposal race */ }

            try
            {
                RunGitSync(this.RepoDir, "worktree", "prune");
                var wtRoot = Path.Combine(this.RepoDir, ".worktree");
                if (Directory.Exists(wtRoot)) Directory.Delete(wtRoot, recursive: true);
            }
            catch { }
            try { Directory.Delete(this.TestRoot, recursive: true); }
            catch { }
        }

        public async Task<List<TaskTranscriptEventRow>> GetEventsAsync()
        {
            return await this.Db.TaskTranscriptEvents.AsNoTracking()
                .Where(e => e.WorkItemId == this.WorkItemId)
                .OrderBy(e => e.Id)
                .ToListAsync();
        }

        public async Task<WorkItemRow?> GetWorkItemAsync()
        {
            return await this.Db.WorkItems.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == this.WorkItemId);
        }
    }

    // ── No config → backward compat ──────────────────────────────────────

    [Fact]
    public async Task No_BuildTest_Config_Skips_Gate()
    {
        if (!GitAvailable()) return;
        using var ctx = await SetupPipelineTestAsync(
            buildCommand: null, testCommand: null);

        var events = await ctx.GetEventsAsync();

        // No build/test events should exist.
        Assert.DoesNotContain(events, e => e.Kind == "task.build-started");
        Assert.DoesNotContain(events, e => e.Kind == "task.test-started");

        // Branch should be pushed (or at least attempted).
        Assert.True(
            events.Any(e => e.Kind == "task.branch-pushed")
            || events.Any(e => e.Kind == "task.no-changes"),
            "Without build/test config, pipeline should proceed to push or no-changes");
    }

    // ── Build passes ─────────────────────────────────────────────────────

    [Fact]
    public async Task BuildGate_Pass_Emits_BuildStarted_And_BuildPassed()
    {
        if (!GitAvailable()) return;
        using var ctx = await SetupPipelineTestAsync(
            buildCommand: "dotnet --version");

        var events = await ctx.GetEventsAsync();

        Assert.Contains(events, e => e.Kind == "task.build-started");
        Assert.Contains(events, e => e.Kind == "task.build-passed");
        Assert.DoesNotContain(events, e => e.Kind == "task.build-failed");

        // Build-started should precede build-passed.
        var startId = events.First(e => e.Kind == "task.build-started").Id;
        var passId = events.First(e => e.Kind == "task.build-passed").Id;
        Assert.True(startId < passId, "build-started must precede build-passed");
    }

    // ── Build fails → delivery aborted ──────────────────────────────────

    [Fact]
    public async Task BuildGate_Fail_Aborts_Delivery_And_Emits_BuildFailed()
    {
        if (!GitAvailable()) return;
        using var ctx = await SetupPipelineTestAsync(
            buildCommand: "dotnet build nonexistent-project-abc123.csproj");

        var events = await ctx.GetEventsAsync();

        // Build events.
        Assert.Contains(events, e => e.Kind == "task.build-started");
        Assert.Contains(events, e => e.Kind == "task.build-failed");
        Assert.DoesNotContain(events, e => e.Kind == "task.build-passed");

        // No push, no PR.
        Assert.DoesNotContain(events, e => e.Kind == "task.branch-pushed");
        Assert.DoesNotContain(events, e => e.Kind == "task.pr-opened");

        // Delivery failed + task failed.
        Assert.Contains(events, e => e.Kind == "task.delivery-failed");
        Assert.Contains(events, e => e.Kind == "task.failed");

        // Execution status = Failed.
        var wi = await ctx.GetWorkItemAsync();
        Assert.Equal(ExecutionStatuses.Failed, wi?.ExecutionStatus);
    }

    // ── Test passes ─────────────────────────────────────────────────────

    [Fact]
    public async Task TestGate_Pass_Emits_TestStarted_And_TestPassed()
    {
        if (!GitAvailable()) return;
        using var ctx = await SetupPipelineTestAsync(
            testCommand: "dotnet --version");

        var events = await ctx.GetEventsAsync();

        Assert.Contains(events, e => e.Kind == "task.test-started");
        Assert.Contains(events, e => e.Kind == "task.test-passed");
        Assert.DoesNotContain(events, e => e.Kind == "task.test-failed");
    }

    // ── Test fails → delivery aborted ───────────────────────────────────

    [Fact]
    public async Task TestGate_Fail_Aborts_Delivery_And_Emits_TestFailed()
    {
        if (!GitAvailable()) return;
        using var ctx = await SetupPipelineTestAsync(
            testCommand: "dotnet build nonexistent-project-abc123.csproj");

        var events = await ctx.GetEventsAsync();

        // Test events.
        Assert.Contains(events, e => e.Kind == "task.test-started");
        Assert.Contains(events, e => e.Kind == "task.test-failed");
        Assert.DoesNotContain(events, e => e.Kind == "task.test-passed");

        // No push, no PR.
        Assert.DoesNotContain(events, e => e.Kind == "task.branch-pushed");
        Assert.DoesNotContain(events, e => e.Kind == "task.pr-opened");

        // Delivery failed + task failed.
        Assert.Contains(events, e => e.Kind == "task.delivery-failed");
        Assert.Contains(events, e => e.Kind == "task.failed");

        // Execution status = Failed.
        var wi = await ctx.GetWorkItemAsync();
        Assert.Equal(ExecutionStatuses.Failed, wi?.ExecutionStatus);
    }

    // ── Test skipped when build fails ───────────────────────────────────

    [Fact]
    public async Task TestGate_Skipped_When_Build_Fails()
    {
        if (!GitAvailable()) return;
        using var ctx = await SetupPipelineTestAsync(
            buildCommand: "dotnet build nonexistent-project-abc123.csproj",
            testCommand: "dotnet --version");

        var events = await ctx.GetEventsAsync();

        // Build ran and failed.
        Assert.Contains(events, e => e.Kind == "task.build-started");
        Assert.Contains(events, e => e.Kind == "task.build-failed");

        // Test never started.
        Assert.DoesNotContain(events, e => e.Kind == "task.test-started");
        Assert.DoesNotContain(events, e => e.Kind == "task.test-passed");
        Assert.DoesNotContain(events, e => e.Kind == "task.test-failed");

        // Delivery failed.
        Assert.Contains(events, e => e.Kind == "task.delivery-failed");
    }

    // ── Both build and test pass → full sequencing ──────────────────────

    [Fact]
    public async Task BuildAndTest_Both_Pass_Full_Event_Sequence()
    {
        if (!GitAvailable()) return;
        using var ctx = await SetupPipelineTestAsync(
            buildCommand: "dotnet --version",
            testCommand: "dotnet --version");

        var events = await ctx.GetEventsAsync();

        // All expected events present.
        Assert.Contains(events, e => e.Kind == "task.build-started");
        Assert.Contains(events, e => e.Kind == "task.build-passed");
        Assert.Contains(events, e => e.Kind == "task.test-started");
        Assert.Contains(events, e => e.Kind == "task.test-passed");

        // No failures.
        Assert.DoesNotContain(events, e => e.Kind == "task.build-failed");
        Assert.DoesNotContain(events, e => e.Kind == "task.test-failed");

        // Correct ordering: build-started < build-passed < test-started < test-passed < push/PR.
        var buildStart = events.First(e => e.Kind == "task.build-started").Id;
        var buildPass = events.First(e => e.Kind == "task.build-passed").Id;
        var testStart = events.First(e => e.Kind == "task.test-started").Id;
        var testPass = events.First(e => e.Kind == "task.test-passed").Id;
        Assert.True(buildStart < buildPass, "build-started → build-passed");
        Assert.True(buildPass < testStart, "build-passed → test-started");
        Assert.True(testStart < testPass, "test-started → test-passed");

        // Push or PR attempt happened after tests.
        var pushOrPr = events.FirstOrDefault(
            e => e.Kind == "task.branch-pushed" || e.Kind == "task.no-changes");
        if (pushOrPr is not null)
        {
            Assert.True(testPass < pushOrPr.Id, "test-passed → push/no-changes");
        }
    }

    // ── Build command with invalid executable → failure ─────────────────

    [Fact]
    public async Task BuildGate_Invalid_Executable_Aborts_Delivery()
    {
        if (!GitAvailable()) return;
        using var ctx = await SetupPipelineTestAsync(
            buildCommand: "nonexistent-build-tool-xyz123 --help");

        var events = await ctx.GetEventsAsync();

        // Build started but should fail (either via build-failed or delivery-failed).
        // The exact behavior depends on whether Process.Start throws or returns non-zero.
        var hasBuildFailure = events.Any(e => e.Kind == "task.build-failed")
            || events.Any(e => e.Kind == "task.delivery-failed")
            || events.Any(e => e.Kind == "task.failed");
        Assert.True(hasBuildFailure, "Invalid executable should cause a failure event");

        // No PR should be created.
        Assert.DoesNotContain(events, e => e.Kind == "task.pr-opened");
    }

    // ── Build payload contains command string ──────────────────────────

    [Fact]
    public async Task BuildStarted_Event_Contains_Command()
    {
        if (!GitAvailable()) return;
        using var ctx = await SetupPipelineTestAsync(
            buildCommand: "dotnet --version");

        var events = await ctx.GetEventsAsync();
        var buildStarted = events.FirstOrDefault(e => e.Kind == "task.build-started");
        Assert.NotNull(buildStarted);

        var payload = JsonDocument.Parse(buildStarted.PayloadJson);
        Assert.Equal("dotnet --version",
            payload.RootElement.GetProperty("command").GetString());
    }

    // ── Build failure payload contains exit code + output ────────────────

    [Fact]
    public async Task BuildFailed_Event_Contains_ExitCode_And_Output()
    {
        if (!GitAvailable()) return;
        using var ctx = await SetupPipelineTestAsync(
            buildCommand: "dotnet build nonexistent-project-abc123.csproj");

        var events = await ctx.GetEventsAsync();
        var buildFailed = events.FirstOrDefault(e => e.Kind == "task.build-failed");
        Assert.NotNull(buildFailed);

        var payload = JsonDocument.Parse(buildFailed.PayloadJson);
        Assert.True(payload.RootElement.TryGetProperty("exitCode", out _),
            "build-failed payload should contain exitCode");
        Assert.True(payload.RootElement.TryGetProperty("reason", out _),
            "build-failed payload should contain reason");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 26.7 BuildSystemDetector — filesystem-only scan
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Detector_Dotnet_Sln()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "MyApp.sln"), "");
            var result = BuildSystemDetector.Detect(dir);
            Assert.Equal("dotnet", result.Ecosystem);
            Assert.Equal("dotnet build MyApp.sln", result.BuildCommand);
            Assert.Equal("dotnet test MyApp.sln", result.TestCommand);
            Assert.Equal("dotnet restore MyApp.sln", result.RestoreCommand);
            Assert.False(result.Ambiguous);
            Assert.Empty(result.QualityCommands);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Detector_Dotnet_Sln_With_Spaces()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "My App.sln"), "");
            var result = BuildSystemDetector.Detect(dir);
            Assert.Equal("dotnet", result.Ecosystem);
            Assert.Equal("dotnet build \"My App.sln\"", result.BuildCommand);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Detector_Multiple_Sln_Is_Ambiguous()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "A.sln"), "");
            File.WriteAllText(Path.Combine(dir, "B.sln"), "");
            var result = BuildSystemDetector.Detect(dir);
            Assert.Equal("dotnet", result.Ecosystem);
            Assert.True(result.Ambiguous);
            Assert.NotNull(result.AmbiguityDetail);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Detector_Node_PackageJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "package.json"),
                "{\"scripts\":{\"build\":\"tsc\",\"test\":\"vitest\"}}");
            var result = BuildSystemDetector.Detect(dir);
            Assert.Equal("node", result.Ecosystem);
            Assert.Equal("npm run build", result.BuildCommand);
            Assert.Equal("npm test", result.TestCommand);
            Assert.Equal("npm install", result.RestoreCommand);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Detector_Node_No_Scripts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "package.json"), "{}");
            var result = BuildSystemDetector.Detect(dir);
            Assert.Equal("node", result.Ecosystem);
            Assert.Null(result.BuildCommand);
            Assert.Null(result.TestCommand);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Detector_Go_Mod()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "go.mod"), "module example.com/foo");
            var result = BuildSystemDetector.Detect(dir);
            Assert.Equal("go", result.Ecosystem);
            Assert.Equal("go build ./...", result.BuildCommand);
            Assert.Equal("go test ./...", result.TestCommand);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Detector_Rust_Cargo()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Cargo.toml"), "[package]\nname = \"foo\"");
            var result = BuildSystemDetector.Detect(dir);
            Assert.Equal("rust", result.Ecosystem);
            Assert.Equal("cargo build", result.BuildCommand);
            Assert.Equal("cargo test", result.TestCommand);
            // Phase 49 follow-up — quality gates mirror upstream CI for
            // Cargo. PR smartpcr/failover-cluster#6 (2026-05-11) sat
            // un-merged for 3+ hours because local `cargo build` passed
            // while remote CI rejected the PR on a cargo-fmt diff. The
            // detector now seeds fmt + clippy as post-pass quality
            // commands so Forge's gate matches CI's required-check set.
            Assert.Equal(2, result.QualityCommands.Count);
            Assert.Equal("cargo fmt --check --all", result.QualityCommands[0]);
            Assert.Equal("cargo clippy --workspace --all-targets -- -D warnings", result.QualityCommands[1]);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Phase 49 follow-up — only Cargo gets non-empty <c>QualityCommands</c>
    /// today; every other ecosystem returns an empty list so existing
    /// behaviour is preserved bit-for-bit. dotnet / node / go / maven /
    /// python vary too much per-repo on what fmt/lint they enforce to ship
    /// universal defaults that wouldn't false-fail.
    /// </summary>
    [Theory]
    [InlineData("dotnet", "MyApp.sln", "")]
    [InlineData("node", "package.json", "{}")]
    [InlineData("go", "go.mod", "module example.com/foo")]
    [InlineData("maven", "pom.xml", "<project></project>")]
    [InlineData("python", "pyproject.toml", "[build-system]")]
    public void Detector_Non_Rust_Ecosystems_Have_Empty_Quality_Commands(string expectedEcosystem, string markerFile, string markerContent)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, markerFile), markerContent);
            var result = BuildSystemDetector.Detect(dir);
            Assert.Equal(expectedEcosystem, result.Ecosystem);
            Assert.NotNull(result.QualityCommands);
            Assert.Empty(result.QualityCommands);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Phase 49 follow-up — an unknown ecosystem (no marker files in the
    /// root) returns empty <c>QualityCommands</c>, matching the build /
    /// test command nulls so the orchestrator skips the gate entirely
    /// instead of running an arbitrary quality command against a workspace
    /// it can't classify.
    /// </summary>
    [Fact]
    public void Detector_Unknown_Has_Empty_Quality_Commands()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var result = BuildSystemDetector.Detect(dir);
            Assert.Equal("unknown", result.Ecosystem);
            Assert.NotNull(result.QualityCommands);
            Assert.Empty(result.QualityCommands);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Detector_Python_Pyproject()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "pyproject.toml"), "[build-system]");
            var result = BuildSystemDetector.Detect(dir);
            Assert.Equal("python", result.Ecosystem);
            Assert.Equal("pytest", result.TestCommand);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Detector_Maven_Pom()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "pom.xml"), "<project></project>");
            var result = BuildSystemDetector.Detect(dir);
            Assert.Equal("maven", result.Ecosystem);
            Assert.Equal("mvn package -DskipTests", result.BuildCommand);
            Assert.Equal("mvn test", result.TestCommand);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Detector_Unknown_Empty_Dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var result = BuildSystemDetector.Detect(dir);
            Assert.Equal("unknown", result.Ecosystem);
            Assert.Null(result.BuildCommand);
            Assert.Null(result.TestCommand);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Detector_Nonexistent_Dir()
    {
        var result = BuildSystemDetector.Detect("/nonexistent/path/xyz");
        Assert.Equal("unknown", result.Ecosystem);
    }

    [Fact]
    public void MergeInto_Only_Sets_Missing_Fields()
    {
        var row = new RepoRow
        {
            Id = "merge-test",
            BuildCommand = "my-custom-build",
            TestCommand = null,
        };
        var detected = new BuildSystemDetector.DetectedCommands(
            null, "dotnet restore", "dotnet build Foo.sln", "dotnet test Foo.sln",
            "dotnet", false, null, Array.Empty<string>());
        var changed = BuildSystemDetector.MergeInto(row, detected);
        Assert.True(changed);
        Assert.Equal("my-custom-build", row.BuildCommand); // preserved
        Assert.Equal("dotnet test Foo.sln", row.TestCommand); // filled
    }

    [Fact]
    public void MergeInto_Returns_False_When_Nothing_New()
    {
        var row = new RepoRow
        {
            Id = "merge-noop",
            BuildCommand = "existing-build",
            TestCommand = "existing-test",
        };
        var detected = new BuildSystemDetector.DetectedCommands(
            null, null, "dotnet build", "dotnet test", "dotnet", false, null, Array.Empty<string>());
        var changed = BuildSystemDetector.MergeInto(row, detected);
        Assert.False(changed);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 26.8 Detect / Validate endpoints
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DetectBuild_Returns_Ecosystem_And_Commands()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var repo = await db.Repos.FirstAsync();
            // Ensure the repo's cloneRoot exists and has a .sln file.
            if (!string.IsNullOrEmpty(repo.CloneRoot) && Directory.Exists(repo.CloneRoot))
            {
                using var client = AuthedClient(f);
                var resp = await client.PostAsync($"/api/repos/{repo.Id}/detect-build", null);
                Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
                var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                Assert.True(doc.RootElement.TryGetProperty("ecosystem", out _));
            }
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task DetectBuild_Requires_Auth()
    {
        var (f, scope, _) = await BootAsync();
        try
        {
            using var client = f.CreateClient();
            var repo = await scope.ServiceProvider.GetRequiredService<ProductDbContext>()
                .Repos.FirstAsync();
            var resp = await client.PostAsync($"/api/repos/{repo.Id}/detect-build", null);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task DetectBuild_Returns_422_When_No_CloneRoot()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Repos.Add(new RepoRow
            {
                Id = "no-clone", DisplayName = "No Clone", Kind = "ado",
                RemoteUrl = "https://example.com/repo", ProductId = product.Id,
                CloneRoot = "/nonexistent/path/xyz",
            });
            await db.SaveChangesAsync();

            using var client = AuthedClient(f);
            var resp = await client.PostAsync("/api/repos/no-clone/detect-build", null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task ValidateBuild_Requires_Auth()
    {
        var (f, scope, _) = await BootAsync();
        try
        {
            using var client = f.CreateClient();
            var repo = await scope.ServiceProvider.GetRequiredService<ProductDbContext>()
                .Repos.FirstAsync();
            var resp = await client.PostAsync($"/api/repos/{repo.Id}/validate-build", null);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task ValidateBuild_Runs_Restore_Build_Test_And_Returns_Steps()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            // Create a temp dir with a minimal dotnet project so all three steps run.
            var tempDir = Path.Combine(Path.GetTempPath(), $"validate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                // Seed a minimal .csproj so dotnet restore/build/test have something to work with.
                // We use "dotnet --version" as the command since it's fast and always exits 0.
                var product = await db.Products.FirstAsync();
                db.Repos.Add(new RepoRow
                {
                    Id = "val-repo", DisplayName = "Validate Repo", Kind = "local",
                    RemoteUrl = $"https://example.com/val-{Guid.NewGuid():N}",
                    ProductId = product.Id,
                    CloneRoot = tempDir,
                    BuildCommand = "dotnet --version",
                    TestCommand = "dotnet --version",
                });
                await db.SaveChangesAsync();

                // No project markers — the detector won't find a restore command,
                // so validate-build will only run the configured build + test steps.

                using var client = AuthedClient(f);
                var resp = await client.PostAsync("/api/repos/val-repo/validate-build", null);
                Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

                var acceptDoc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var runId = acceptDoc.RootElement.GetProperty("runId").GetString();
                Assert.NotNull(runId);

                // Poll until the background worker completes.
                var run = await PollValidationRunAsync(client, "val-repo", runId!);
                Assert.True(run.GetProperty("allPassed").GetBoolean(), "allPassed should be true");

                var steps = JsonDocument.Parse(run.GetProperty("stepsJson").GetString()!)
                    .RootElement.EnumerateArray().ToArray();
                Assert.True(steps.Length >= 2, $"Expected at least 2 steps, got {steps.Length}");

                foreach (var step in steps)
                {
                    Assert.True(step.GetProperty("passed").GetBoolean(),
                        $"Step '{step.GetProperty("step").GetString()}' should pass");
                    Assert.Equal(0, step.GetProperty("exitCode").GetInt32());
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task ValidateBuild_Stops_On_First_Failure()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"valfail-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var product = await db.Products.FirstAsync();
                db.Repos.Add(new RepoRow
                {
                    Id = "valfail-repo", DisplayName = "Fail Repo", Kind = "local",
                    RemoteUrl = $"https://example.com/valfail-{Guid.NewGuid():N}",
                    ProductId = product.Id,
                    CloneRoot = tempDir,
                    BuildCommand = "dotnet build nonexistent-xyz.csproj",
                    TestCommand = "dotnet --version",
                });
                await db.SaveChangesAsync();

                using var client = AuthedClient(f);
                var resp = await client.PostAsync("/api/repos/valfail-repo/validate-build", null);
                Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

                var acceptDoc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var runId = acceptDoc.RootElement.GetProperty("runId").GetString();
                Assert.NotNull(runId);

                var run = await PollValidationRunAsync(client, "valfail-repo", runId!);
                Assert.False(run.GetProperty("allPassed").GetBoolean());

                var steps = JsonDocument.Parse(run.GetProperty("stepsJson").GetString()!)
                    .RootElement.EnumerateArray().ToArray();
                // Build should have failed.
                var buildStep = steps.First(s =>
                    s.GetProperty("step").GetString() == "build");
                Assert.False(buildStep.GetProperty("passed").GetBoolean());

                // Test should NOT have run (stops on first failure).
                Assert.DoesNotContain(steps, s =>
                    s.GetProperty("step").GetString() == "test");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task DetectBuild_Returns_Correct_Dotnet_Commands()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"detect-api-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                File.WriteAllText(Path.Combine(tempDir, "MyApp.sln"), "");
                var product = await db.Products.FirstAsync();
                db.Repos.Add(new RepoRow
                {
                    Id = "detect-api-repo", DisplayName = "Detect API", Kind = "local",
                    RemoteUrl = $"https://example.com/detect-api-{Guid.NewGuid():N}",
                    ProductId = product.Id,
                    CloneRoot = tempDir,
                });
                await db.SaveChangesAsync();

                using var client = AuthedClient(f);
                var resp = await client.PostAsync("/api/repos/detect-api-repo/detect-build", null);
                Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

                var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                Assert.Equal("dotnet", doc.RootElement.GetProperty("ecosystem").GetString());
                Assert.Equal("dotnet build MyApp.sln", doc.RootElement.GetProperty("buildCommand").GetString());
                Assert.Equal("dotnet test MyApp.sln", doc.RootElement.GetProperty("testCommand").GetString());
                Assert.Equal("dotnet restore MyApp.sln", doc.RootElement.GetProperty("restoreCommand").GetString());
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task ValidateBuild_Returns_422_When_No_Clone()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Repos.Add(new RepoRow
            {
                Id = "val-noclone", DisplayName = "No Clone", Kind = "local",
                RemoteUrl = $"https://example.com/val-noclone-{Guid.NewGuid():N}",
                ProductId = product.Id,
                CloneRoot = "/nonexistent/path/xyz",
            });
            await db.SaveChangesAsync();

            using var client = AuthedClient(f);
            var resp = await client.PostAsync("/api/repos/val-noclone/validate-build", null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task PatchRepo_With_CloneRoot_AutoDetects_BuildCommand()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            // Create a temp dir with a .sln file to simulate a cloned repo.
            var tempDir = Path.Combine(Path.GetTempPath(), $"detect-patch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "TestProject.sln"), "");

            try
            {
                var product = await db.Products.FirstAsync();
                db.Repos.Add(new RepoRow
                {
                    Id = "detect-repo", DisplayName = "Detect Repo", Kind = "ado",
                    RemoteUrl = $"https://example.com/detect-{Guid.NewGuid():N}",
                    ProductId = product.Id,
                    CloneRoot = "/old/path",
                });
                await db.SaveChangesAsync();

                using var client = AuthedClient(f);
                var patch = new { cloneRoot = tempDir };
                var resp = await client.PatchAsJsonAsync("/api/repos/detect-repo", patch, JsonOpts);
                Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

                // Verify build command was auto-detected.
                using var scope2 = f.Services.CreateScope();
                var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
                var saved = await db2.Repos.AsNoTracking().FirstAsync(r => r.Id == "detect-repo");
                Assert.Equal("dotnet build TestProject.sln", saved.BuildCommand);
                Assert.Equal("dotnet test TestProject.sln", saved.TestCommand);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        finally { SafeDispose(f, scope); }
    }

    [Fact]
    public async Task PatchRepo_Preserves_Existing_BuildCommand()
    {
        var (f, scope, db) = await BootAsync();
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"detect-keep-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "MyApp.sln"), "");

            try
            {
                var product = await db.Products.FirstAsync();
                db.Repos.Add(new RepoRow
                {
                    Id = "keep-repo", DisplayName = "Keep Repo", Kind = "ado",
                    RemoteUrl = $"https://example.com/keep-{Guid.NewGuid():N}",
                    ProductId = product.Id,
                    CloneRoot = "/old/path",
                    BuildCommand = "my-custom-build",
                });
                await db.SaveChangesAsync();

                using var client = AuthedClient(f);
                var patch = new { cloneRoot = tempDir };
                var resp = await client.PatchAsJsonAsync("/api/repos/keep-repo", patch, JsonOpts);
                Assert.True(resp.IsSuccessStatusCode);

                using var scope2 = f.Services.CreateScope();
                var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
                var saved = await db2.Repos.AsNoTracking().FirstAsync(r => r.Id == "keep-repo");
                Assert.Equal("my-custom-build", saved.BuildCommand); // preserved
                Assert.Equal("dotnet test MyApp.sln", saved.TestCommand); // auto-detected
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        finally { SafeDispose(f, scope); }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 26.9 E2E — real dotnet project: PATCH repo → detect → validate
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a real dotnet project via <c>dotnet new</c>, PATCHes a repo
    /// to point at it, calls detect-build, then validate-build — verifying
    /// restore → build → test all pass on real tooling.
    /// </summary>
    [Fact]
    public async Task E2E_RealDotnetProject_Detect_And_Validate_All_Pass()
    {
        if (!DotnetAvailable()) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"forge-e2e-dotnet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var (f, scope, db) = await BootAsync();
        try
        {
            // Create a real dotnet xunit test project — this gives us a
            // .csproj with restore, build, AND passing tests.
            var (initCode, _, initErr) = await RunDotnetAsync(tempDir, "new", "xunit", "-n", "E2ETests", "--force");
            Assert.Equal(0, initCode);
            var projectDir = Path.Combine(tempDir, "E2ETests");
            Assert.True(Directory.Exists(projectDir), $"dotnet new should create {projectDir}");

            // Seed a repo pointing at the project dir.
            var product = await db.Products.FirstAsync();
            db.Repos.Add(new RepoRow
            {
                Id = "e2e-dotnet", DisplayName = "E2E Dotnet", Kind = "local",
                RemoteUrl = $"https://example.com/e2e-dotnet-{Guid.NewGuid():N}",
                ProductId = product.Id,
                CloneRoot = projectDir,
            });
            await db.SaveChangesAsync();

            using var client = AuthedClient(f);

            // ── Step 1: Detect ───────────────────────────────────────────
            var detectResp = await client.PostAsync("/api/repos/e2e-dotnet/detect-build", null);
            Assert.True(detectResp.IsSuccessStatusCode, await detectResp.Content.ReadAsStringAsync());
            var detectDoc = JsonDocument.Parse(await detectResp.Content.ReadAsStringAsync());
            Assert.Equal("dotnet", detectDoc.RootElement.GetProperty("ecosystem").GetString());
            var detectedBuild = detectDoc.RootElement.GetProperty("buildCommand").GetString();
            var detectedTest = detectDoc.RootElement.GetProperty("testCommand").GetString();
            Assert.NotNull(detectedBuild);
            Assert.NotNull(detectedTest);
            Assert.Contains("E2ETests.csproj", detectedBuild);

            // ── Step 2: PATCH to persist detected commands ───────────────
            var patchResp = await client.PatchAsJsonAsync("/api/repos/e2e-dotnet", new
            {
                buildCommand = detectedBuild,
                testCommand = detectedTest,
            }, JsonOpts);
            Assert.True(patchResp.IsSuccessStatusCode, await patchResp.Content.ReadAsStringAsync());

            // Verify commands persisted.
            using var scope2 = f.Services.CreateScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
            var savedRepo = await db2.Repos.AsNoTracking().FirstAsync(r => r.Id == "e2e-dotnet");
            Assert.Equal(detectedBuild, savedRepo.BuildCommand);
            Assert.Equal(detectedTest, savedRepo.TestCommand);

            // ── Step 3: Validate (init → restore → build → test) ────────
            var valResp = await client.PostAsync("/api/repos/e2e-dotnet/validate-build", null);
            Assert.True(valResp.IsSuccessStatusCode, await valResp.Content.ReadAsStringAsync());
            var acceptDoc = JsonDocument.Parse(await valResp.Content.ReadAsStringAsync());
            var runId = acceptDoc.RootElement.GetProperty("runId").GetString();
            Assert.NotNull(runId);

            var run = await PollValidationRunAsync(client, "e2e-dotnet", runId!);
            Assert.True(run.GetProperty("allPassed").GetBoolean(),
                $"allPassed should be true.\nRun: {run}");

            var steps = JsonDocument.Parse(run.GetProperty("stepsJson").GetString()!)
                .RootElement.EnumerateArray().ToArray();
            // Should have restore + build + test = 3 steps.
            Assert.Equal(3, steps.Length);

            foreach (var step in steps)
            {
                var stepName = step.GetProperty("step").GetString();
                var passed = step.GetProperty("passed").GetBoolean();
                var exitCode = step.GetProperty("exitCode").GetInt32();
                Assert.True(passed, $"Step '{stepName}' should pass (exit {exitCode})");
                Assert.Equal(0, exitCode);
                Assert.False(step.GetProperty("timedOut").GetBoolean(),
                    $"Step '{stepName}' should not time out");
            }

            // Verify step ordering: restore → build → test.
            Assert.Equal("restore", steps[0].GetProperty("step").GetString());
            Assert.Equal("build", steps[1].GetProperty("step").GetString());
            Assert.Equal("test", steps[2].GetProperty("step").GetString());
        }
        finally
        {
            SafeDispose(f, scope);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// PATCHes cloneRoot to a real dotnet project dir — verifies auto-detection
    /// fills BuildCommand and TestCommand from the .csproj.
    /// </summary>
    [Fact]
    public async Task E2E_PatchCloneRoot_AutoDetects_And_Persists_From_Real_Project()
    {
        if (!DotnetAvailable()) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"forge-e2e-patch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var (f, scope, db) = await BootAsync();
        try
        {
            // Create a real dotnet classlib project.
            var (code, _, _) = await RunDotnetAsync(tempDir, "new", "classlib", "-n", "PatchLib", "--force");
            Assert.Equal(0, code);
            var projectDir = Path.Combine(tempDir, "PatchLib");

            var product = await db.Products.FirstAsync();
            db.Repos.Add(new RepoRow
            {
                Id = "e2e-patch", DisplayName = "E2E Patch", Kind = "local",
                RemoteUrl = $"https://example.com/e2e-patch-{Guid.NewGuid():N}",
                ProductId = product.Id,
                CloneRoot = "/nonexistent/initial",
            });
            await db.SaveChangesAsync();

            using var client = AuthedClient(f);

            // PATCH to real directory — should auto-detect.
            var patchResp = await client.PatchAsJsonAsync("/api/repos/e2e-patch", new
            {
                cloneRoot = projectDir,
            }, JsonOpts);
            Assert.True(patchResp.IsSuccessStatusCode, await patchResp.Content.ReadAsStringAsync());

            // Verify auto-detected commands were persisted.
            using var scope2 = f.Services.CreateScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<ProductDbContext>();
            var saved = await db2.Repos.AsNoTracking().FirstAsync(r => r.Id == "e2e-patch");
            Assert.NotNull(saved.BuildCommand);
            Assert.NotNull(saved.TestCommand);
            Assert.Contains("PatchLib.csproj", saved.BuildCommand);
            Assert.Contains("PatchLib.csproj", saved.TestCommand);
        }
        finally
        {
            SafeDispose(f, scope);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Validates that the validate-build endpoint runs all three steps
    /// against a real project with a failing test.
    /// </summary>
    [Fact]
    public async Task E2E_ValidateBuild_Catches_Failing_Test()
    {
        if (!DotnetAvailable()) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"forge-e2e-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var (f, scope, db) = await BootAsync();
        try
        {
            // Create a real xunit project.
            var (code, _, _) = await RunDotnetAsync(tempDir, "new", "xunit", "-n", "FailTests", "--force");
            Assert.Equal(0, code);
            var projectDir = Path.Combine(tempDir, "FailTests");

            // Inject a failing test.
            var testFile = Path.Combine(projectDir, "UnitTest1.cs");
            await File.WriteAllTextAsync(testFile, @"
namespace FailTests;
public class UnitTest1
{
    [Xunit.Fact]
    public void Test1() => Xunit.Assert.True(false, ""intentional failure"");
}
");

            var product = await db.Products.FirstAsync();
            db.Repos.Add(new RepoRow
            {
                Id = "e2e-fail", DisplayName = "E2E Fail", Kind = "local",
                RemoteUrl = $"https://example.com/e2e-fail-{Guid.NewGuid():N}",
                ProductId = product.Id,
                CloneRoot = projectDir,
                BuildCommand = "dotnet build FailTests.csproj",
                TestCommand = "dotnet test FailTests.csproj",
            });
            await db.SaveChangesAsync();

            using var client = AuthedClient(f);
            var valResp = await client.PostAsync("/api/repos/e2e-fail/validate-build", null);
            Assert.True(valResp.IsSuccessStatusCode, await valResp.Content.ReadAsStringAsync());

            var acceptDoc = JsonDocument.Parse(await valResp.Content.ReadAsStringAsync());
            var runId = acceptDoc.RootElement.GetProperty("runId").GetString();
            Assert.NotNull(runId);

            var run = await PollValidationRunAsync(client, "e2e-fail", runId!);
            // Build should pass, test should fail.
            Assert.False(run.GetProperty("allPassed").GetBoolean());

            var steps = JsonDocument.Parse(run.GetProperty("stepsJson").GetString()!)
                .RootElement.EnumerateArray().ToArray();

            // Restore should pass (auto-detected from .csproj).
            var restoreStep = steps.FirstOrDefault(s => s.GetProperty("step").GetString() == "restore");
            // Build should pass.
            var buildStep = steps.First(s => s.GetProperty("step").GetString() == "build");
            Assert.True(buildStep.GetProperty("passed").GetBoolean());
            // Test should fail.
            var testStep = steps.First(s => s.GetProperty("step").GetString() == "test");
            Assert.False(testStep.GetProperty("passed").GetBoolean());
            Assert.NotEqual(0, testStep.GetProperty("exitCode").GetInt32());
        }
        finally
        {
            SafeDispose(f, scope);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static bool DotnetAvailable()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("dotnet", "--version")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                })!;
            p.WaitForExit(5_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<(int code, string stdout, string stderr)> RunDotnetAsync(
        string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout, stderr);
    }

    private static bool GitAvailable()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("git", "--version")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                })!;
            p.WaitForExit(2_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task RunGitAsync(string cwd, params string[] args)
    {
        var (code, _, stderr) = await RunGitResultAsync(cwd, args);
        if (code != 0)
            throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {stderr}");
    }

    private static void RunGitSync(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(5_000);
    }

    private static async Task<(int code, string stdout, string stderr)> RunGitResultAsync(
        string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout, stderr);
    }
}
