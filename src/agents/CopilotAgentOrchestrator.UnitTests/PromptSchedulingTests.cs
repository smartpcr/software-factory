// -----------------------------------------------------------------------
// 
// -----------------------------------------------------------------------

namespace CopilotAgentOrchestrator.UnitTests;

using System.Collections.Concurrent;
using CopilotAgentOrchestrator;

/// <summary>
/// Tests that real prompts from <c>src/agents/Chat/Questions/</c> produce correct
/// subagent schedules via the DAG-based topological sort.
///
/// The prompts model a real-world code analysis pipeline:
///   01-repo-structure   — Independent exploration (no prerequisites)
///   02-feature-inventory — Independent exploration (can run with 01 in parallel)
///   03-feature-details  — Depends on 02 (needs feature list to deep-dive each one)
///   04-build-and-test   — Independent exploration (no prerequisites)
///   05-architecture     — Depends on 01 + 02 (needs structure and features for synthesis)
///
/// Natural DAG:
///   Wave 1: 01, 02, 04  (all independent explores)
///   Wave 2: 03, 05      (03 depends on 02; 05 depends on 01 + 02)
///
/// With sequential pipeline (01→02→03→05, 04 independent):
///   Wave 1: 01, 04
///   Wave 2: 02
///   Wave 3: 03
///   Wave 4: 05
/// </summary>
[TestClass]
public class PromptSchedulingTests
{
    private static readonly string QuestionsDir = Path.Combine(
        AppContext.BaseDirectory, "Questions");

    // ── Prompt file loading ──────────────────────────────────

    [TestMethod]
    public void AllPromptFiles_ExistAsLinkedTestData()
    {
        var expectedFiles = new[]
        {
            "01-repo-structure.md",
            "02-feature-inventory.md",
            "03-feature-details.md",
            "04-build-and-test.md",
            "05-architecture.md"
        };

        foreach (var file in expectedFiles)
        {
            var path = Path.Combine(QuestionsDir, file);
            File.Exists(path).Should().BeTrue(
                $"Linked prompt file '{file}' should exist at {path}");
        }
    }

    [TestMethod]
    [DataRow("01-repo-structure.md")]
    [DataRow("02-feature-inventory.md")]
    [DataRow("03-feature-details.md")]
    [DataRow("04-build-and-test.md")]
    [DataRow("05-architecture.md")]
    public void EachPromptFile_HasNonEmptyContent(string fileName)
    {
        var content = LoadPrompt(fileName);
        content.Should().NotBeNullOrWhiteSpace();
        content.Length.Should().BeGreaterThan(50,
            $"Prompt '{fileName}' should have meaningful content");
    }

    // ── All parallel (no dependencies) ───────────────────────

    [TestMethod]
    public void AllPromptsParallel_ProducesSingleWave()
    {
        var agents = CreateAgentsFromPrompts();

        var waves = AgentOrchestrator.TopologicalSort(agents);

        waves.Should().HaveCount(1, "all agents have no dependencies → single wave");
        waves[0].Should().HaveCount(5);
    }

    // ── Natural DAG (realistic pipeline) ─────────────────────

    [TestMethod]
    public void NaturalDag_ProducesTwoWaves()
    {
        // Wave 1: 01 (repo-structure), 02 (feature-inventory), 04 (build-and-test) — independent
        // Wave 2: 03 (feature-details, depends on 02), 05 (architecture, depends on 01+02)
        var agents = CreateAgentsWithNaturalDependencies();

        var waves = AgentOrchestrator.TopologicalSort(agents);

        waves.Should().HaveCount(2);

        // Wave 1: independent agents
        waves[0].Select(a => a.Id).Should().BeEquivalentTo(
            "repo-structure", "feature-inventory", "build-and-test");

        // Wave 2: dependent agents
        waves[1].Select(a => a.Id).Should().BeEquivalentTo(
            "feature-details", "architecture");
    }

