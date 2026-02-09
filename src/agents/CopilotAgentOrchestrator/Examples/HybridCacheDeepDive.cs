// -----------------------------------------------------------------------
// 
// -----------------------------------------------------------------------

// =============================================================================
// Example: HybridCache Deep-Dive using Multi-Agent Orchestration
//
// This demonstrates how to replicate Claude Code's subagent pattern using
// the CopilotAgentOrchestrator library with Copilot enterprise subscription.
//
// Instead of one agent exhausting its context on file exploration, we spawn
// specialist agents in parallel — each with a fresh context window — then
// synthesize their findings with a premium model.
// =============================================================================

namespace CopilotAgentOrchestrator.Examples;

using GitHub.Copilot.SDK;

/// <summary>
/// Example showing multi-agent orchestration for a HybridCache deep-dive analysis.
/// </summary>
public static class HybridCacheDeepDive
{
    /// <summary>
    /// Runs a full deep-dive with 6 specialist agents + synthesis.
    /// </summary>
    public static async Task RunAsync()
    {
        // ── 1. Create a single Copilot client (enterprise subscription) ──────────
        await using var client = new CopilotClient();

        // ── 2. Create an orchestrator with custom settings ───────────────────────
        await using var orchestrator = client.CreateOrchestrator(new OrchestratorConfig
        {
            MaxConcurrency = 4,                           // Run up to 4 agents simultaneously
            SynthesisModel = AgentDefaults.PremiumModel,  // claude-opus-4.6 for final synthesis
            DefaultTimeout = TimeSpan.FromMinutes(5)
        });

        // ── 3. Define specialist agents — each gets FRESH context ────────────────
        //
        // This is the key advantage: instead of one agent spending 70% of its context
        // on file exploration, each specialist focuses on one aspect with full budget.

        var cacheSourceFiles = new List<string>
        {
            @"src\Common\Common\Source\Caching\DefaultHybridCache.cs",
            @"src\Common\Common\Source\Caching\CacheSettings.cs",
            @"src\Common\Common\Source\Caching\CacheKeyMapping.cs",
        };

        var agents = new[]
        {
            // ── Cheap Haiku agents for parallel exploration ──
            AgentFactory.Explore(
                id: "di-registration",
                prompt: """
                    Analyze the complete dependency injection registration chain for HybridCache.
                    Trace from UnityConfig.RegisterCaching() through ServiceCollection to Unity.
                    Document every registered type, its lifetime, and the order of registration.
                    Include the 6 type-specific cache settings with their TTL values.
                    """,
                files: [@"src\UpdateResourceProvider\UpdateService\Service\Source\UnityConfig.cs"]),

            AgentFactory.Explore(
                id: "serialization",
                prompt: """
                    Map the entire serialization layer for the HybridCache system.
                    Document SerializerFactory's type-matching cascade, all IHybridCacheSerializer<T>
                    implementations, custom JsonConverters, and which types use which serializer.
                    Include the priority order and fallback logic.
                    """,
                files: [@"src\Common\Common\Source\Caching\SerializerFactory.cs"]),

            AgentFactory.Explore(
                id: "storage-tiers",
                prompt: """
                    Analyze the storage architecture: MemoryCache (L1) -> CsvFileCache (L2).
                    Document the CacheKeyMapping bidirectional sync mechanism, how lists are stored
                    as individual items with mapping, hash-based isolation, and the CSV file format.
                    """,
                files: cacheSourceFiles),

            AgentFactory.Explore(
                id: "concurrency",
                prompt: """
                    Identify ALL thread-safety mechanisms and potential race conditions in the
                    HybridCache implementation. Check: CacheKeyMapping's HashSet access, syncLock
                    usage, SemaphoreSlim for file access, CAS operations, and concurrent collections.
                    Flag any unprotected shared state.
                    """,
                files: cacheSourceFiles),

            // ── Standard model for deeper analysis ──
            AgentFactory.Custom(
                id: "background-refresh",
                prompt: """
                    Trace the complete background refresh flow. Document: staleness detection via
                    GetLastModifiedTime(), the refresh trigger conditions, valueFactory timeout
                    handling, cancel-then-background-refresh pattern, and stampede risks.
                    Include all configuration parameters with their defaults.
                    """,
                model: AgentDefaults.StandardModel,
                systemPrompt: """
                    You are an expert in async patterns and caching systems. Trace code paths
                    exhaustively, noting every branch condition and edge case.
                    """,
                files: cacheSourceFiles),

            // ── Code review agent for bug detection ──
            AgentFactory.CodeReview(
                id: "bugs-and-risks",
                prompt: """
                    Review the HybridCache implementation for bugs, race conditions, resource leaks,
                    and security issues. Focus on: CacheKeyMapping thread safety, serialization
                    error handling, file cache corruption scenarios, and memory growth patterns.
                    """,
                files: cacheSourceFiles),
        };

        Console.WriteLine($"Spawning {agents.Length} specialist agents in parallel...");
        Console.WriteLine($"Models: {string.Join(", ", agents.Select(a => a.Model ?? AgentDefaults.GetDefaultModel(a.AgentType)).Distinct())}");
        Console.WriteLine();

        // ── 4. Execute all agents in parallel and synthesize with Opus ───────────
        var result = await orchestrator.RunAsync(
            agents,
            AggregationStrategy.Synthesize,
            synthesisPrompt: """
                You are writing an exhaustive deep-dive document about a HybridCache implementation.
                Multiple specialist agents have analyzed different aspects. Combine their findings into
                a single, publication-quality technical document with:

                1. Table of Contents
                2. Registration & Wiring (from di-registration agent)
                3. Serialization & Type Handling (from serialization agent)
                4. Storage Architecture (from storage-tiers agent)
                5. Concurrency & Thread Safety (from concurrency agent)
                6. Background Refresh Flow (from background-refresh agent)
                7. Bugs & Risks (from bugs-and-risks agent)
                8. Knowledge Graph (synthesize relationships across all sections)

                Preserve ALL technical details. Add cross-references between sections.
                The result should read as if written by one expert who knows every line of code.

                {results}
                """);

        // ── 5. Report results ────────────────────────────────────────────────────
        Console.WriteLine($"Completed in {result.TotalDuration.TotalSeconds:F1}s");
        Console.WriteLine($"Success: {result.SuccessCount}/{result.AgentResults.Count}");
        Console.WriteLine();

        foreach (var agentResult in result.AgentResults)
        {
            var status = agentResult.Success ? "✓" : "✗";
            Console.WriteLine($"  [{status}] {agentResult.AgentId} ({agentResult.ModelUsed}, {agentResult.Duration.TotalSeconds:F1}s)");
        }

        Console.WriteLine();
        Console.WriteLine("═══ SYNTHESIZED DOCUMENT ═══");
        Console.WriteLine(result.AggregatedContent);
    }

