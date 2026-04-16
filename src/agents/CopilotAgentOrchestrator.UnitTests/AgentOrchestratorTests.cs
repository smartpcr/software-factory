// -----------------------------------------------------------------------
// 
// -----------------------------------------------------------------------

namespace CopilotAgentOrchestrator.UnitTests;

using System.Collections.Concurrent;
using CopilotAgentOrchestrator;
using GitHub.Copilot.SDK;

/// <summary>
/// Tests for prompt placeholder resolution (<c>{result:agent-id}</c> patterns)
/// and orchestrator construction/validation.
/// </summary>
[TestClass]
public class AgentOrchestratorTests
{
    // ── ResolvePromptPlaceholders ────────────────────────────

    [TestMethod]
    public void ResolvePromptPlaceholders_ReplacesSuccessResult()
    {
        var orchestrator = CreateOrchestrator();
        var completed = new ConcurrentDictionary<string, AgentResult>();
        completed["explore"] = new AgentResult
        {
            AgentId = "explore",
            Success = true,
            Content = "Found 5 classes"
        };

        var agent = new SubAgentConfig
        {
            Id = "analyze",
            Prompt = "Analyze this: {result:explore}",
            DependsOn = ["explore"]
        };

        var resolved = orchestrator.ResolvePromptPlaceholders(agent, completed);

        resolved.Prompt.Should().Contain("Found 5 classes");
        resolved.Prompt.Should().NotContain("{result:explore}");
    }

    [TestMethod]
    public void ResolvePromptPlaceholders_ReplacesFailedResult()
    {
        var orchestrator = CreateOrchestrator();
        var completed = new ConcurrentDictionary<string, AgentResult>();
        completed["step1"] = new AgentResult
        {
            AgentId = "step1",
            Success = false,
            Error = "Timeout exceeded"
        };

        var agent = new SubAgentConfig
        {
            Id = "step2",
            Prompt = "Based on: {result:step1}",
            DependsOn = ["step1"]
        };

        var resolved = orchestrator.ResolvePromptPlaceholders(agent, completed);

        resolved.Prompt.Should().Contain("(FAILED: Timeout exceeded)");
    }

    [TestMethod]
    public void ResolvePromptPlaceholders_NullContent_ReplacesWithNoContent()
    {
        var orchestrator = CreateOrchestrator();
        var completed = new ConcurrentDictionary<string, AgentResult>();
        completed["empty"] = new AgentResult
        {
            AgentId = "empty",
            Success = true,
            Content = null
        };

        var agent = new SubAgentConfig
        {
            Id = "next",
            Prompt = "Use: {result:empty}",
            DependsOn = ["empty"]
        };

        var resolved = orchestrator.ResolvePromptPlaceholders(agent, completed);

        resolved.Prompt.Should().Contain("(no content)");
    }

    [TestMethod]
    public void ResolvePromptPlaceholders_MultiplePlaceholders()
    {
        var orchestrator = CreateOrchestrator();
        var completed = new ConcurrentDictionary<string, AgentResult>();
        completed["a"] = new AgentResult { AgentId = "a", Success = true, Content = "Result A" };
        completed["b"] = new AgentResult { AgentId = "b", Success = true, Content = "Result B" };

        var agent = new SubAgentConfig
        {
            Id = "c",
            Prompt = "Combine: {result:a} and {result:b}",
            DependsOn = ["a", "b"]
        };

        var resolved = orchestrator.ResolvePromptPlaceholders(agent, completed);

        resolved.Prompt.Should().Be("Combine: Result A and Result B");
    }

    [TestMethod]
    public void ResolvePromptPlaceholders_NoPlaceholders_ReturnsOriginalAgent()
    {
        var orchestrator = CreateOrchestrator();
        var completed = new ConcurrentDictionary<string, AgentResult>();
        completed["a"] = new AgentResult { AgentId = "a", Success = true, Content = "X" };

        var agent = new SubAgentConfig
        {
            Id = "b",
            Prompt = "Plain prompt with no placeholders",
            DependsOn = ["a"]
        };

        var resolved = orchestrator.ResolvePromptPlaceholders(agent, completed);

        resolved.Should().BeSameAs(agent);
    }

    [TestMethod]
    public void ResolvePromptPlaceholders_NullDependsOn_ReturnsOriginalAgent()
    {
        var orchestrator = CreateOrchestrator();
        var completed = new ConcurrentDictionary<string, AgentResult>();

        var agent = new SubAgentConfig
        {
            Id = "standalone",
            Prompt = "No dependencies {result:whatever}",
            DependsOn = null
        };

        var resolved = orchestrator.ResolvePromptPlaceholders(agent, completed);

        resolved.Should().BeSameAs(agent);
    }