    [TestMethod]
    public void NaturalDag_Wave1AgentsHaveNoDependencies()
    {
        var agents = CreateAgentsWithNaturalDependencies();
        var waves = AgentOrchestrator.TopologicalSort(agents);

        foreach (var agent in waves[0])
        {
            agent.DependsOn.Should().BeNullOrEmpty(
                $"Wave 1 agent '{agent.Id}' should have no dependencies");
        }
    }

    [TestMethod]
    public void NaturalDag_FeatureDetailsWaitsForInventory()
    {
        var agents = CreateAgentsWithNaturalDependencies();
        var waves = AgentOrchestrator.TopologicalSort(agents);

        var featureDetails = waves.SelectMany(w => w).First(a => a.Id == "feature-details");
        featureDetails.DependsOn.Should().Contain("feature-inventory");
    }

    [TestMethod]
    public void NaturalDag_ArchitectureWaitsForStructureAndInventory()
    {
        var agents = CreateAgentsWithNaturalDependencies();
        var waves = AgentOrchestrator.TopologicalSort(agents);

        var architecture = waves.SelectMany(w => w).First(a => a.Id == "architecture");
        architecture.DependsOn.Should().Contain("repo-structure")
            .And.Contain("feature-inventory");
    }

    // ── Sequential pipeline (strict order) ───────────────────

    [TestMethod]
    public void SequentialPipeline_ProducesFourWaves()
    {
        // 01 → 02 → 03 → 05, with 04 independent
        // Wave 1: 01 + 04
        // Wave 2: 02
        // Wave 3: 03
        // Wave 4: 05
        var agents = CreateAgentsWithSequentialPipeline();

        var waves = AgentOrchestrator.TopologicalSort(agents);

        waves.Should().HaveCount(4);
        waves[0].Select(a => a.Id).Should().BeEquivalentTo("repo-structure", "build-and-test");
        waves[1].Should().ContainSingle().Which.Id.Should().Be("feature-inventory");
        waves[2].Should().ContainSingle().Which.Id.Should().Be("feature-details");
        waves[3].Should().ContainSingle().Which.Id.Should().Be("architecture");
    }

    // ── Full pipeline with synthesis agent ────────────────────

    [TestMethod]
    public void FullPipelineWithSynthesis_ProducesThreeWaves()
    {
        // Wave 1: all 5 explores (using natural DAG: 01, 02, 04)
        // Wave 2: 03, 05 (depend on wave 1)
        // Wave 3: synthesis (depends on ALL agents)
        var agents = CreateAgentsWithNaturalDependencies();
        agents.Add(new SubAgentConfig
        {
            Id = "synthesis",
            AgentType = AgentType.GeneralPurpose,
            Prompt = """
                Synthesize all findings into a comprehensive document:
                Structure: {result:repo-structure}
                Features: {result:feature-inventory}
                Details: {result:feature-details}
                Build: {result:build-and-test}
                Architecture: {result:architecture}
                """,
            DependsOn = ["repo-structure", "feature-inventory", "feature-details",
                         "build-and-test", "architecture"]
        });

        var waves = AgentOrchestrator.TopologicalSort(agents);

        waves.Should().HaveCount(3);
        waves[0].Should().HaveCount(3, "independent explores");
        waves[1].Should().HaveCount(2, "dependent explores");
        waves[2].Should().ContainSingle().Which.Id.Should().Be("synthesis");
    }

    // ── Placeholder resolution with real prompts ─────────────

