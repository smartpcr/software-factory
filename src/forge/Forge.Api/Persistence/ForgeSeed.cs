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

    /// <summary>
    /// Reconcile the static crew + prototype-agent catalog. Safe to call on
    /// every boot against a populated prod DB: structural fields on the
    /// canonical 8 crews are snapped back to spec, the prototype agents
    /// (A1/A2/E4/E7/E9/R3/R5/Q2/XD) get their Role re-pulled from
    /// <c>agents/allocation.json</c>, and lead↔CrewId mismatches are healed.
    /// Does NOT seed prototype cards/PRs/workstreams (that path lives in
    /// <see cref="EnsureSeededAsync"/> and is gated on empty epics).
    /// </summary>
    public static async Task EnsureCrewCatalogAsync(ForgeDbContext db)
    {
        await EnsureSeedCrewsAsync(db);
        await EnsureSeedAgentsAsync(db);
        await EnsureRosterBackfillAsync(db);
    }

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

        // Phase 11.3 — first-class agent rows. Idempotent.
        await EnsureSeedAgentsAsync(db);

        // Phase 18 — split half of each team's agents to its night sibling
        // crew. Runs every boot; rebalances if the current split is uneven.
        await EnsureNightShiftSplitAsync(db);

        // Phase 19 — ensure every agent with a crew assignment has a
        // CrewMemberRow entry (the formal roster). Leads are tracked via
        // CrewRow.Lead and are excluded to avoid double-counting.
        await EnsureRosterBackfillAsync(db);

        if (!await db.Epics.AnyAsync())
        {
            await SeedPrototypeDataAsync(db);
        }

        // Phase 13 — rename the prototype's mocked agents (Forge/Rivet/Anvil/
        // Cortex/Lattice/Atlas/Mercer) to the Ne Zha character crew. Runs every
        // boot but is a no-op once the DB rows already carry the new names, so
        // existing data (synced ADO work items, audit history) is preserved.
        await RenameMockedAgentsAsync(db);

        // Phase 12.2 — backfill work_items from existing cards. Idempotent.
        // Runs after the prototype-data seed so first-boot picks up the 23
        // default cards; subsequent boots no-op because every id already
        // exists in work_items.
        await BackfillWorkItemsFromCardsAsync(db);
    }

    /// <summary>
    /// Replace the prototype's mocked agent names in every JSON-bearing column
    /// (cards.AgentJson, prs.AuthorJson, prs.ReviewJson, audit.WhoJson, etc.)
    /// and in plain string columns (overview_agents.Name) with the matching
    /// character from src/forge/Forge.UI/src/crew.ts. Idempotent: each
    /// replacement only runs when the *old* string is still present.
    /// </summary>
    private static async Task RenameMockedAgentsAsync(ForgeDbContext db)
    {
        var renames = new Dictionary<string, string>
        {
            ["Atlas"]   = "Yuan Shi Tianzun",
            ["Mercer"]  = "Wen Zhong",
            ["Forge"]   = "Ne Zha",
            ["Rivet"]   = "Ao Bing",
            ["Anvil"]   = "Zhu Rong",
            ["Cortex"]  = "Shen Gongbao",
            ["Lattice"] = "Yun Xiao",
        };

        // Bulk update is the cheapest path here — the string columns are small
        // and SQLite's REPLACE() is happy to no-op when the substring is gone.
        // We scope each REPLACE to the columns that actually carry agent names
        // so a stray match in a card title can't corrupt unrelated data.
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        // Heal an earlier accidental rename: the default product was named
        // "Atlas" before this migration learned to skip the products table.
        await using (var heal = conn.CreateCommand())
        {
            heal.CommandText = "UPDATE products SET Name = 'Atlas' WHERE Id = 'atlas' AND Name = 'Yuan Shi Tianzun';";
            await heal.ExecuteNonQueryAsync();
        }

        // Phase 13 — Q2 was the unnamed "Probe" row before the Ne Zha rename;
        // promote it to Shun Feng'er to match the soul.md persona.
        await using (var probe = conn.CreateCommand())
        {
            probe.CommandText = @"
                UPDATE overview_agents SET Name = 'Shun Feng''er' WHERE Id = 'Q2' AND Name = 'Probe';
                UPDATE agents          SET Name = 'Shun Feng''er' WHERE Id = 'Q2' AND Name = 'Probe';";
            await probe.ExecuteNonQueryAsync();
        }

        foreach (var (oldName, newName) in renames)
        {
            // Quoted form (`"name":"Forge"`) inside JSON fields. We wrap with
            // the surrounding quotes so we don't accidentally rewrite a card
            // title that legitimately mentions the word "Forge".
            var jsonOld = "\"" + oldName + "\"";
            var jsonNew = "\"" + newName + "\"";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE cards            SET AgentJson  = REPLACE(AgentJson,  $jsonOld, $jsonNew) WHERE AgentJson  LIKE $jsonLike;
                UPDATE prs              SET AuthorJson = REPLACE(AuthorJson, $jsonOld, $jsonNew) WHERE AuthorJson LIKE $jsonLike;
                UPDATE prs              SET ReviewJson = REPLACE(ReviewJson, $jsonOld, $jsonNew) WHERE ReviewJson LIKE $jsonLike;
                UPDATE work_items       SET AssignedToJson = REPLACE(AssignedToJson, $jsonOld, $jsonNew) WHERE AssignedToJson LIKE $jsonLike;
                UPDATE overview_agents  SET Name = $newName WHERE Name = $oldName;
                UPDATE agents           SET Name = $newName WHERE Name = $oldName;
            ";
            void AddParam(string name, object value)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = name;
                p.Value = value;
                cmd.Parameters.Add(p);
            }
            AddParam("$oldName", oldName);
            AddParam("$newName", newName);
            AddParam("$jsonOld", jsonOld);
            AddParam("$jsonNew", jsonNew);
            AddParam("$jsonLike", "%" + jsonOld + "%");
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Original Phase-0 prototype seed extracted into its own method so the
    /// Phase 12 backfill can run reliably after it on first boot.
    /// </summary>
    private static async Task SeedPrototypeDataAsync(ForgeDbContext db)
    {

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
        var a2Mercer = new { id = "A2", role = "arch",  name = "Wen Zhong" };
        var a1Atlas  = new { id = "A1", role = "arch",  name = "Yuan Shi Tianzun" };
        var e4Forge  = new { id = "E4", role = "eng",   name = "Ne Zha" };
        var e7Rivet  = new { id = "E7", role = "eng",   name = "Ao Bing" };
        var e9Anvil  = new { id = "E9", role = "eng",   name = "Zhu Rong" };
        var r3Cortex = new { id = "R3", role = "rev",   name = "Shen Gongbao" };
        var r5Lattice = new { id = "R5", role = "rev",  name = "Yun Xiao" };
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
            new PrRow { Id = "PR #237",  Col = "build",          Epic = "EPIC-021", EpicColor = "epic021", Workstream = "ws/retry-jitter",        Base = "main", Branch = "ws/retry-jitter",        Title = "Webhook retry — exponential backoff + jitter",        AuthorJson = J(e7Rivet), Additions = 64,  Deletions = 22,  Files = 3,  Commits = 5,  Age = "40m",    BuildJson = J(new { status = "running",  step = "Tests · 142 / 142",     percent = 92 }),  ReviewJson = J(new { agent = "R3", name = "Shen Gongbao",  verdict = "approved", summary = "Backoff math is sound. Verified jitter caps at 30s. Approved." }), Conflicts = false, HumanGate = false },
            new PrRow { Id = "PR #240",  Col = "review",         Epic = "EPIC-024", EpicColor = "epic024", Workstream = "ws/idp-failover",        Base = "main", Branch = "ws/idp-failover",        Title = "Add IdP fail-over to Microsoft Entra",                AuthorJson = J(e9Anvil), Additions = 412, Deletions = 76,  Files = 19, Commits = 23, Age = "6h",     BuildJson = J(new { status = "passing",  step = "CI green · 3m 12s",     percent = 100 }), ReviewJson = J(new { agent = "R3", name = "Shen Gongbao",  verdict = "changes",  summary = "Token cache TTL needs to handle clock skew. Suggested 30s buffer in <span class=\"hl\">authcache.go:142</span>." }), Conflicts = false, HumanGate = true, GateReason = "/security/ path · human approval required" },
            new PrRow { Id = "PR #229",  Col = "review",         Epic = "EPIC-024", EpicColor = "epic024", Workstream = "ws/auth-core",           Base = "main", Branch = "ws/auth-core-extract",   Title = "Extract token store to auth-core package",            AuthorJson = J(e4Forge), Additions = 198, Deletions = 234, Files = 12, Commits = 7,  Age = "2h",     BuildJson = J(new { status = "passing",  step = "CI green · 2m 41s",     percent = 100 }), ReviewJson = J(new { agent = "R3", name = "Shen Gongbao",  verdict = "approved", summary = "Clean refactor. No behavioural changes detected by trace diff. Auto-merge armed." }), Conflicts = false, HumanGate = false, AutoMerge = true },
            new PrRow { Id = "PR #237b", Col = "review",         Epic = "EPIC-022", EpicColor = "epic022", Workstream = "ws/dlq-routing",         Base = "main", Branch = "ws/dlq-routing",         Title = "DLQ routing for failed webhook events",               AuthorJson = J(e7Rivet), Additions = 156, Deletions = 31,  Files = 7,  Commits = 19, Age = "8h",     BuildJson = J(new { status = "passing",  step = "CI green · 4m 02s",     percent = 100 }), ReviewJson = J(new { agent = "R3", name = "Shen Gongbao",  verdict = "changes",  summary = "Schema migration touches live tables. Recommend two-phase migration (add column nullable → backfill → enforce)." }), Conflicts = true, ConflictPaths = "db/migrations/2026_03_dlq.sql", HumanGate = true, GateReason = "Schema migration · human gate" },
            new PrRow { Id = "PR #231",  Col = "build",          Epic = "EPIC-021", EpicColor = "epic021", Workstream = "ws/session-cleanup",     Base = "main", Branch = "ws/session-cleanup",     Title = "Session manager cleanup + idle eviction",             AuthorJson = J(e7Rivet), Additions = 84,  Deletions = 51,  Files = 4,  Commits = 9,  Age = "5h",     BuildJson = J(new { status = "failing",  step = "TestIdleEviction.fail", percent = 60, failingTest = "TestIdleEviction" }), ReviewJson = J(new { agent = "R5", name = "Yun Xiao", verdict = "pending",  summary = "Waiting on green build before reviewing." }), Conflicts = false, HumanGate = false },
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
            new OverviewAgentRow { Id = "A1", Name = "Yuan Shi Tianzun",   Role = "arch", Status = "working", Task = "#820 OTel propagation" },
            new OverviewAgentRow { Id = "A2", Name = "Wen Zhong",  Role = "arch", Status = "working", Task = "#822 log schema design" },
            new OverviewAgentRow { Id = "E4", Name = "Ne Zha",   Role = "eng",  Status = "working", Task = "#234 OIDC rotation (build)" },
            new OverviewAgentRow { Id = "E7", Name = "Ao Bing",   Role = "eng",  Status = "working", Task = "#237 retry/jitter" },
            new OverviewAgentRow { Id = "E9", Name = "Zhu Rong",   Role = "eng",  Status = "queued",  Task = "awaiting #822 schema" },
            new OverviewAgentRow { Id = "R3", Name = "Shen Gongbao",  Role = "rev",  Status = "working", Task = "PR #240 review" },
            new OverviewAgentRow { Id = "R5", Name = "Yun Xiao", Role = "rev",  Status = "idle",    Task = "—" },
            new OverviewAgentRow { Id = "Q2", Name = "Shun Feng'er", Role = "qa",   Status = "idle",    Task = "—" },
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
    public static async Task EnsureDefaultProductAndRepoAsync(ForgeDbContext db)
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
    /// <summary>
    /// Phase 11.3 — seed first-class <see cref="AgentRow"/>s mirroring the
    /// prototype's overview agents, mapping each to the right crew by id
    /// prefix (E* → engineering, R* → review, A* → architecture, Q* → qa).
    /// Idempotent on agent id.
    /// </summary>
    private static async Task EnsureSeedAgentsAsync(ForgeDbContext db)
    {
        var existing = await db.Agents.ToListAsync();
        var existingIds = existing.Select(a => a.Id).ToHashSet();

        // Phase 13 — every crew member is sourced from agents/allocation.json.
        // The eight prototype rows (A1/A2/E4/E7/E9/R3/R5/Q2/XD) keep their
        // legacy ids for compatibility with old screenshots / tests; their
        // SoulPath is wired to the matching character file. The remaining
        // characters seed with their character codes.
        var repoRoot = FindRepoRoot();
        string SoulFor(string characterId) =>
            Path.Combine(repoRoot, "agents", characterId, "soul.md");

        // Legacy aliases: prototype id → character id. Names line up with the
        // RenameMockedAgentsAsync pass run earlier in EnsureSeededAsync.
        var prototypeAlias = new Dictionary<string, string>
        {
            ["A1"] = "YS",
            ["A2"] = "WZ",
            ["E4"] = "NZ",
            ["E7"] = "AB",
            ["E9"] = "ZR",
            ["R3"] = "SG",
            ["R5"] = "YX",
            ["Q2"] = "SF",
            ["XD"] = "XD",
        };

        var allocation = LoadAllocation(repoRoot);
        // Map character-id → allocation entry for O(1) lookup.
        var byCharId = allocation.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        // -- 1) Heal the eight prototype rows (and XD): name + SoulPath +
        // Role come from the matching allocation entry under the alias
        // mapping. Role is RECONCILED (snapped back to allocation.json on
        // every boot) so test mutations cannot stick. CrewId is left to
        // EnsureRosterBackfillAsync below — it knows about Lead↔CrewId
        // invariants that the alias map alone can't see (e.g. crew-eng
        // is led by E4 even though E4's crew_role is "arch").
        var sort = 0;
        foreach (var (proto, charId) in prototypeAlias)
        {
            if (!byCharId.TryGetValue(charId, out var entry)) { sort++; continue; }
            var crewId = CrewIdFor(entry.CrewRole);
            if (existingIds.Contains(proto))
            {
                var row = existing.First(a => a.Id == proto);
                if (string.IsNullOrEmpty(row.SoulPath)) row.SoulPath = SoulFor(charId);
                if (row.Name == "Probe") row.Name = entry.Name;
                if (!string.Equals(row.Role, entry.CrewRole, StringComparison.OrdinalIgnoreCase))
                {
                    row.Role = entry.CrewRole;
                }
                if (string.IsNullOrEmpty(row.CrewId)) row.CrewId = crewId;
            }
            else
            {
                db.Agents.Add(new AgentRow
                {
                    Id = proto,
                    Sort = sort++,
                    Name = entry.Name,
                    Role = entry.CrewRole,
                    CrewId = crewId,
                    CapabilitiesJson = "[]",
                    State = "idle",
                    SoulPath = SoulFor(charId),
                });
            }
        }

        // -- 2) Seed every other character that doesn't already exist. They
        // use their character id directly (NZ, AB, YS, …) so the backend
        // surfaces the full Ne Zha crew of 91 to the UI.
        var aliasedCharIds = new HashSet<string>(prototypeAlias.Values, StringComparer.OrdinalIgnoreCase);
        sort = 100; // leave ids 0-99 to the prototype slots
        foreach (var entry in allocation)
        {
            if (aliasedCharIds.Contains(entry.Id)) continue; // already covered by alias
            if (existingIds.Contains(entry.Id))
            {
                var row = existing.First(a => a.Id == entry.Id);
                if (string.IsNullOrEmpty(row.SoulPath)) row.SoulPath = SoulFor(entry.Id);
                if (string.IsNullOrEmpty(row.CrewId)) row.CrewId = CrewIdFor(entry.CrewRole);
                continue;
            }
            db.Agents.Add(new AgentRow
            {
                Id = entry.Id,
                Sort = sort++,
                Name = entry.Name,
                Role = entry.CrewRole,
                CrewId = CrewIdFor(entry.CrewRole),
                CapabilitiesJson = "[]",
                State = "idle",
                SoulPath = SoulFor(entry.Id),
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Crew-role → Phase 11 crew-row id. Pure function so we can use it for
    /// both fresh seeds and backfill.
    /// </summary>
    private static string? CrewIdFor(string crewRole) => crewRole switch
    {
        "arch" => "crew-arch",
        "eng"  => "crew-eng",
        "rev"  => "crew-rev",
        "qa"   => "crew-qa",
        _      => null,
    };

    /// <summary>Loaded once at seed time from agents/allocation.json.</summary>
    private sealed record AllocationEntry(string Id, string Name, string RoleLabel, string Team, string RoleBucket, string CrewRole, string? Avatar);

    private static List<AllocationEntry> LoadAllocation(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "agents", "allocation.json");
        if (!File.Exists(path)) return new List<AllocationEntry>();
        var json = File.ReadAllText(path);
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };
        // The JSON keys are snake_case (id, name, role_label, team,
        // role_bucket, crew_role, avatar) — map manually because the record
        // uses PascalCase parameters.
        using var doc = JsonDocument.Parse(json);
        var list = new List<AllocationEntry>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            list.Add(new AllocationEntry(
                Id: el.GetProperty("id").GetString() ?? string.Empty,
                Name: el.GetProperty("name").GetString() ?? string.Empty,
                RoleLabel: el.GetProperty("role_label").GetString() ?? string.Empty,
                Team: el.GetProperty("team").GetString() ?? string.Empty,
                RoleBucket: el.GetProperty("role_bucket").GetString() ?? string.Empty,
                CrewRole: el.GetProperty("crew_role").GetString() ?? "eng",
                Avatar: el.TryGetProperty("avatar", out var a) ? a.GetString() : null));
        }
        return list;
    }

    /// <summary>
    /// Walks up from <c>AppContext.BaseDirectory</c> until it finds the repo
    /// root (presence of an <c>agents/</c> sibling). Used to resolve
    /// <see cref="AgentRow.SoulPath"/> for the seeded crew. Falls back to the
    /// current directory if nothing matches.
    /// </summary>
    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "agents"))
                && File.Exists(Path.Combine(dir.FullName, "agents", "README.md")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static async Task EnsureSeedCrewsAsync(ForgeDbContext db)
    {
        // Canonical model + quotas for all crews. Pricing/quota fields are
        // owned by EnsureStartupConfigAsync (which re-asserts them on every
        // boot); we set them here only for the insert path so a freshly-
        // created row carries sane defaults before EnsureStartupConfigAsync
        // runs.
        const string RuntimeModel      = "claude-opus-4.6-1m";
        const decimal InputPrice       = 15m;
        const decimal OutputPrice      = 75m;
        const string EvalModel         = "gpt-5.5";
        const int    ConcurrentCap     = 20;
        const int    WeekCap           = 200;
        const decimal DailyBudget      = 5000m;
        const decimal PerTaskBudget    = 2500m;

        // Canonical-8 spec. Fields below the comment line are STRUCTURAL
        // (the crew's identity in the org chart) and are reconciled to these
        // values on every boot — overriding any stale write from past test
        // pollution. Lead, Backend, InvokeMode, capabilities, timezone, and
        // schedule-enabled are operator-mutable and intentionally NOT touched.
        var spec = new (string Id, int Sort, string Name, string Summary, string Shift, string? ParentCrewId, string PairRole, string? PairedWithCrewId, string DefaultLead)[]
        {
            ("crew-eng",        0, "Engineering",          "Cuts PRs, ships features, keeps tests green.",          "day",   null,         "generator",  "crew-eng-night",  "E4"),
            ("crew-rev",        1, "Review",               "Reads every diff, catches what engineers missed.",      "day",   null,         "standalone", null,              "R3"),
            ("crew-arch",       2, "Architecture",         "Frames problems, owns ADRs, keeps blast radii small.",  "day",   null,         "generator",  "crew-arch-night", "A1"),
            ("crew-qa",         3, "QA",                   "Fuzzes, soaks, watches the dials.",                     "day",   null,         "standalone", null,              "Q2"),
            ("crew-eng-night",  4, "Engineering (Night)",  "Engineering — 21:00–09:00 night band.",                 "night", "crew-eng",   "evaluator",  "crew-eng",        ""),
            ("crew-rev-night",  5, "Review (Night)",       "Review — 21:00–09:00 night band.",                      "night", "crew-rev",   "standalone", null,              ""),
            ("crew-arch-night", 6, "Architecture (Night)", "Architecture — 21:00–09:00 night band.",                "night", "crew-arch",  "evaluator",  "crew-arch",       ""),
            ("crew-qa-night",   7, "QA (Night)",           "QA — 21:00–09:00 night band.",                          "night", "crew-qa",    "standalone", null,              ""),
        };

        var specIds = spec.Select(s => s.Id).ToList();
        var existing = await db.Crews
            .Where(c => specIds.Contains(c.Id))
            .ToListAsync();
        var byId = existing.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

        var changed = false;
        foreach (var s in spec)
        {
            if (byId.TryGetValue(s.Id, out var row))
            {
                // Reconcile structural fields — overwrite even if a test
                // (or any other writer) flipped them. Lead is preserved.
                if (row.Sort             != s.Sort)             { row.Sort = s.Sort; changed = true; }
                if (row.Name             != s.Name)             { row.Name = s.Name; changed = true; }
                if (row.Summary          != s.Summary)          { row.Summary = s.Summary; changed = true; }
                if (row.Shift            != s.Shift)            { row.Shift = s.Shift; changed = true; }
                if (row.ParentCrewId     != s.ParentCrewId)     { row.ParentCrewId = s.ParentCrewId; changed = true; }
                if (row.PairRole         != s.PairRole)         { row.PairRole = s.PairRole; changed = true; }
                if (row.PairedWithCrewId != s.PairedWithCrewId) { row.PairedWithCrewId = s.PairedWithCrewId; changed = true; }
            }
            else
            {
                db.Crews.Add(new CrewRow
                {
                    Id = s.Id,
                    Sort = s.Sort,
                    Name = s.Name,
                    Summary = s.Summary,
                    Lead = string.IsNullOrEmpty(s.DefaultLead) ? null : s.DefaultLead,
                    Model = RuntimeModel,
                    ModelInputPricePerMTok = InputPrice,
                    ModelOutputPricePerMTok = OutputPrice,
                    EvaluatorModel = EvalModel,
                    MaxConcurrent = ConcurrentCap,
                    WeeklyCap = WeekCap,
                    DailyBudgetUsd = DailyBudget,
                    MaxTaskBudgetUsd = PerTaskBudget,
                    Shift = s.Shift,
                    ParentCrewId = s.ParentCrewId,
                    PairRole = s.PairRole,
                    PairedWithCrewId = s.PairedWithCrewId,
                });
                changed = true;
            }
        }
        if (changed) await db.SaveChangesAsync();
    }

    /// <summary>
    /// Phase 18 — for each (day, night) crew pair, split agents evenly
    /// between the two shifts. The day-crew lead is preserved; the
    /// remaining agents are sorted by id ascending and the second half
    /// moves to the night crew. For teams with an odd non-lead count,
    /// the night crew gets the extra agent (day already has the lead).
    ///
    /// Idempotent: on fresh DBs the night crew starts empty and the
    /// initial split runs. On existing DBs with an uneven legacy split,
    /// the method rebalances until the difference is ≤ 1.
    /// </summary>
    private static async Task EnsureNightShiftSplitAsync(ForgeDbContext db)
    {
        var pairs = new (string Day, string Night)[]
        {
            ("crew-eng",  "crew-eng-night"),
            ("crew-rev",  "crew-rev-night"),
            ("crew-arch", "crew-arch-night"),
            ("crew-qa",   "crew-qa-night"),
        };

        var changed = false;

        foreach (var (dayId, nightId) in pairs)
        {
            var nightCrew = await db.Crews.FirstOrDefaultAsync(c => c.Id == nightId);
            if (nightCrew is null) continue;

            var dayCrew = await db.Crews.FirstOrDefaultAsync(c => c.Id == dayId);
            var dayLeadId = dayCrew?.Lead;

            // Gather ALL agents for this team (both shifts).
            var teamAgents = await db.Agents
                .Where(a => a.CrewId == dayId || a.CrewId == nightId)
                .OrderBy(a => a.Id)
                .ToListAsync();
            if (teamAgents.Count == 0) continue;

            // Check if already balanced (difference ≤ 1).
            var dayCount = teamAgents.Count(a => a.CrewId == dayId);
            var nightCount = teamAgents.Count(a => a.CrewId == nightId);
            if (nightCount > 0 && Math.Abs(dayCount - nightCount) <= 1) continue;

            // Build balanced split: lead stays in day, split rest evenly.
            var nonLead = teamAgents.Where(a => a.Id != dayLeadId).ToList();
            var half = nonLead.Count / 2;

            // Day: lead + first half of non-lead agents.
            foreach (var a in nonLead.Take(half))
            {
                a.CrewId = dayId;
            }

            // Night: remaining half.
            string? newNightLead = null;
            foreach (var a in nonLead.Skip(half))
            {
                a.CrewId = nightId;
                newNightLead ??= a.Id;
            }

            // Ensure lead stays in day crew.
            var lead = teamAgents.FirstOrDefault(a => a.Id == dayLeadId);
            if (lead is not null) lead.CrewId = dayId;

            if (string.IsNullOrEmpty(nightCrew.Lead) && newNightLead is not null)
            {
                nightCrew.Lead = newNightLead;
            }

            changed = true;
        }

        if (changed) await db.SaveChangesAsync();
    }

    /// <summary>
    /// Phase 19 — ensure every agent assigned to a crew also has a
    /// <see cref="CrewMemberRow"/> entry in that crew's roster. Leads
    /// are excluded: they are tracked via <see cref="CrewRow.Lead"/>
    /// and including them here would double-count in effective-roster
    /// calculations.
    ///
    /// Also reconciles lead/CrewId mismatches: if an agent is the lead
    /// of crew X but has <c>AgentRow.CrewId</c> pointing elsewhere,
    /// the CrewId is corrected to match the crew they lead.
    ///
    /// Idempotent: existing roster entries are preserved; only missing
    /// rows are added.
    /// </summary>
    private static async Task EnsureRosterBackfillAsync(ForgeDbContext db)
    {
        var crews = await db.Crews.ToListAsync();
        var leadByCrewId = crews
            .Where(c => !string.IsNullOrEmpty(c.Lead))
            .ToDictionary(c => c.Id, c => c.Lead!, StringComparer.OrdinalIgnoreCase);

        // Reconcile lead/CrewId mismatches: ensure each crew's lead
        // agent has CrewId pointing at the crew they lead.
        var allAgents = await db.Agents.ToListAsync();
        var agentsById = allAgents.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var (crewId, leadId) in leadByCrewId)
        {
            if (agentsById.TryGetValue(leadId, out var leadAgent)
                && !string.Equals(leadAgent.CrewId, crewId, StringComparison.OrdinalIgnoreCase))
            {
                leadAgent.CrewId = crewId;
            }
        }

        // Build set of (crewId → leadId) so we skip leads in roster entries.
        var existingMembers = await db.CrewMembers.ToListAsync();
        var existingPairs = new HashSet<(string, string)>(
            existingMembers.Select(m => (m.CrewId.ToUpperInvariant(), m.AgentId.ToUpperInvariant())));

        // Track insertion index per crew for alternating roles.
        var crewIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in existingMembers)
        {
            crewIndex.TryGetValue(m.CrewId, out var cnt);
            crewIndex[m.CrewId] = cnt + 1;
        }

        var added = false;
        var assignedAgents = allAgents
            .Where(a => !string.IsNullOrEmpty(a.CrewId))
            .OrderBy(a => a.Sort)
            .ThenBy(a => a.Id)
            .ToList();

        foreach (var agent in assignedAgents)
        {
            var crewId = agent.CrewId!;

            // Skip leads — they're tracked via CrewRow.Lead.
            if (leadByCrewId.TryGetValue(crewId, out var crewLead)
                && string.Equals(crewLead, agent.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Skip if already in roster.
            if (existingPairs.Contains((crewId.ToUpperInvariant(), agent.Id.ToUpperInvariant())))
            {
                continue;
            }

            crewIndex.TryGetValue(crewId, out var idx);
            var role = idx % 2 == 0 ? "generator" : "evaluator";
            crewIndex[crewId] = idx + 1;

            db.CrewMembers.Add(new CrewMemberRow
            {
                CrewId = crewId,
                AgentId = agent.Id,
                Role = role,
                AddedBy = "seed-backfill",
            });
            added = true;
        }

        if (added) await db.SaveChangesAsync();
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

    /// <summary>
    /// Phase 12.2 — every existing <see cref="CardRow"/> gets mirrored as a
    /// <see cref="WorkItemRow"/> the first time we boot after the migration.
    /// Idempotent on work item id; we use the card id as-is so cross-links
    /// from the existing cards endpoint stay coherent. Subsequent edits flow
    /// through /api/work-items; a follow-up batch rewires /api/cards as a
    /// projection over <c>work_items</c>.
    /// </summary>
    private static async Task BackfillWorkItemsFromCardsAsync(ForgeDbContext db)
    {
        var existingIds = await db.WorkItems.Select(w => w.Id).ToListAsync();
        var existing = new HashSet<string>(existingIds);
        var cards = await db.Cards.AsNoTracking().ToListAsync();
        var added = 0;
        foreach (var c in cards)
        {
            if (existing.Contains(c.Id)) continue;

            // Card.Type is one of: story | task | bug. Map onto the dev-track
            // work-item types per Q12.1 (revised). Cards never carry epics or
            // features today, but if a card's title says "Epic:" we promote it.
            var type = c.Type switch
            {
                "story"  => "feature",
                "task"   => "task",
                "bug"    => "bug",
                _        => "task",
            };

            // Card.Col → State. backlog/design → proposed; build → active;
            // review → resolved; done → closed.
            var state = c.Col switch
            {
                "backlog" => "proposed",
                "design"  => "proposed",
                "build"   => "active",
                "review"  => "resolved",
                "done"    => "closed",
                _         => "active",
            };

            db.WorkItems.Add(new WorkItemRow
            {
                Id = c.Id,
                RepoId = c.RepoId,
                Provider = "local",
                ProviderRef = null,
                Track = "dev",
                Type = type,
                State = state,
                Title = c.Title,
                DescriptionMd = string.Empty,
                ParentId = c.Epic, // best-effort: epic id is the closest parent today
                FeatureRef = null,
                IterationRef = string.Empty,
                AreaPath = string.Empty,
                AssignedToJson = TranslateAgentToAssignee(c.AgentJson),
                Points = c.Pts,
                Priority = c.Priority,
                TagsJson = c.TagsJson,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                RemoteRevision = c.RemoteRevision,
            });
            added++;
        }
        if (added > 0) await db.SaveChangesAsync();

        // One-shot fix-up for rows that were backfilled with the legacy
        // Card.Agent shape (`{ id, role, name }`). The WorkItemAssignee
        // contract uses `{ kind, id, displayName, initials }`, so the legacy
        // rows surface a null displayName in the UI and crash sort/select.
        await RepairLegacyAssigneeShapeAsync(db);
    }

    /// <summary>
    /// Translate Card.Agent JSON (`{ id, role, name }`) into the Phase 12
    /// WorkItemAssignee shape (`{ kind, id, displayName, initials }`).
    /// Returns null when the card has no agent, when the JSON is malformed,
    /// or when neither name nor id is present.
    /// </summary>
    private static string? TranslateAgentToAssignee(string? agentJson)
    {
        if (string.IsNullOrWhiteSpace(agentJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(agentJson);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            var role = root.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : null;

            // Already the new shape? Pass through unchanged.
            var alreadyNew = root.TryGetProperty("displayName", out _);
            if (alreadyNew) return agentJson;

            if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(name)) return null;

            var displayName = name ?? id ?? string.Empty;
            var kind = string.Equals(role, "human", StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "product-owner", StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "tech-lead", StringComparison.OrdinalIgnoreCase)
                ? "human"
                : "agent";
            var initials = MakeInitials(displayName, fallback: id);
            var assignee = new
            {
                kind,
                id = id ?? initials,
                displayName,
                initials,
            };
            return System.Text.Json.JsonSerializer.Serialize(assignee, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Idempotent repair: walk every <c>work_items</c> row whose
    /// AssignedToJson is in the legacy `{ id, role, name }` shape and
    /// rewrite it as the Phase 12 WorkItemAssignee shape. Skips rows that
    /// already carry <c>displayName</c>.
    /// </summary>
    private static async Task RepairLegacyAssigneeShapeAsync(ForgeDbContext db)
    {
        var rows = await db.WorkItems
            .Where(w => w.AssignedToJson != null && w.AssignedToJson != "")
            .ToListAsync();
        var changed = 0;
        foreach (var row in rows)
        {
            // Cheap shape check before parsing — skip rows already migrated.
            if (row.AssignedToJson!.Contains("\"displayName\"", StringComparison.Ordinal)) continue;
            var fixedJson = TranslateAgentToAssignee(row.AssignedToJson);
            if (fixedJson is not null && fixedJson != row.AssignedToJson)
            {
                row.AssignedToJson = fixedJson;
                changed++;
            }
            else if (fixedJson is null)
            {
                row.AssignedToJson = null;
                changed++;
            }
        }
        if (changed > 0) await db.SaveChangesAsync();
    }

    private static string MakeInitials(string display, string? fallback)
    {
        var s = (display ?? string.Empty).Trim();
        if (s.Length == 0) return (fallback ?? "?").ToUpperInvariant();
        var parts = s.Split(new[] { ' ', '\t', '_', '-', '.', '@' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            return parts[0].Length >= 2
                ? (parts[0][0].ToString() + parts[0][1]).ToUpperInvariant()
                : parts[0][0].ToString().ToUpperInvariant();
        }
        return (parts[0][0].ToString() + parts[1][0]).ToUpperInvariant();
    }

    /// <summary>
    /// Startup reconciliation pass. Runs every boot after seed and
    /// <c>ApplyAutoAssignAsync</c>. Two responsibilities:
    /// <list type="number">
    ///   <item><description>Enforce canonical model, evaluator model, and
    ///         quota settings on the 8 seeded crews.</description></item>
    ///   <item><description>Guarantee each team's day/night shift has at
    ///         least 2 effective members (lead + ≥1 roster entry). If a
    ///         shift is short, fill from same-team agents that are not yet
    ///         on any crew's effective roster.</description></item>
    /// </list>
    /// Idempotent: writing the same values is a no-op at the SQLite layer;
    /// roster additions only happen when the guarantee is unmet.
    /// </summary>
    public static async Task EnsureStartupConfigAsync(ForgeDbContext db, ILogger? logger = null)
    {
        // ── 1. Canonical crew ids we manage. ────────────────────────────
        var canonicalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "crew-eng", "crew-rev", "crew-arch", "crew-qa",
            "crew-eng-night", "crew-rev-night", "crew-arch-night", "crew-qa-night",
        };

        var crews = await db.Crews.Where(c => canonicalIds.Contains(c.Id)).ToListAsync();
        if (crews.Count == 0) return;

        // ── 2. Enforce model + quotas. ──────────────────────────────────
        const string runtimeModel  = "claude-opus-4.6-1m";
        const decimal inputPrice   = 15m;
        const decimal outputPrice  = 75m;
        const string evalModel     = "gpt-5.5";
        const int maxConcurrent    = 20;
        const int weeklyCap        = 200;
        const decimal dailyBudget  = 5000m;
        const decimal perTaskBudget = 2500m;

        foreach (var c in crews)
        {
            c.Model                   = runtimeModel;
            c.ModelInputPricePerMTok  = inputPrice;
            c.ModelOutputPricePerMTok = outputPrice;
            c.EvaluatorModel          = evalModel;
            c.MaxConcurrent           = maxConcurrent;
            c.WeeklyCap               = weeklyCap;
            c.DailyBudgetUsd          = dailyBudget;
            c.MaxTaskBudgetUsd        = perTaskBudget;
        }

        // ── 3. Roster balance — each shift needs ≥ 2 effective members. ─
        // Effective roster = lead (if set) + CrewMemberRows.
        var allLeads = crews
            .Where(c => !string.IsNullOrEmpty(c.Lead))
            .Select(c => c.Lead!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allMemberships = await db.CrewMembers.ToListAsync();
        var rosterAgentIds = allMemberships.Select(m => m.AgentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Globally claimed = anyone who is a lead OR a roster member of any crew.
        var globallyClaimedIds = new HashSet<string>(allLeads, StringComparer.OrdinalIgnoreCase);
        globallyClaimedIds.UnionWith(rosterAgentIds);

        // Team mapping: crew id → team name.
        string? TeamOf(string crewId) => crewId switch
        {
            "crew-eng" or "crew-eng-night"   => "Engineering",
            "crew-rev" or "crew-rev-night"   => "Review",
            "crew-arch" or "crew-arch-night" => "Architecture",
            "crew-qa" or "crew-qa-night"     => "QA",
            _ => null,
        };

        // Agent role → team name (matches CrewsEndpoints.TeamFromAgentRole).
        string? TeamOfRole(string? role) => role?.ToLowerInvariant() switch
        {
            "arch" or "architect" => "Architecture",
            "eng" or "engineer"   => "Engineering",
            "rev" or "reviewer"   => "Review",
            "qa" or "qe" or "test" => "QA",
            _ => null,
        };

        var dayNightPairs = new (string DayId, string NightId)[]
        {
            ("crew-eng",  "crew-eng-night"),
            ("crew-rev",  "crew-rev-night"),
            ("crew-arch", "crew-arch-night"),
            ("crew-qa",   "crew-qa-night"),
        };

        var allAgents = await db.Agents.OrderBy(a => a.Sort).ThenBy(a => a.Id).ToListAsync();

        foreach (var (dayId, nightId) in dayNightPairs)
        {
            var dayCrew = crews.FirstOrDefault(c => c.Id == dayId);
            var nightCrew = crews.FirstOrDefault(c => c.Id == nightId);
            if (dayCrew is null || nightCrew is null) continue;

            var team = TeamOf(dayId);
            if (team is null) continue;

            int EffectiveCount(CrewRow crew)
            {
                var count = allMemberships.Count(m => m.CrewId == crew.Id);
                if (!string.IsNullOrEmpty(crew.Lead)) count++;
                return count;
            }

            var dayCount = EffectiveCount(dayCrew);
            var nightCount = EffectiveCount(nightCrew);

            // Both shifts already have ≥ 2 members — skip.
            if (dayCount >= 2 && nightCount >= 2) continue;

            // Find same-team agents not globally claimed.
            var candidates = allAgents
                .Where(a => TeamOfRole(a.Role) == team && !globallyClaimedIds.Contains(a.Id))
                .ToList();

            if (candidates.Count == 0)
            {
                logger?.LogWarning(
                    "Startup roster check: {Team} has shifts with <2 members (day={Day}, night={Night}) but no unassigned agents available.",
                    team, dayCount, nightCount);
                continue;
            }

            // Fill the underpopulated shift(s). Alternate between day and
            // night when both are short, preferring the smaller shift first.
            while (candidates.Count > 0 && (dayCount < 2 || nightCount < 2))
            {
                var target = dayCount <= nightCount ? dayCrew : nightCrew;
                ref int targetCount = ref dayCount <= nightCount ? ref dayCount : ref nightCount;

                var agent = candidates[0];
                candidates.RemoveAt(0);

                // Set lead if the crew has none.
                if (string.IsNullOrEmpty(target.Lead))
                {
                    target.Lead = agent.Id;
                    globallyClaimedIds.Add(agent.Id);
                    targetCount++;
                    logger?.LogInformation("Startup roster: set {Agent} as lead of {Crew}.", agent.Id, target.Id);
                    continue;
                }

                // Add as roster member.
                var idx = allMemberships.Count(m => m.CrewId == target.Id);
                var role = idx % 2 == 0 ? "generator" : "evaluator";
                db.CrewMembers.Add(new CrewMemberRow
                {
                    CrewId = target.Id,
                    AgentId = agent.Id,
                    Role = role,
                    AddedBy = "startup-config",
                });
                allMemberships.Add(new CrewMemberRow { CrewId = target.Id, AgentId = agent.Id, Role = role });
                agent.CrewId = target.Id;
                globallyClaimedIds.Add(agent.Id);
                targetCount++;

                logger?.LogInformation("Startup roster: added {Agent} to {Crew} as {Role}.", agent.Id, target.Id, role);
            }
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// One-time scrub of test fixture rows that leaked into the prod DB
    /// before <c>ForgeWebApplicationFactory</c>'s in-memory config tripwire
    /// landed (the factory used to race on a global env var, so any test
    /// class calling <c>db.Crews.Add(new CrewRow {...})</c> wrote into
    /// <c>E:\forge\forge.db</c>).
    ///
    /// <para>
    /// Canonical sets:
    /// <list type="bullet">
    ///   <item><description>crews — the 8 ids in <see cref="EnsureSeedCrewsAsync"/>.</description></item>
    ///   <item><description>agents — every id in <c>agents/allocation.json</c> plus the
    ///         prototype aliases (A1/A2/E4/E7/E9/R3/R5/Q2) plus XD.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Scrub deletes from: crews, crew_members, crew_history, agents,
    /// agent_history, agent_usage, agent_level_history, deliverables.
    /// Nullable refs in task_queue (RoutedToCrewId, PreferredAgentId) and
    /// agent_assignments (CrewId) are nulled rather than deleted so live
    /// queue rows aren't lost — orphaned routes will fall back to default
    /// dispatch.
    /// </para>
    /// <para>
    /// Idempotent. <paramref name="dryRun"/> = true (default) only counts
    /// rows that would be touched; pass false to actually delete.
    /// </para>
    /// </summary>
    public static async Task<ScrubReport> ScrubTestArtifactsAsync(ForgeDbContext db, bool dryRun, ILogger? logger = null)
    {
        var canonicalCrewIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "crew-eng", "crew-rev", "crew-arch", "crew-qa",
            "crew-eng-night", "crew-rev-night", "crew-arch-night", "crew-qa-night",
        };

        var allocation = LoadAllocation(FindRepoRoot());
        var canonicalAgentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "A1", "A2", "E4", "E7", "E9", "R3", "R5", "Q2", "XD",
        };
        foreach (var entry in allocation)
        {
            if (!string.IsNullOrEmpty(entry.Id)) canonicalAgentIds.Add(entry.Id);
        }

        var report = new ScrubReport();

        // ── crew_members: any row referencing a non-canonical crew or agent.
        var staleMembers = await db.CrewMembers
            .Where(m => !canonicalCrewIds.Contains(m.CrewId) || !canonicalAgentIds.Contains(m.AgentId))
            .ToListAsync();
        report.CrewMembersDeleted = staleMembers.Count;
        if (!dryRun) db.CrewMembers.RemoveRange(staleMembers);

        // ── crew_history: rows for non-canonical crews.
        var staleHistory = await db.CrewHistory
            .Where(h => !canonicalCrewIds.Contains(h.CrewId))
            .ToListAsync();
        report.CrewHistoryDeleted = staleHistory.Count;
        if (!dryRun) db.CrewHistory.RemoveRange(staleHistory);

        // ── agent_history: rows for non-canonical agents.
        var staleAgentHistory = await db.AgentHistory
            .Where(h => !canonicalAgentIds.Contains(h.AgentId))
            .ToListAsync();
        report.AgentHistoryDeleted = staleAgentHistory.Count;
        if (!dryRun) db.AgentHistory.RemoveRange(staleAgentHistory);

        // ── agent_usage: cost-ledger rows for non-canonical crew or agent.
        var staleUsage = await db.AgentUsage
            .Where(u => !canonicalCrewIds.Contains(u.CrewId) || !canonicalAgentIds.Contains(u.AgentId))
            .ToListAsync();
        report.AgentUsageDeleted = staleUsage.Count;
        if (!dryRun) db.AgentUsage.RemoveRange(staleUsage);

        // ── agent_level_history: rows for non-canonical agents.
        var staleLevels = await db.AgentLevelHistory
            .Where(h => !canonicalAgentIds.Contains(h.AgentId))
            .ToListAsync();
        report.AgentLevelHistoryDeleted = staleLevels.Count;
        if (!dryRun) db.AgentLevelHistory.RemoveRange(staleLevels);

        // ── deliverables: rows for non-canonical crew or any participant agent.
        var staleDeliverables = await db.Deliverables
            .Where(d =>
                !canonicalCrewIds.Contains(d.CrewId)
                || !canonicalAgentIds.Contains(d.GeneratorAgentId)
                || !canonicalAgentIds.Contains(d.EvaluatorAgentId)
                || (d.JudgeAgentId != null && !canonicalAgentIds.Contains(d.JudgeAgentId)))
            .ToListAsync();
        report.DeliverablesDeleted = staleDeliverables.Count;
        if (!dryRun) db.Deliverables.RemoveRange(staleDeliverables);

        // ── task_queue: live queue rows. Don't delete — null out refs so
        // orphaned routes fall back to default dispatch.
        var taskRows = await db.TaskQueue
            .Where(t =>
                (t.RoutedToCrewId != null && !canonicalCrewIds.Contains(t.RoutedToCrewId))
                || (t.PreferredAgentId != null && !canonicalAgentIds.Contains(t.PreferredAgentId)))
            .ToListAsync();
        foreach (var t in taskRows)
        {
            if (t.RoutedToCrewId != null && !canonicalCrewIds.Contains(t.RoutedToCrewId))
            {
                report.TaskQueueRoutedNulled++;
                if (!dryRun) t.RoutedToCrewId = null;
            }
            if (t.PreferredAgentId != null && !canonicalAgentIds.Contains(t.PreferredAgentId))
            {
                report.TaskQueuePreferredNulled++;
                if (!dryRun) t.PreferredAgentId = null;
            }
        }

        // ── agent_assignments: null CrewId for non-canonical crews.
        var staleAssignments = await db.AgentAssignments
            .Where(a => a.CrewId != null && !canonicalCrewIds.Contains(a.CrewId))
            .ToListAsync();
        report.AgentAssignmentCrewNulled = staleAssignments.Count;
        if (!dryRun) foreach (var a in staleAssignments) a.CrewId = null;

        // ── agents: delete non-canonical rows. Done AFTER agent_history /
        // agent_usage / agent_level_history removal so no FK-style orphans.
        var staleAgents = await db.Agents
            .Where(a => !canonicalAgentIds.Contains(a.Id))
            .ToListAsync();
        report.AgentsDeleted = staleAgents.Count;
        report.AgentIdsDeleted = staleAgents.Select(a => a.Id).OrderBy(s => s).ToList();
        if (!dryRun) db.Agents.RemoveRange(staleAgents);

        // ── crews: delete non-canonical rows last.
        var staleCrews = await db.Crews
            .Where(c => !canonicalCrewIds.Contains(c.Id))
            .ToListAsync();
        report.CrewsDeleted = staleCrews.Count;
        report.CrewIdsDeleted = staleCrews.Select(c => c.Id).OrderBy(s => s).ToList();
        if (!dryRun) db.Crews.RemoveRange(staleCrews);

        if (!dryRun) await db.SaveChangesAsync();

        logger?.LogInformation(
            "Scrub {Mode}: crews={Crews} agents={Agents} crew_members={Members} crew_history={History} agent_history={AgentHistory} agent_usage={Usage} agent_level_history={Levels} deliverables={Deliverables} task_queue.routed_nulled={Routed} task_queue.preferred_nulled={Preferred} agent_assignments.crew_nulled={Assignments}",
            dryRun ? "DRY-RUN" : "APPLIED",
            report.CrewsDeleted, report.AgentsDeleted, report.CrewMembersDeleted, report.CrewHistoryDeleted,
            report.AgentHistoryDeleted, report.AgentUsageDeleted, report.AgentLevelHistoryDeleted,
            report.DeliverablesDeleted, report.TaskQueueRoutedNulled, report.TaskQueuePreferredNulled,
            report.AgentAssignmentCrewNulled);

        return report;
    }
}

/// <summary>
/// Result of <see cref="ForgeSeed.ScrubTestArtifactsAsync"/>. All counts are
/// rows touched; in dry-run mode the DB is unchanged but the counts reflect
/// what an apply pass would do.
/// </summary>
public sealed class ScrubReport
{
    public int CrewsDeleted { get; set; }
    public int AgentsDeleted { get; set; }
    public int CrewMembersDeleted { get; set; }
    public int CrewHistoryDeleted { get; set; }
    public int AgentHistoryDeleted { get; set; }
    public int AgentUsageDeleted { get; set; }
    public int AgentLevelHistoryDeleted { get; set; }
    public int DeliverablesDeleted { get; set; }
    public int TaskQueueRoutedNulled { get; set; }
    public int TaskQueuePreferredNulled { get; set; }
    public int AgentAssignmentCrewNulled { get; set; }
    public List<string> CrewIdsDeleted { get; set; } = new();
    public List<string> AgentIdsDeleted { get; set; } = new();
}

public static class ForgeSeedSpawnScrub
{
    /// <summary>
    /// Drop test-fixture story spawns + workstreams. Keeps any spawn whose
    /// <c>Id</c> is in <paramref name="keepSpawnIds"/> and any workstream
    /// whose <c>Id</c> is in <paramref name="keepWorkstreamIds"/>. Orphan
    /// workstreams (no <c>StorySpawnId</c>) are dropped unless explicitly
    /// kept.
    ///
    /// <para>
    /// Cascades to <c>workstream_deps</c>. Does NOT touch <c>prs</c>,
    /// <c>cards</c>, or <c>work_items</c> — those carry independently
    /// synced data whose lifecycle is decoupled from the spawn loop.
    /// </para>
    /// </summary>
    public static async Task<SpawnScrubReport> ScrubMockedSpawnsAsync(
        ForgeDbContext db,
        IReadOnlyCollection<string> keepSpawnIds,
        IReadOnlyCollection<string> keepWorkstreamIds,
        bool dryRun,
        ILogger? logger = null)
    {
        var keepSpawns = new HashSet<string>(keepSpawnIds, StringComparer.OrdinalIgnoreCase);
        var keepWorkstreams = new HashSet<string>(keepWorkstreamIds, StringComparer.OrdinalIgnoreCase);

        var report = new SpawnScrubReport();

        // ── workstreams: anything not in the explicit keep list, or whose
        // spawn isn't kept either, is removed.
        var allWs = await db.Workstreams.ToListAsync();
        var staleWs = allWs.Where(w =>
        {
            if (keepWorkstreams.Contains(w.Id)) return false;
            if (!string.IsNullOrEmpty(w.StorySpawnId) && keepSpawns.Contains(w.StorySpawnId!)) return false;
            return true;
        }).ToList();
        var staleWsIds = staleWs.Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        report.WorkstreamsDeleted = staleWs.Count;
        report.WorkstreamIdsDeleted = staleWs.Select(w => w.Id).OrderBy(s => s).ToList();

        // ── workstream_deps: any row referencing a stale workstream on
        // either side. Composite PK so RemoveRange handles cascade safely.
        var allDeps = await db.WorkstreamDeps.ToListAsync();
        var staleDeps = allDeps.Where(d =>
            staleWsIds.Contains(d.WorkstreamId) || staleWsIds.Contains(d.DependsOnId)).ToList();
        report.WorkstreamDepsDeleted = staleDeps.Count;

        // ── story_spawns: anything not in the keep list.
        var allSpawns = await db.StorySpawns.ToListAsync();
        var staleSpawns = allSpawns.Where(s => !keepSpawns.Contains(s.Id)).ToList();
        report.StorySpawnsDeleted = staleSpawns.Count;
        report.StorySpawnIdsDeleted = staleSpawns.Select(s => s.Id).OrderBy(s => s).ToList();

        if (!dryRun)
        {
            // Delete deps first (composite FK), then workstreams, then spawns.
            db.WorkstreamDeps.RemoveRange(staleDeps);
            db.Workstreams.RemoveRange(staleWs);
            db.StorySpawns.RemoveRange(staleSpawns);
            await db.SaveChangesAsync();
        }

        logger?.LogInformation(
            "Spawn scrub {Mode}: workstreams={Ws} (orphans included) workstream_deps={Deps} story_spawns={Spawns}",
            dryRun ? "DRY-RUN" : "APPLIED",
            report.WorkstreamsDeleted, report.WorkstreamDepsDeleted, report.StorySpawnsDeleted);

        return report;
    }
}

public sealed class SpawnScrubReport
{
    public int WorkstreamsDeleted { get; set; }
    public int WorkstreamDepsDeleted { get; set; }
    public int StorySpawnsDeleted { get; set; }
    public List<string> WorkstreamIdsDeleted { get; set; } = new();
    public List<string> StorySpawnIdsDeleted { get; set; } = new();
}
