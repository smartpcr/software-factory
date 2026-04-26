// -----------------------------------------------------------------------
// <copyright file="ForgeSeed.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Idempotent first-boot seed that imports <c>docs/examples/forge-board.html</c> sample data.
/// Values are byte-for-byte identical to the prototype's JS arrays so fidelity tests pass.
/// </summary>
public static class ForgeSeed
{
    public const string DefaultProductId = "atlas";

    public const string DefaultRepoId = "atlas-platform";

    public static async Task EnsureSeededAsync(ForgeDbContext db)
    {
        // Phase 8.3 — products / repos seed runs independently of the prototype
        // sample data so older DBs that already have epics still get a default
        // product + repo, and existing cards/workstreams/prs get repo_id backfilled.
        await EnsureDefaultProductAndRepoAsync(db);
        await BackfillRepoIdAsync(db);

        // Phase 10.6 — crew seed runs independently too so existing DBs
        // pick up the four crews on next boot.
        await EnsureSeedCrewsAsync(db);

        if (await db.Epics.AnyAsync())
        {
            return;
        }

        var serializerOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        string J(object value) => JsonSerializer.Serialize(value, serializerOpts);

        // -- epics --------------------------------------------------------
        db.Epics.AddRange(
            new EpicRow { Id = "EPIC-024", Sort = 0, Name = "Multi-tenant authentication", Status = "active",   Progress = 38,  CrewJson = J(new[] { "A1", "E4", "E7", "E9", "R3" }), Cost = 2.18, Color = "epic024" },
            new EpicRow { Id = "EPIC-021", Sort = 1, Name = "Observability v2",            Status = "active",   Progress = 62,  CrewJson = J(new[] { "E4", "E7", "R5", "Q2" }),       Cost = 1.84, Color = "epic021" },
            new EpicRow { Id = "EPIC-022", Sort = 2, Name = "Webhook reliability",         Status = "merging",  Progress = 95,  CrewJson = J(new[] { "E7", "R3" }),                   Cost = 0.94, Color = "epic022" },
            new EpicRow { Id = "EPIC-026", Sort = 3, Name = "Cluster log rotation cap",    Status = "planning", Progress = 12,  CrewJson = J(new[] { "A2", "E9" }),                   Cost = 0.41, Color = "epic026" },
            new EpicRow { Id = "EPIC-018", Sort = 4, Name = "BITS chunked download",       Status = "merging",  Progress = 100, CrewJson = J(new[] { "E4", "R3" }),                   Cost = 1.12, Color = "epic018" });

        // -- columns ------------------------------------------------------
        db.Columns.AddRange(
            new ColumnRow { Id = "backlog", Name = "Backlog", Sort = 0, WipLimit = 20 },
            new ColumnRow { Id = "design",  Name = "Design",  Sort = 1, WipLimit = 6 },
            new ColumnRow { Id = "build",   Name = "Build",   Sort = 2, WipLimit = 8 },
            new ColumnRow { Id = "review",  Name = "Review",  Sort = 3, WipLimit = 6 },
            new ColumnRow { Id = "done",    Name = "Done",    Sort = 4, WipLimit = null });

        // -- cards (verbatim from prototype, Sort assigned by index) ------
        var a2Mercer = new { id = "A2", role = "arch",  name = "Mercer" };
        var a1Atlas  = new { id = "A1", role = "arch",  name = "Atlas" };
        var e4Forge  = new { id = "E4", role = "eng",   name = "Forge" };
        var e7Rivet  = new { id = "E7", role = "eng",   name = "Rivet" };
        var e9Anvil  = new { id = "E9", role = "eng",   name = "Anvil" };
        var r3Cortex = new { id = "R3", role = "rev",   name = "Cortex" };
        var r5Lattice = new { id = "R5", role = "rev",  name = "Lattice" };
        // Phase 9.11 — XD is the operator (product owner) under the new Role enum.
        var xdHuman  = new { id = "XD", role = "product-owner", name = "Xiaodong" };

        var cards = new[]
        {
            new CardRow { Id = "#831",   Epic = "EPIC-024", Col = "backlog", Type = "story", Title = "Tenant audit log retention policy", TagsJson = J(new[] { "auth" }), Pts = 5, Priority = "low", AgentJson = null, Cost = 0 },
            new CardRow { Id = "#832",   Epic = "EPIC-024", Col = "backlog", Type = "task",  Title = "Add tenant ID to OTel span attributes", TagsJson = J(new[] { "auth", "infra" }), Pts = 2, Priority = "low", AgentJson = null, Cost = 0 },
            new CardRow { Id = "#812",   Epic = "EPIC-024", Col = "design",  Type = "story", Title = "Tenant context middleware", TagsJson = J(new[] { "auth" }), Pts = 3, Priority = "high", AgentJson = J(a2Mercer), Cost = 0.18, AutoNext = "Auto-promote to Build when spec approved", Live = true, Progress = 72 },
            new CardRow { Id = "#813",   Epic = "EPIC-024", Col = "design",  Type = "story", Title = "Tenant routing in load balancer", TagsJson = J(new[] { "auth", "infra" }), Pts = 5, Priority = "med", AgentJson = J(a1Atlas), Cost = 0.24 },
            new CardRow { Id = "#234",   Epic = "EPIC-024", Col = "build",   Type = "story", Title = "OIDC token rotation handler with key rollover", TagsJson = J(new[] { "auth" }), Pts = 5, Priority = "high", AgentJson = J(e4Forge), Cost = 0.42, Live = true, Progress = 64, AutoNext = "Open PR when tests pass", Branch = "ws/oidc-rotation" },
            new CardRow { Id = "#241",   Epic = "EPIC-024", Col = "build",   Type = "story", Title = "Per-tenant rate limit policy with sliding window", TagsJson = J(new[] { "auth" }), Pts = 5, Priority = "med", AgentJson = J(e7Rivet), Cost = 0.31, Live = true, Progress = 28, Branch = "ws/rate-limits" },
            new CardRow { Id = "#243",   Epic = "EPIC-024", Col = "build",   Type = "task",  Title = "Tenant admin UI scaffolding", TagsJson = J(new[] { "ui", "auth" }), Pts = 3, Priority = "low", AgentJson = J(e4Forge), Cost = 0.08, Live = true, Progress = 8, Branch = "ws/tenant-admin-ui" },
            new CardRow { Id = "PR #240", Epic = "EPIC-024", Col = "review", Type = "story", Title = "Add IdP failover to Microsoft Entra", TagsJson = J(new[] { "security", "auth" }), Pts = 8, Priority = "high", AgentJson = J(xdHuman), HumanGate = true, GateReason = "/security/ path requires human approval", Cost = 0.51, Branch = "ws/idp-failover" },
            new CardRow { Id = "PR #229", Epic = "EPIC-024", Col = "review", Type = "story", Title = "Token store refactor — extract to auth-core", TagsJson = J(new[] { "auth" }), Pts = 5, Priority = "med", AgentJson = J(r3Cortex), Cost = 0.34, AutoNext = "Auto-merge when CI passes" },

            new CardRow { Id = "#821",   Epic = "EPIC-021", Col = "backlog", Type = "task",  Title = "Document OTel collector deployment", TagsJson = J(new[] { "infra" }), Pts = 2, Priority = "low", AgentJson = null, Cost = 0 },
            new CardRow { Id = "#820",   Epic = "EPIC-021", Col = "design",  Type = "story", Title = "OTel span propagation across CSV cluster", TagsJson = J(new[] { "infra" }), Pts = 5, Priority = "med", AgentJson = J(a1Atlas), Cost = 0.27 },
            new CardRow { Id = "#237",   Epic = "EPIC-021", Col = "build",   Type = "story", Title = "Webhook retry with exponential backoff and jitter", TagsJson = J(new[] { "infra" }), Pts = 3, Priority = "med", AgentJson = J(e7Rivet), Cost = 0.38, Live = true, Progress = 82, Branch = "ws/retry-jitter", AutoNext = "PR ready in ~3m" },
            new CardRow { Id = "PR #231", Epic = "EPIC-021", Col = "review", Type = "story", Title = "Session manager cleanup and idle eviction", TagsJson = J(new[] { "infra" }), Pts = 3, Priority = "low", AgentJson = J(r5Lattice), Cost = 0.21 },
            new CardRow { Id = "PR #228", Epic = "EPIC-021", Col = "done",   Type = "story", Title = "Log rotation cap on CSV", TagsJson = J(new[] { "infra" }), Pts = 3, Priority = "med", AgentJson = J(e4Forge), Cost = 0.14, DoneAt = "47m ago" },
            new CardRow { Id = "PR #225", Epic = "EPIC-021", Col = "done",   Type = "bug",   Title = "Connection pool exhaustion fix", TagsJson = J(new[] { "bug", "infra" }), Pts = 2, Priority = "high", AgentJson = J(e7Rivet), Cost = 0.09, DoneAt = "3h ago" },

            new CardRow { Id = "PR #237b", Epic = "EPIC-022", Col = "review", Type = "story", Title = "DLQ routing for failed webhook events", TagsJson = J(new[] { "infra" }), Pts = 5, Priority = "med", AgentJson = J(xdHuman), HumanGate = true, GateReason = "Schema migration in /db/migrations/", Cost = 0.41 },
            new CardRow { Id = "PR #226",  Epic = "EPIC-022", Col = "done",   Type = "task",  Title = "Dashboard metrics card refactor", TagsJson = J(new[] { "ui" }), Pts = 2, Priority = "low", AgentJson = J(e7Rivet), Cost = 0.06, DoneAt = "12m ago" },

            new CardRow { Id = "#828",   Epic = "EPIC-026", Col = "backlog", Type = "task",  Title = "CAU node ordering for 6-node clusters", TagsJson = J(new[] { "infra" }), Pts = 5, Priority = "med", AgentJson = null, Cost = 0 },
            new CardRow { Id = "#825",   Epic = "EPIC-026", Col = "backlog", Type = "bug",   Title = "WMI quota violation telemetry", TagsJson = J(new[] { "bug", "infra" }), Pts = 3, Priority = "low", AgentJson = null, Cost = 0 },
            new CardRow { Id = "#822",   Epic = "EPIC-026", Col = "design",  Type = "story", Title = "Log rotation policy schema", TagsJson = J(new[] { "infra" }), Pts = 5, Priority = "med", AgentJson = J(a2Mercer), Cost = 0.23, Live = true, Progress = 41 },
            new CardRow { Id = "#809",   Epic = "EPIC-026", Col = "build",   Type = "bug",   Title = "Cluster log rotation patch", TagsJson = J(new[] { "bug", "infra" }), Pts = 8, Priority = "high", AgentJson = J(e9Anvil), Cost = 0.18, Blocked = true, BlockedReason = "Waiting on schema review" },

            new CardRow { Id = "PR #218", Epic = "EPIC-018", Col = "done", Type = "story", Title = "Resumable downloads on CSV", TagsJson = J(new[] { "infra" }), Pts = 8, Priority = "high", AgentJson = J(e4Forge), Cost = 0.78, DoneAt = "6d ago" },
            new CardRow { Id = "PR #220", Epic = "EPIC-018", Col = "done", Type = "task",  Title = "BITS chunk size telemetry", TagsJson = J(new[] { "infra" }), Pts = 2, Priority = "low",  AgentJson = J(e7Rivet), Cost = 0.08, DoneAt = "5d ago" },
        };
        for (var i = 0; i < cards.Length; i++) { cards[i].Sort = i; }
        db.Cards.AddRange(cards);

        // -- workstreams --------------------------------------------------
        var workstreams = new[]
        {
            new WorkstreamRow { Id = "ws-oidc",          Name = "oidc-rotation",   Branch = "ws/oidc-rotation",   Base = "main", Epic = "EPIC-024", Status = "active",   WorktreeJson = J(new[] { "C:\\forge\\atlas\\wt", "oidc-rotation" }),    Created = -3,   LastActive = -0.01,  AgentsJson = J(new[] { "E4" }),       PrsJson = J(new[] { "#234" }),    CardsJson = J(new[] { "#234" }),     Commits = 12, Ahead = 8,  Behind = 0, Conflicts = 0, Summary = "Rotates OIDC signing keys on a 24h cadence with 60s grace window. Live build in flight.", BarStart = -3,   BarEnd = 1.5 },
            new WorkstreamRow { Id = "ws-rate-limits",   Name = "rate-limits",     Branch = "ws/rate-limits",     Base = "main", Epic = "EPIC-024", Status = "active",   WorktreeJson = J(new[] { "C:\\forge\\atlas\\wt", "rate-limits" }),     Created = -2,   LastActive = -0.05,  AgentsJson = J(new[] { "E7" }),       PrsJson = J(new[] { "#241" }),    CardsJson = J(new[] { "#241" }),     Commits = 6,  Ahead = 4,  Behind = 1, Conflicts = 0, Summary = "Per-tenant sliding-window rate limit policy. Implementation 28% — tests not yet written.", BarStart = -2,   BarEnd = 4 },
            new WorkstreamRow { Id = "ws-idp-failover",  Name = "idp-failover",    Branch = "ws/idp-failover",    Base = "main", Epic = "EPIC-024", Status = "review",   WorktreeJson = J(new[] { "C:\\forge\\atlas\\wt", "idp-failover" }),    Created = -8,   LastActive = -0.2,   AgentsJson = J(new[] { "E9", "R3" }), PrsJson = J(new[] { "#240" }),    CardsJson = J(new[] { "PR #240" }),  Commits = 23, Ahead = 18, Behind = 0, Conflicts = 0, Summary = "Microsoft Entra fail-over path. Awaiting human gate (touches /security/).", BarStart = -8,   BarEnd = 0.2 },
            new WorkstreamRow { Id = "ws-retry-jitter",  Name = "retry-jitter",    Branch = "ws/retry-jitter",    Base = "main", Epic = "EPIC-021", Status = "active",   WorktreeJson = J(new[] { "C:\\forge\\atlas\\wt", "retry-jitter" }),    Created = -1,   LastActive = -0.005, AgentsJson = J(new[] { "E7" }),       PrsJson = J(new[] { "#237" }),    CardsJson = J(new[] { "#237" }),     Commits = 5,  Ahead = 5,  Behind = 0, Conflicts = 0, Summary = "Webhook retry with exponential backoff and jitter. Tests passing — opening PR shortly.", BarStart = -1,   BarEnd = 0.5 },
            new WorkstreamRow { Id = "ws-dlq",           Name = "dlq-routing",     Branch = "ws/dlq-routing",     Base = "main", Epic = "EPIC-022", Status = "review",   WorktreeJson = J(new[] { "C:\\forge\\atlas\\wt", "dlq-routing" }),     Created = -7,   LastActive = -0.5,   AgentsJson = J(new[] { "E7", "R3", "XD" }), PrsJson = J(new[] { "#237b" }), CardsJson = J(new[] { "PR #237b" }), Commits = 19, Ahead = 0,  Behind = 2, Conflicts = 1, Summary = "Dead-letter routing for failed webhook events. Schema migration → human gate. Has merge conflict in /db/migrations/.", BarStart = -7, BarEnd = 0 },
            new WorkstreamRow { Id = "ws-tenant-admin",  Name = "tenant-admin-ui", Branch = "ws/tenant-admin-ui", Base = "main", Epic = "EPIC-024", Status = "active",   WorktreeJson = J(new[] { "C:\\forge\\atlas\\wt", "tenant-admin-ui" }), Created = -0.5, LastActive = -0.02,  AgentsJson = J(new[] { "E4" }),       PrsJson = J(Array.Empty<string>()), CardsJson = J(new[] { "#243" }),     Commits = 1,  Ahead = 1,  Behind = 0, Conflicts = 0, Summary = "Tenant admin UI scaffold. Just spawned — agent walking the design.", BarStart = -0.5, BarEnd = 3 },
            new WorkstreamRow { Id = "ws-log-rotation",  Name = "log-rotation-cap", Branch = "ws/log-rotation-cap", Base = "main", Epic = "EPIC-026", Status = "planning", WorktreeJson = J(new[] { "(not yet created)" }),                          Created = 0,    LastActive = 0,      AgentsJson = J(new[] { "A2" }),       PrsJson = J(Array.Empty<string>()), CardsJson = J(new[] { "#822", "#809" }), Commits = 0, Ahead = 0, Behind = 0, Conflicts = 0, Summary = "Schema design phase. Worktree spawns when design approved.", BarStart = 0.5, BarEnd = 5, Planned = true },
            new WorkstreamRow { Id = "ws-bits-cleanup",  Name = "bits-cleanup",    Branch = "ws/bits-cleanup",    Base = "main", Epic = "EPIC-018", Status = "merging",  WorktreeJson = J(new[] { "C:\\forge\\atlas\\wt", "bits-cleanup" }),    Created = -6,   LastActive = -1,     AgentsJson = J(new[] { "R3" }),       PrsJson = J(new[] { "#218", "#220" }), CardsJson = J(new[] { "PR #218", "PR #220" }), Commits = 22, Ahead = 0, Behind = 0, Conflicts = 0, Summary = "Final cleanup commits for the BITS epic. Both PRs merged — pruning worktree.", BarStart = -6, BarEnd = -0.5 },
        };
        for (var i = 0; i < workstreams.Length; i++) { workstreams[i].Sort = i; }
        db.Workstreams.AddRange(workstreams);

        // -- workstream events -------------------------------------------
        var wsEvents = new[]
        {
            new WsEventRow { Ws = "ws-oidc",         Day = -2,   Kind = "pr",     Label = "PR #234" },
            new WsEventRow { Ws = "ws-rate-limits",  Day = -1,   Kind = "pr",     Label = "PR #241" },
            new WsEventRow { Ws = "ws-idp-failover", Day = -3,   Kind = "pr",     Label = "PR #240" },
            new WsEventRow { Ws = "ws-idp-failover", Day = -0.5, Kind = "review", Label = "Human gate" },
            new WsEventRow { Ws = "ws-retry-jitter", Day = 0.3,  Kind = "pr",     Label = "PR #237" },
            new WsEventRow { Ws = "ws-dlq",          Day = -3,   Kind = "pr",     Label = "PR #237b" },
            new WsEventRow { Ws = "ws-dlq",          Day = -0.6, Kind = "block",  Label = "Conflict" },
            new WsEventRow { Ws = "ws-bits-cleanup", Day = -1,   Kind = "merge",  Label = "merged" },
        };
        for (var i = 0; i < wsEvents.Length; i++) { wsEvents[i].Sort = i; }
        db.WsEvents.AddRange(wsEvents);

        // -- prs ----------------------------------------------------------
        var prs = new[]
        {
            new PrRow { Id = "PR #234",  Col = "build",          Epic = "EPIC-024", EpicColor = "epic024", Workstream = "ws/oidc-rotation",       Base = "main", Branch = "ws/oidc-rotation",       Title = "OIDC token rotation handler with key rollover",       AuthorJson = J(e4Forge), Additions = 287, Deletions = 41,  Files = 8,  Commits = 12, Age = "3h",     BuildJson = J(new { status = "running",  step = "Tests · 98 / 142",     percent = 68 }),  ReviewJson = null, Conflicts = false, HumanGate = false },
            new PrRow { Id = "PR #241",  Col = "implementation", Epic = "EPIC-024", EpicColor = "epic024", Workstream = "ws/rate-limits",         Base = "main", Branch = "ws/rate-limits",         Title = "Per-tenant rate limit policy with sliding window",    AuthorJson = J(e7Rivet), Additions = 102, Deletions = 8,   Files = 4,  Commits = 6,  Age = "1h",     BuildJson = J(new { status = "queued",   step = "queued behind 1",      percent = 0 }),   ReviewJson = null, Conflicts = false, HumanGate = false, ImplJson = J(new { phase = "writing tests", step = "sliding-window store", percent = 28 }) },
            new PrRow { Id = "PR #243",  Col = "implementation", Epic = "EPIC-024", EpicColor = "epic024", Workstream = "ws/tenant-admin-ui",     Base = "main", Branch = "ws/tenant-admin-ui",     Title = "Tenant admin UI scaffold (just spawned)",             AuthorJson = J(e4Forge), Additions = 18,  Deletions = 0,   Files = 2,  Commits = 1,  Age = "12m",    BuildJson = null, ReviewJson = null, Conflicts = false, HumanGate = false, ImplJson = J(new { phase = "reading spec", step = "route layout", percent = 8 }) },
            new PrRow { Id = "PR #237",  Col = "build",          Epic = "EPIC-021", EpicColor = "epic021", Workstream = "ws/retry-jitter",        Base = "main", Branch = "ws/retry-jitter",        Title = "Webhook retry — exponential backoff + jitter",        AuthorJson = J(e7Rivet), Additions = 64,  Deletions = 22,  Files = 3,  Commits = 5,  Age = "40m",    BuildJson = J(new { status = "running",  step = "Tests · 142 / 142",     percent = 92 }),  ReviewJson = J(new { agent = "R3", name = "Cortex",  verdict = "approved", summary = "Backoff math is sound. Verified jitter caps at 30s. Approved." }), Conflicts = false, HumanGate = false },
            new PrRow { Id = "PR #240",  Col = "review",         Epic = "EPIC-024", EpicColor = "epic024", Workstream = "ws/idp-failover",        Base = "main", Branch = "ws/idp-failover",        Title = "Add IdP fail-over to Microsoft Entra",                AuthorJson = J(e9Anvil), Additions = 412, Deletions = 76,  Files = 19, Commits = 23, Age = "6h",     BuildJson = J(new { status = "passing",  step = "CI green · 3m 12s",     percent = 100 }), ReviewJson = J(new { agent = "R3", name = "Cortex",  verdict = "changes",  summary = "Token cache TTL needs to handle clock skew. Suggested 30s buffer in <span class=\"hl\">authcache.go:142</span>." }), Conflicts = false, HumanGate = true, GateReason = "/security/ path · human approval required" },
            new PrRow { Id = "PR #229",  Col = "review",         Epic = "EPIC-024", EpicColor = "epic024", Workstream = "ws/auth-core",           Base = "main", Branch = "ws/auth-core-extract",   Title = "Extract token store to auth-core package",            AuthorJson = J(e4Forge), Additions = 198, Deletions = 234, Files = 12, Commits = 7,  Age = "2h",     BuildJson = J(new { status = "passing",  step = "CI green · 2m 41s",     percent = 100 }), ReviewJson = J(new { agent = "R3", name = "Cortex",  verdict = "approved", summary = "Clean refactor. No behavioural changes detected by trace diff. Auto-merge armed." }), Conflicts = false, HumanGate = false, AutoMerge = true },
            new PrRow { Id = "PR #237b", Col = "review",         Epic = "EPIC-022", EpicColor = "epic022", Workstream = "ws/dlq-routing",         Base = "main", Branch = "ws/dlq-routing",         Title = "DLQ routing for failed webhook events",               AuthorJson = J(e7Rivet), Additions = 156, Deletions = 31,  Files = 7,  Commits = 19, Age = "8h",     BuildJson = J(new { status = "passing",  step = "CI green · 4m 02s",     percent = 100 }), ReviewJson = J(new { agent = "R3", name = "Cortex",  verdict = "changes",  summary = "Schema migration touches live tables. Recommend two-phase migration (add column nullable → backfill → enforce)." }), Conflicts = true, ConflictPaths = "db/migrations/2026_03_dlq.sql", HumanGate = true, GateReason = "Schema migration · human gate" },
            new PrRow { Id = "PR #231",  Col = "build",          Epic = "EPIC-021", EpicColor = "epic021", Workstream = "ws/session-cleanup",     Base = "main", Branch = "ws/session-cleanup",     Title = "Session manager cleanup + idle eviction",             AuthorJson = J(e7Rivet), Additions = 84,  Deletions = 51,  Files = 4,  Commits = 9,  Age = "5h",     BuildJson = J(new { status = "failing",  step = "TestIdleEviction.fail", percent = 60, failingTest = "TestIdleEviction" }), ReviewJson = J(new { agent = "R5", name = "Lattice", verdict = "pending",  summary = "Waiting on green build before reviewing." }), Conflicts = false, HumanGate = false },
            new PrRow { Id = "PR #244",  Col = "implementation", Epic = "EPIC-026", EpicColor = "epic026", Workstream = "ws/log-rotation-cap",    Base = "main", Branch = "ws/log-rotation-cap",    Title = "Cluster log rotation cap implementation",             AuthorJson = J(e9Anvil), Additions = 0,   Deletions = 0,   Files = 0,  Commits = 0,  Age = "queued", BuildJson = null, ReviewJson = null, Conflicts = false, HumanGate = false, Blocked = true, ImplJson = J(new { phase = "blocked", step = "Waiting on schema review · #822", percent = 0 }) },
        };
        for (var i = 0; i < prs.Length; i++) { prs[i].Sort = i; }
        db.Prs.AddRange(prs);

        // -- ws stats -----------------------------------------------------
        db.WsStats.AddRange(
            new WsStatRow { Sort = 0, Label = "Active workstreams", Val = "5",     Unit = "of 8",        Trend = "up",   TrendVal = "+2 this sprint" },
            new WsStatRow { Sort = 1, Label = "Open PRs",           Val = "9",     Unit = "open",        Trend = "flat", TrendVal = "2 awaiting approval" },
            new WsStatRow { Sort = 2, Label = "Merged today",       Val = "3",     Unit = "PRs",         Trend = "up",   TrendVal = "+1 vs avg" },
            new WsStatRow { Sort = 3, Label = "Total cost · 24h",   Val = "$4.81", Unit = "/$7.50 cap",  Trend = "down", TrendVal = "64% of budget" });

        // -- overview activity -------------------------------------------
        db.OverviewActivity.AddRange(
            new OverviewActivityRow { Sort = 0, Time = "12:48", Who = "CI",          Msg = "<span class=\"ref\">PR #228</span> merged · log rotation cap shipped" },
            new OverviewActivityRow { Sort = 1, Time = "12:31", Who = "E-04 Forge",  Msg = "pushed 3 commits to <span class=\"ref\">ws/oidc-rotation</span>" },
            new OverviewActivityRow { Sort = 2, Time = "12:18", Who = "R-03 Cortex", Msg = "flagged <span class=\"ref\">PR #240</span> for human review · /security/ path" },
            new OverviewActivityRow { Sort = 3, Time = "11:52", Who = "A-02 Mercer", Msg = "drafted <span class=\"ref\">ADR-021</span> · log rotation policy schema" },
            new OverviewActivityRow { Sort = 4, Time = "11:09", Who = "E-07 Rivet",  Msg = "opened <span class=\"ref\">PR #241</span> · per-tenant rate limit" },
            new OverviewActivityRow { Sort = 5, Time = "10:45", Who = "ADO sync",    Msg = "imported 3 work items from sprint 14 backlog" },
            new OverviewActivityRow { Sort = 6, Time = "10:12", Who = "XD Xiaodong", Msg = "spawned workstream <span class=\"ref\">ws/tenant-admin-ui</span>" },
            new OverviewActivityRow { Sort = 7, Time = "09:38", Who = "E-04 Forge",  Msg = "created branch <span class=\"ref\">ws/tenant-admin-ui</span> off main" });

        // -- overview agents ---------------------------------------------
        var overviewAgents = new[]
        {
            new OverviewAgentRow { Id = "A1", Name = "Atlas",   Role = "arch", Status = "working", Task = "#820 OTel propagation" },
            new OverviewAgentRow { Id = "A2", Name = "Mercer",  Role = "arch", Status = "working", Task = "#822 log schema design" },
            new OverviewAgentRow { Id = "E4", Name = "Forge",   Role = "eng",  Status = "working", Task = "#234 OIDC rotation (build)" },
            new OverviewAgentRow { Id = "E7", Name = "Rivet",   Role = "eng",  Status = "working", Task = "#237 retry/jitter" },
            new OverviewAgentRow { Id = "E9", Name = "Anvil",   Role = "eng",  Status = "queued",  Task = "awaiting #822 schema" },
            new OverviewAgentRow { Id = "R3", Name = "Cortex",  Role = "rev",  Status = "working", Task = "PR #240 review" },
            new OverviewAgentRow { Id = "R5", Name = "Lattice", Role = "rev",  Status = "idle",    Task = "—" },
            new OverviewAgentRow { Id = "Q2", Name = "Probe",   Role = "qa",   Status = "idle",    Task = "—" },
        };
        for (var i = 0; i < overviewAgents.Length; i++) { overviewAgents[i].Sort = i; }
        db.OverviewAgents.AddRange(overviewAgents);

        // Phase 8.3 — pin every freshly-seeded row to the default repo.
        foreach (var c in cards) c.RepoId = DefaultRepoId;
        foreach (var w in workstreams) w.RepoId = DefaultRepoId;
        foreach (var p in prs) p.RepoId = DefaultRepoId;

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Idempotent: creates the default <c>atlas</c> product and the
    /// <c>atlas-platform</c> repo if they don't yet exist. Used both by the
    /// boot path and by Phase 8.3 backfill against pre-existing dev DBs.
    /// </summary>
    private static async Task EnsureDefaultProductAndRepoAsync(ForgeDbContext db)
    {
        var changed = false;

        if (!await db.Products.AnyAsync(p => p.Id == DefaultProductId))
        {
            db.Products.Add(new ProductRow
            {
                Id = DefaultProductId,
                Sort = 0,
                Name = "Atlas",
                Slug = "atlas",
                Description = "Default product. Backfilled from the prototype's single-repo seed.",
                OwnerUserId = null,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            changed = true;
        }

        if (!await db.Repos.AnyAsync(r => r.Id == DefaultRepoId))
        {
            db.Repos.Add(new RepoRow
            {
                Id = DefaultRepoId,
                Sort = 0,
                ProductId = DefaultProductId,
                Kind = "ado",
                RemoteUrl = "https://dev.azure.com/atlas/atlas-platform/_git/atlas-platform",
                DisplayName = "atlas-platform",
                DefaultBranch = "main",
                CloneRoot = @"C:\forge\atlas\atlas-platform",
                AdoOrg = "atlas",
                AdoProject = "atlas-platform",
            });
            changed = true;
        }

        if (changed) await db.SaveChangesAsync();
    }

    /// <summary>
    /// Idempotent: ensure the four prototype-derived crews exist. Per the
    /// Phase 10.6 spec, default model assignments are:
    ///   Engineering → claude-opus-4-7
    ///   Review → claude-sonnet-4-6
    ///   Architecture → claude-opus-4-7
    ///   QA → claude-haiku-4-5
    /// Pricing is denormalised onto the crew so historical salary lookups
    /// stay reproducible across price changes (Phase 11.2 cost ledger).
    /// </summary>
    private static async Task EnsureSeedCrewsAsync(ForgeDbContext db)
    {
        var existing = await db.Crews.Select(c => c.Id).ToListAsync();
        if (existing.Count >= 4) return;

        var crews = new[]
        {
            new CrewRow { Id = "crew-eng",  Sort = 0, Name = "Engineering",  ProductId = DefaultProductId, Summary = "Cuts PRs, ships features, keeps tests green.",        Lead = "E4", Model = "claude-opus-4-7",        ModelInputPricePerMTok = 15m, ModelOutputPricePerMTok = 75m },
            new CrewRow { Id = "crew-rev",  Sort = 1, Name = "Review",       ProductId = DefaultProductId, Summary = "Reads every diff, catches what engineers missed.",   Lead = "R3", Model = "claude-sonnet-4-6",       ModelInputPricePerMTok = 3m,  ModelOutputPricePerMTok = 15m },
            new CrewRow { Id = "crew-arch", Sort = 2, Name = "Architecture", ProductId = DefaultProductId, Summary = "Frames problems, owns ADRs, keeps blast radii small.", Lead = "A1", Model = "claude-opus-4-7",        ModelInputPricePerMTok = 15m, ModelOutputPricePerMTok = 75m },
            new CrewRow { Id = "crew-qa",   Sort = 3, Name = "QA",           ProductId = DefaultProductId, Summary = "Fuzzes, soaks, watches the dials.",                   Lead = "Q2", Model = "claude-haiku-4-5-20251001", ModelInputPricePerMTok = 1m,  ModelOutputPricePerMTok = 5m },
        };
        foreach (var c in crews.Where(c => !existing.Contains(c.Id))) db.Crews.Add(c);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Idempotent: any cards / workstreams / prs that don't yet have a
    /// <c>RepoId</c> are pinned to the default repo. Phase 8.2's migration
    /// adds the column nullable; Phase 8.3 fills it in.
    /// </summary>
    private static async Task BackfillRepoIdAsync(ForgeDbContext db)
    {
        var changed = false;
        await foreach (var c in db.Cards.Where(c => c.RepoId == null).AsAsyncEnumerable())
        {
            c.RepoId = DefaultRepoId;
            changed = true;
        }
        await foreach (var w in db.Workstreams.Where(w => w.RepoId == null).AsAsyncEnumerable())
        {
            w.RepoId = DefaultRepoId;
            changed = true;
        }
        await foreach (var p in db.Prs.Where(p => p.RepoId == null).AsAsyncEnumerable())
        {
            p.RepoId = DefaultRepoId;
            changed = true;
        }
        if (changed) await db.SaveChangesAsync();
    }
}