    [TestMethod]
    public void PlaceholderResolution_FeatureDetailsGetsInventoryResults()
    {
        var orchestrator = CreateOrchestrator();
        var completed = new ConcurrentDictionary<string, AgentResult>();
        completed["feature-inventory"] = new AgentResult
        {
            AgentId = "feature-inventory",
            Success = true,
            Content = "1. HybridCache\n2. ETW Pipeline\n3. ExecutionEngine"
        };

        var featureDetailsPrompt = LoadPrompt("03-feature-details.md");
        var agent = new SubAgentConfig
        {
            Id = "feature-details",
            AgentType = AgentType.GeneralPurpose,
            Prompt = $"Inventory results:\n{{result:feature-inventory}}\n\n{featureDetailsPrompt}",
            DependsOn = ["feature-inventory"]
        };

        var resolved = orchestrator.ResolvePromptPlaceholders(agent, completed);

        resolved.Prompt.Should().Contain("HybridCache");
        resolved.Prompt.Should().Contain("ETW Pipeline");
        resolved.Prompt.Should().Contain("ExecutionEngine");
        resolved.Prompt.Should().NotContain("{result:feature-inventory}");
        // Original prompt content should still be present
        resolved.Prompt.Should().Contain("exhaustive");
    }

    [TestMethod]
    public void PlaceholderResolution_ArchitectureGetsMultipleResults()
    {
        var orchestrator = CreateOrchestrator();
        var completed = new ConcurrentDictionary<string, AgentResult>();
        completed["repo-structure"] = new AgentResult
        {
            AgentId = "repo-structure",
            Success = true,
            Content = "src/ contains 12 projects organized by domain"
        };
        completed["feature-inventory"] = new AgentResult
        {
            AgentId = "feature-inventory",
            Success = true,
            Content = "Found 8 features: caching, ETW, workflow..."
        };

        var archPrompt = LoadPrompt("05-architecture.md");
        var agent = new SubAgentConfig
        {
            Id = "architecture",
            AgentType = AgentType.GeneralPurpose,
            Prompt = "## Repository Structure\n{result:repo-structure}\n\n## Feature Inventory\n{result:feature-inventory}\n\n" + archPrompt,
            DependsOn = ["repo-structure", "feature-inventory"]
        };

        var resolved = orchestrator.ResolvePromptPlaceholders(agent, completed);

        resolved.Prompt.Should().Contain("12 projects organized by domain");
        resolved.Prompt.Should().Contain("Found 8 features");
        resolved.Prompt.Should().NotContain("{result:repo-structure}");
        resolved.Prompt.Should().NotContain("{result:feature-inventory}");
        resolved.Prompt.Should().Contain("architecture");
    }

    [TestMethod]
    public void PlaceholderResolution_SynthesisGetsAllFiveResults()
    {
        var orchestrator = CreateOrchestrator();
        var completed = new ConcurrentDictionary<string, AgentResult>();
        completed["repo-structure"] = new AgentResult { AgentId = "repo-structure", Success = true, Content = "STRUCTURE_OUTPUT" };
        completed["feature-inventory"] = new AgentResult { AgentId = "feature-inventory", Success = true, Content = "INVENTORY_OUTPUT" };
        completed["feature-details"] = new AgentResult { AgentId = "feature-details", Success = true, Content = "DETAILS_OUTPUT" };
        completed["build-and-test"] = new AgentResult { AgentId = "build-and-test", Success = true, Content = "BUILD_OUTPUT" };
        completed["architecture"] = new AgentResult { AgentId = "architecture", Success = true, Content = "ARCHITECTURE_OUTPUT" };

        var agent = new SubAgentConfig
        {
            Id = "synthesis",
            AgentType = AgentType.GeneralPurpose,
            Prompt = """
                Combine all results:
                {result:repo-structure}
                {result:feature-inventory}
                {result:feature-details}
                {result:build-and-test}
                {result:architecture}
                """,
            DependsOn = ["repo-structure", "feature-inventory", "feature-details",
                         "build-and-test", "architecture"]
        };

        var resolved = orchestrator.ResolvePromptPlaceholders(agent, completed);

        resolved.Prompt.Should().Contain("STRUCTURE_OUTPUT");
        resolved.Prompt.Should().Contain("INVENTORY_OUTPUT");
        resolved.Prompt.Should().Contain("DETAILS_OUTPUT");
        resolved.Prompt.Should().Contain("BUILD_OUTPUT");
        resolved.Prompt.Should().Contain("ARCHITECTURE_OUTPUT");
        resolved.Prompt.Should().NotContainAny(
            "{result:repo-structure}", "{result:feature-inventory}",
            "{result:feature-details}", "{result:build-and-test}",
            "{result:architecture}");
    }