    /// <summary>
    /// Quick exploration + synthesis using the shorthand extension method.
    /// </summary>
    public static async Task RunQuickSynthesisAsync()
    {
        await using var client = new CopilotClient();

        var quickResult = await client.ExploreAndSynthesizeAsync(new Dictionary<string, string>
        {
            ["architecture"] = "Describe the overall HybridCache architecture in 3 paragraphs",
            ["testing"] = "List all test classes and their coverage areas for HybridCache",
            ["performance"] = "Identify N+1 queries, unbounded growth, and hot paths in the cache",
        });

        Console.WriteLine(quickResult.AggregatedContent);
    }

    /// <summary>
    /// DAG-based scheduling: agents with dependencies run after their predecessors.
    /// Predecessor results are injected via {result:agent-id} placeholders.
    /// </summary>
    public static async Task RunWithDependenciesAsync()
    {
        await using var client = new CopilotClient();
        await using var orchestrator = client.CreateOrchestrator(new OrchestratorConfig
        {
            MaxConcurrency = 4,
            SynthesisModel = AgentDefaults.PremiumModel
        });

        // ── DAG structure ─────────────────────────────────────────────────
        //
        //   Wave 1 (parallel):  di-registration, serialization, storage, concurrency
        //   Wave 2 (depends on wave 1):  cross-cutting-analysis
        //   Wave 3 (depends on wave 2):  executive-summary
        //
        //   di-registration ──┐
        //   serialization ────┤
        //   storage ──────────┼──► cross-cutting-analysis ──► executive-summary
        //   concurrency ──────┘

        var agents = new SubAgentConfig[]
        {
            // ── Wave 1: Independent exploration agents (run in parallel) ──
            new()
            {
                Id = "di-registration",
                AgentType = AgentType.Explore,
                Prompt = "Analyze the DI registration chain for HybridCache. Document all registered types.",
            },
            new()
            {
                Id = "serialization",
                AgentType = AgentType.Explore,
                Prompt = "Map the serialization layer: SerializerFactory, type matching, all serializer implementations.",
            },
            new()
            {
                Id = "storage",
                AgentType = AgentType.Explore,
                Prompt = "Analyze MemoryCache (L1) -> CsvFileCache (L2) storage tiers and CacheKeyMapping.",
            },
            new()
            {
                Id = "concurrency",
                AgentType = AgentType.Explore,
                Prompt = "Identify all thread-safety mechanisms and race conditions in HybridCache.",
            },

            // ── Wave 2: Cross-cutting analysis (depends on all Wave 1 agents) ──
            new()
            {
                Id = "cross-cutting",
                AgentType = AgentType.GeneralPurpose,
                DependsOn = ["di-registration", "serialization", "storage", "concurrency"],
                Prompt = """
                    Based on the following specialist analyses, identify cross-cutting concerns,
                    hidden dependencies between subsystems, and architectural risks that span
                    multiple areas.

                    ## DI Registration Analysis
                    {result:di-registration}

                    ## Serialization Analysis
                    {result:serialization}

                    ## Storage Analysis
                    {result:storage}

                    ## Concurrency Analysis
                    {result:concurrency}
                    """,
            },

            // ── Wave 3: Executive summary (depends on cross-cutting) ──
            new()
            {
                Id = "executive-summary",
                AgentType = AgentType.GeneralPurpose,
                DependsOn = ["cross-cutting"],
                Prompt = """
                    Write a 1-page executive summary of the HybridCache system for a principal
                    engineer audience. Focus on: architecture quality, top 3 risks, and
                    recommended improvements.

                    Based on this cross-cutting analysis:
                    {result:cross-cutting}
                    """,
            },
        };

        Console.WriteLine("Running DAG-scheduled agents...");
        Console.WriteLine("  Wave 1: di-registration, serialization, storage, concurrency (parallel)");
        Console.WriteLine("  Wave 2: cross-cutting (waits for Wave 1)");
        Console.WriteLine("  Wave 3: executive-summary (waits for Wave 2)");
        Console.WriteLine();

        var result = await orchestrator.RunAsync(agents, AggregationStrategy.Structured);

        foreach (var agentResult in result.AgentResults)
        {
            var status = agentResult.Success ? "✓" : "✗";
            Console.WriteLine($"  [{status}] {agentResult.AgentId} ({agentResult.ModelUsed}, {agentResult.Duration.TotalSeconds:F1}s)");
        }

        // The executive summary is the final agent's output
        var summary = result.AgentResults.FirstOrDefault(r => r.AgentId == "executive-summary");
        Console.WriteLine();
        Console.WriteLine("═══ EXECUTIVE SUMMARY ═══");
        Console.WriteLine(summary?.Content);
    }
}