    [TestMethod]
    public void ResolvePromptPlaceholders_PreservesAllConfigProperties()
    {
        var orchestrator = CreateOrchestrator();
        var completed = new ConcurrentDictionary<string, AgentResult>();
        completed["dep"] = new AgentResult { AgentId = "dep", Success = true, Content = "data" };

        var files = new List<string> { "file.cs" };
        var deps = new List<string> { "dep" };
        var agent = new SubAgentConfig
        {
            Id = "test",
            Description = "Test agent",
            AgentType = AgentType.CodeReview,
            Model = "gpt-5",
            SystemPrompt = "Custom system",
            Prompt = "Review: {result:dep}",
            FileAttachments = files,
            Timeout = TimeSpan.FromMinutes(10),
            MaxRetries = 3,
            WorkingDirectory = @"C:\work",
            DependsOn = deps
        };

        var resolved = orchestrator.ResolvePromptPlaceholders(agent, completed);

        resolved.Id.Should().Be("test");
        resolved.Description.Should().Be("Test agent");
        resolved.AgentType.Should().Be(AgentType.CodeReview);
        resolved.Model.Should().Be("gpt-5");
        resolved.SystemPrompt.Should().Be("Custom system");
        resolved.FileAttachments.Should().BeEquivalentTo(files);
        resolved.Timeout.Should().Be(TimeSpan.FromMinutes(10));
        resolved.MaxRetries.Should().Be(3);
        resolved.WorkingDirectory.Should().Be(@"C:\work");
        resolved.DependsOn.Should().BeEquivalentTo(deps);
        resolved.Prompt.Should().Contain("data");
    }

    // ── Constructor validation ───────────────────────────────