    // ── Agent type selection for prompts ──────────────────────

    [TestMethod]
    public void PromptAgentTypes_ExploresUsesFastModel_SynthesisUsesPremium()
    {
        var agents = CreateAgentsWithNaturalDependencies();

        // Explore agents default to fast model
        foreach (var agent in agents.Where(a => a.AgentType == AgentType.Explore))
        {
            var model = agent.Model ?? AgentDefaults.GetDefaultModel(agent.AgentType);
            model.Should().Be(AgentDefaults.FastModel,
                $"explore agent '{agent.Id}' should use fast model");
        }

        // GeneralPurpose agents (feature-details, architecture) use premium
        foreach (var agent in agents.Where(a => a.AgentType == AgentType.GeneralPurpose))
        {
            var model = agent.Model ?? AgentDefaults.GetDefaultModel(agent.AgentType);
            model.Should().Be(AgentDefaults.PremiumModel,
                $"general-purpose agent '{agent.Id}' should use premium model");
        }
    }

    // ── Edge case: adding cycle to prompts ───────────────────

    [TestMethod]
    public void PromptPipeline_WithCircularDependency_ThrowsCycleError()
    {
        var agents = new List<SubAgentConfig>
        {
            new()
            {
                Id = "repo-structure",
                Prompt = LoadPrompt("01-repo-structure.md"),
                DependsOn = ["architecture"]
            },
            new()
            {
                Id = "architecture",
                Prompt = LoadPrompt("05-architecture.md"),
                DependsOn = ["repo-structure"]
            },
        };

        var act = () => AgentOrchestrator.TopologicalSort(agents);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Circular dependency*");
    }

    // ── Feature-details prompt contains {feature} placeholder ─

    [TestMethod]
    public void FeatureDetailsPrompt_ContainsFeaturePlaceholder()
    {
        var prompt = LoadPrompt("03-feature-details.md");
        prompt.Should().Contain("{feature}",
            "03-feature-details should have a {feature} placeholder for per-feature deep-dive");
    }

    [TestMethod]
    public void FeatureDetailsPrompt_CanBeParameterizedWithFeatureName()
    {
        var promptTemplate = LoadPrompt("03-feature-details.md");
        var prompt = promptTemplate.Replace("{feature}", "HybridCache");

        prompt.Should().Contain("HybridCache");
        prompt.Should().NotContain("{feature}");
    }

    // ── Per-feature fan-out: inventory → N feature-detail agents ─

    [TestMethod]
    public void PerFeatureFanOut_CreatesCorrectDag()
    {
        // Simulate: inventory discovers 3 features → spawn 3 detail agents → synthesize
        var features = new[] { "HybridCache", "ETW-Pipeline", "ExecutionEngine" };
        var promptTemplate = LoadPrompt("03-feature-details.md");

        var agents = new List<SubAgentConfig>
        {
            AgentFactory.Explore("inventory", LoadPrompt("02-feature-inventory.md")),
        };

        // Fan out: one detail agent per feature
        foreach (var feature in features)
        {
            agents.Add(new SubAgentConfig
            {
                Id = $"detail-{feature}",
                AgentType = AgentType.GeneralPurpose,
                Prompt = $"Feature inventory:\n{{result:inventory}}\n\n{promptTemplate.Replace("{feature}", feature)}",
                DependsOn = ["inventory"]
            });
        }

        // Fan in: synthesize all details
        agents.Add(new SubAgentConfig
        {
            Id = "synthesize-details",
            AgentType = AgentType.GeneralPurpose,
            Prompt = string.Join("\n", features.Select(f => $"{{result:detail-{f}}}")),
            DependsOn = features.Select(f => $"detail-{f}").ToList()
        });

        var waves = AgentOrchestrator.TopologicalSort(agents);

        waves.Should().HaveCount(3);
        waves[0].Should().ContainSingle().Which.Id.Should().Be("inventory");
        waves[1].Should().HaveCount(3, "3 feature-detail agents in parallel");
        waves[1].Select(a => a.Id).Should().BeEquivalentTo(
            "detail-HybridCache", "detail-ETW-Pipeline", "detail-ExecutionEngine");
        waves[2].Should().ContainSingle().Which.Id.Should().Be("synthesize-details");
    }