    [TestMethod]
    public void Constructor_NullClient_ThrowsArgumentNull()
    {
        var act = () => new AgentOrchestrator(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("client");
    }

    [TestMethod]
    public void Constructor_NullConfig_UsesDefaults()
    {
        var client = CreateMockClient();
        var orchestrator = new AgentOrchestrator(client, null);

        // No throw means defaults were applied successfully
        orchestrator.Should().NotBeNull();
    }

    // ── Background agent management ──────────────────────────

    [TestMethod]
    public void ListBackgroundAgents_InitiallyEmpty()
    {
        var orchestrator = CreateOrchestrator();
        var agents = orchestrator.ListBackgroundAgents();

        agents.Should().BeEmpty();
    }

    [TestMethod]
    public async Task WaitForAgentAsync_UnknownId_ThrowsKeyNotFound()
    {
        var orchestrator = CreateOrchestrator();

        var act = () => orchestrator.WaitForAgentAsync("nonexistent");

        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage("*nonexistent*");
    }

    // ── Wave parallelism (no semaphore throttle) ────────────

    [TestMethod]
    public async Task WaveExecution_MaxConcurrencyThrottlesWithinWave()
    {
        // Arrange: 10 agents in W1 (no deps), MaxConcurrency = 3.
        // Peak concurrency should be capped at MaxConcurrency, not wave size.
        // All 10 agents should still complete — they just run 3 at a time.
        const int waveSize = 10;
        const int maxConcurrency = 3;

        var peakConcurrency = 0;
        var currentConcurrency = 0;
        var concurrencyLock = new object();

        var orchestrator = CreateOrchestratorWithConfig(new OrchestratorConfig
        {
            MaxConcurrency = maxConcurrency,
            ContinueOnFailure = true
        });

        orchestrator.ExecuteOverride = async (config, ct) =>
        {
            int snapshot;
            lock (concurrencyLock)
            {
                currentConcurrency++;
                snapshot = currentConcurrency;
                if (snapshot > peakConcurrency)
                {
                    peakConcurrency = snapshot;
                }
            }

            // Simulate work — long enough for concurrency to be measured
            await System.Threading.Tasks.Task.Delay(50, ct);

            lock (concurrencyLock)
            {
                currentConcurrency--;
            }

            return new AgentResult
            {
                AgentId = config.Id,
                Success = true,
                Content = $"Result from {config.Id}"
            };
        };

        // W1: 10 parallel explore agents, W2: 1 agent depending on first
        var agents = Enumerable.Range(1, waveSize)
            .Select(i => new SubAgentConfig
            {
                Id = $"explore-{i}",
                AgentType = AgentType.Explore,
                Prompt = $"Explore task {i}"
            })
            .ToList<SubAgentConfig>();

        // Add a W2 agent so the DAG path is used (not the flat parallel path)
        agents.Add(new SubAgentConfig
        {
            Id = "analyze",
            AgentType = AgentType.GeneralPurpose,
            Prompt = "Analyze results from {result:explore-1}",
            DependsOn = ["explore-1"]
        });

        // Act
        var result = await orchestrator.RunAsync(agents, cancellationToken: CancellationToken.None);

        // Assert: peak concurrency should be capped at MaxConcurrency
        peakConcurrency.Should().BeLessThanOrEqualTo(maxConcurrency,
            $"peak concurrency should not exceed MaxConcurrency={maxConcurrency}");
        result.AgentResults.Should().HaveCount(waveSize + 1);
        result.SuccessCount.Should().Be(waveSize + 1);
    }

    [TestMethod]
    public async Task WaveExecution_LaterWavesWaitForEarlierWaves()
    {
        // Arrange: W1 has 3 agents, W2 has 1 agent depending on a W1 agent.
        // The W2 agent must not start until all W1 agents are done.
        var w1Completed = false;
        var w2StartedBeforeW1Done = false;
        var completionCount = 0;
        var completionLock = new object();
        const int w1Size = 3;

        var orchestrator = CreateOrchestratorWithConfig(new OrchestratorConfig
        {
            MaxConcurrency = 2,
            ContinueOnFailure = true
        });

        orchestrator.ExecuteOverride = async (config, ct) =>
        {
            if (config.Id.StartsWith("w1-"))
            {
                await System.Threading.Tasks.Task.Delay(100, ct);
                lock (completionLock)
                {
                    completionCount++;
                    if (completionCount == w1Size)
                    {
                        w1Completed = true;
                    }
                }
            }
            else if (config.Id == "w2-analyze")
            {
                if (!w1Completed)
                {
                    w2StartedBeforeW1Done = true;
                }
            }

            return new AgentResult
            {
                AgentId = config.Id,
                Success = true,
                Content = $"Result from {config.Id}"
            };
        };

        var agents = new List<SubAgentConfig>
        {
            new() { Id = "w1-a", Prompt = "task a" },
            new() { Id = "w1-b", Prompt = "task b" },
            new() { Id = "w1-c", Prompt = "task c" },
            new() { Id = "w2-analyze", Prompt = "analyze {result:w1-a}", DependsOn = ["w1-a"] }
        };

        // Act
        var result = await orchestrator.RunAsync(agents, cancellationToken: CancellationToken.None);

        // Assert
        w2StartedBeforeW1Done.Should().BeFalse("W2 agent must wait for all W1 agents to complete");
        result.SuccessCount.Should().Be(4);
    }

    [TestMethod]
    public async Task WaveExecution_PlaceholdersResolvedFromCompletedWave()
    {
        // Arrange: W1 agent produces content, W2 agent uses {result:...} placeholder
        var orchestrator = CreateOrchestratorWithConfig(new OrchestratorConfig
        {
            ContinueOnFailure = true
        });

        string? resolvedPrompt = null;

        orchestrator.ExecuteOverride = async (config, ct) =>
        {
            if (config.Id == "w2-agent")
            {
                resolvedPrompt = config.Prompt;
            }

            await System.Threading.Tasks.Task.Delay(10, ct);

            return new AgentResult
            {
                AgentId = config.Id,
                Success = true,
                Content = config.Id == "w1-agent" ? "DISCOVERY_OUTPUT" : "ANALYSIS_OUTPUT"
            };
        };

        var agents = new List<SubAgentConfig>
        {
            new() { Id = "w1-agent", Prompt = "Explore" },
            new() { Id = "w2-agent", Prompt = "Analyze: {result:w1-agent}", DependsOn = ["w1-agent"] }
        };

        // Act
        await orchestrator.RunAsync(agents, cancellationToken: CancellationToken.None);

        // Assert: W2 agent should receive the resolved prompt, not the placeholder
        resolvedPrompt.Should().NotBeNull();
        resolvedPrompt.Should().Contain("DISCOVERY_OUTPUT");
        resolvedPrompt.Should().NotContain("{result:w1-agent}");
    }

    // ── Helpers ──────────────────────────────────────────────

    private static AgentOrchestrator CreateOrchestrator()
    {
        var client = CreateMockClient();
        return new AgentOrchestrator(client);
    }

    private static AgentOrchestrator CreateOrchestratorWithConfig(OrchestratorConfig config)
    {
        var client = CreateMockClient();
        return new AgentOrchestrator(client, config);
    }

    private static CopilotClient CreateMockClient()
    {
        // CopilotClient requires no constructor args for testing
        // Session creation will fail but we're testing non-session methods
        return new CopilotClient();
    }
}