    // ── Helpers ──────────────────────────────────────────────

    private static string LoadPrompt(string fileName)
    {
        var path = Path.Combine(QuestionsDir, fileName);
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Creates agents from all 5 prompts with NO dependencies (all parallel).
    /// </summary>
    private static List<SubAgentConfig> CreateAgentsFromPrompts()
    {
        return
        [
            AgentFactory.Explore("repo-structure", LoadPrompt("01-repo-structure.md")),
            AgentFactory.Explore("feature-inventory", LoadPrompt("02-feature-inventory.md")),
            AgentFactory.GeneralPurpose("feature-details", LoadPrompt("03-feature-details.md")),
            AgentFactory.Explore("build-and-test", LoadPrompt("04-build-and-test.md")),
            AgentFactory.GeneralPurpose("architecture", LoadPrompt("05-architecture.md")),
        ];
    }

    /// <summary>
    /// Creates agents with natural dependencies:
    ///   Wave 1: 01-repo-structure, 02-feature-inventory, 04-build-and-test
    ///   Wave 2: 03-feature-details (→02), 05-architecture (→01,02)
    /// </summary>
    private static List<SubAgentConfig> CreateAgentsWithNaturalDependencies()
    {
        return
        [
            AgentFactory.Explore("repo-structure", LoadPrompt("01-repo-structure.md")),
            AgentFactory.Explore("feature-inventory", LoadPrompt("02-feature-inventory.md")),
            new SubAgentConfig
            {
                Id = "feature-details",
                AgentType = AgentType.GeneralPurpose,
                Prompt = $"Feature inventory:\n{{result:feature-inventory}}\n\n{LoadPrompt("03-feature-details.md")}",
                DependsOn = ["feature-inventory"]
            },
            AgentFactory.Explore("build-and-test", LoadPrompt("04-build-and-test.md")),
            new SubAgentConfig
            {
                Id = "architecture",
                AgentType = AgentType.GeneralPurpose,
                Prompt = $"Structure:\n{{result:repo-structure}}\nFeatures:\n{{result:feature-inventory}}\n\n{LoadPrompt("05-architecture.md")}",
                DependsOn = ["repo-structure", "feature-inventory"]
            },
        ];
    }

    /// <summary>
    /// Creates a strict sequential pipeline:
    ///   01 → 02 → 03 → 05, with 04 independent
    /// </summary>
    private static List<SubAgentConfig> CreateAgentsWithSequentialPipeline()
    {
        return
        [
            AgentFactory.Explore("repo-structure", LoadPrompt("01-repo-structure.md")),
            AgentFactory.Explore("feature-inventory", LoadPrompt("02-feature-inventory.md"),
                dependsOn: ["repo-structure"]),
            new SubAgentConfig
            {
                Id = "feature-details",
                AgentType = AgentType.GeneralPurpose,
                Prompt = LoadPrompt("03-feature-details.md"),
                DependsOn = ["feature-inventory"]
            },
            AgentFactory.Explore("build-and-test", LoadPrompt("04-build-and-test.md")),
            new SubAgentConfig
            {
                Id = "architecture",
                AgentType = AgentType.GeneralPurpose,
                Prompt = LoadPrompt("05-architecture.md"),
                DependsOn = ["feature-details"]
            },
        ];
    }

    private static AgentOrchestrator CreateOrchestrator()
    {
        return new AgentOrchestrator(new GitHub.Copilot.SDK.CopilotClient());
    }
}