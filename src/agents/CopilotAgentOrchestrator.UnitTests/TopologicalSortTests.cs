// -----------------------------------------------------------------------
// 
// -----------------------------------------------------------------------

namespace CopilotAgentOrchestrator.UnitTests;

using CopilotAgentOrchestrator;

/// <summary>
/// Tests for the DAG-based topological sort in <see cref="AgentOrchestrator.TopologicalSort"/>.
/// Covers wave grouping, dependency ordering, cycle detection, and edge cases.
/// </summary>
[TestClass]
public class TopologicalSortTests
{
    // ── No dependencies (all parallel) ───────────────────────

    [TestMethod]
    public void NoDependencies_AllAgentsInSingleWave()
    {
        var agents = new List<SubAgentConfig>
        {
            new() { Id = "a", Prompt = "p" },
            new() { Id = "b", Prompt = "p" },
            new() { Id = "c", Prompt = "p" },
        };

        var waves = AgentOrchestrator.TopologicalSort(agents);

        waves.Should().HaveCount(1);
        waves[0].Should().HaveCount(3);
        waves[0].Select(a => a.Id).Should().BeEquivalentTo("a", "b", "c");
    }

    [TestMethod]
    public void SingleAgent_NoDependencies_SingleWave()
    {
        var agents = new List<SubAgentConfig>
        {
            new() { Id = "solo", Prompt = "p" },
        };

        var waves = AgentOrchestrator.TopologicalSort(agents);

        waves.Should().HaveCount(1);
        waves[0].Should().HaveCount(1);
        waves[0][0].Id.Should().Be("solo");
    }

    // ── Linear chain ─────────────────────────────────────────

    [TestMethod]
    public void LinearChain_ABC_ProducesThreeWaves()
    {
        // A → B → C
        var agents = new List<SubAgentConfig>
        {
            new() { Id = "a", Prompt = "p" },
            new() { Id = "b", Prompt = "p", DependsOn = ["a"] },
            new() { Id = "c", Prompt = "p", DependsOn = ["b"] },
        };

        var waves = AgentOrchestrator.TopologicalSort(agents);

        waves.Should().HaveCount(3);
        waves[0].Should().ContainSingle().Which.Id.Should().Be("a");
        waves[1].Should().ContainSingle().Which.Id.Should().Be("b");
        waves[2].Should().ContainSingle().Which.Id.Should().Be("c");
    }

    // ── Diamond dependency ───────────────────────────────────

    [TestMethod]
    public void DiamondDependency_CorrectWaves()
    {
        //     A
        //    / \
        //   B   C
        //    \ /
        //     D
        var agents = new List<SubAgentConfig>
        {
            new() { Id = "a", Prompt = "p" },
            new() { Id = "b", Prompt = "p", DependsOn = ["a"] },
            new() { Id = "c", Prompt = "p", DependsOn = ["a"] },
            new() { Id = "d", Prompt = "p", DependsOn = ["b", "c"] },
        };

        var waves = AgentOrchestrator.TopologicalSort(agents);

        waves.Should().HaveCount(3);
        waves[0].Should().ContainSingle().Which.Id.Should().Be("a");
        waves[1].Select(a => a.Id).Should().BeEquivalentTo("b", "c");
        waves[2].Should().ContainSingle().Which.Id.Should().Be("d");
    }

    // ── Wide fan-out then fan-in ─────────────────────────────

    [TestMethod]
    public void FanOutFanIn_FourExplorersToSynthesizer()
    {
        //  e1  e2  e3  e4  (wave 1: parallel)
        //   \  |   |  /
        //    synth          (wave 2: depends on all)
        var agents = new List<SubAgentConfig>
        {
            new() { Id = "e1", Prompt = "p" },
            new() { Id = "e2", Prompt = "p" },
            new() { Id = "e3", Prompt = "p" },
            new() { Id = "e4", Prompt = "p" },
            new() { Id = "synth", Prompt = "p", DependsOn = ["e1", "e2", "e3", "e4"] },
        };

        var waves = AgentOrchestrator.TopologicalSort(agents);

        waves.Should().HaveCount(2);
        waves[0].Should().HaveCount(4);
        waves[0].Select(a => a.Id).Should().BeEquivalentTo("e1", "e2", "e3", "e4");
        waves[1].Should().ContainSingle().Which.Id.Should().Be("synth");
    }

    // ── Mixed: some independent, some dependent ──────────────

    [TestMethod]
    public void MixedDependencies_IndependentAndDependentAgents()
    {
        //  a  b       (wave 1: independent)
        //  |  
        //  c  d       (wave 2: c depends on a, d is independent → same wave as c)
        //     |
        //     e       (wave 3: depends on d)
        var agents = new List<SubAgentConfig>
        {
            new() { Id = "a", Prompt = "p" },
            new() { Id = "b", Prompt = "p" },
            new() { Id = "c", Prompt = "p", DependsOn = ["a"] },
            new() { Id = "d", Prompt = "p" },
            new() { Id = "e", Prompt = "p", DependsOn = ["d"] },
        };

        var waves = AgentOrchestrator.TopologicalSort(agents);

        // Wave 1: a, b, d (all have in-degree 0)
        // Wave 2: c (depends on a), e (depends on d)
        waves.Should().HaveCount(2);
        waves[0].Select(a => a.Id).Should().BeEquivalentTo("a", "b", "d");
        waves[1].Select(a => a.Id).Should().BeEquivalentTo("c", "e");
    }

    // ── Three-wave pipeline ──────────────────────────────────

    [TestMethod]
    public void ThreeWavePipeline_ExploreAnalyzeSynthesize()
    {
        //  e1  e2  e3  e4     (wave 1: parallel explores)
        //   \  |   |  /
        //    cross-cut         (wave 2: analysis)
        //       |
        //    summary           (wave 3: synthesis)
        var agents = new List<SubAgentConfig>
        {
            new() { Id = "e1", Prompt = "p" },
            new() { Id = "e2", Prompt = "p" },
            new() { Id = "e3", Prompt = "p" },
            new() { Id = "e4", Prompt = "p" },
            new() { Id = "cross-cut", Prompt = "p", DependsOn = ["e1", "e2", "e3", "e4"] },
            new() { Id = "summary", Prompt = "p", DependsOn = ["cross-cut"] },
        };

        var waves = AgentOrchestrator.TopologicalSort(agents);

        waves.Should().HaveCount(3);
        waves[0].Should().HaveCount(4);
        waves[1].Should().ContainSingle().Which.Id.Should().Be("cross-cut");
        waves[2].Should().ContainSingle().Which.Id.Should().Be("summary");
    }

    // ── Cycle detection ──────────────────────────────────────

    [TestMethod]
    public void CyclicDependency_TwoNodes_ThrowsInvalidOperation()
    {
        // A → B → A (cycle)
        var agents = new List<SubAgentConfig>
        {
            new() { Id = "a", Prompt = "p", DependsOn = ["b"] },
            new() { Id = "b", Prompt = "p", DependsOn = ["a"] },
        };

        var act = () => AgentOrchestrator.TopologicalSort(agents);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Circular dependency*")
            .WithMessage("*a*")
            .WithMessage("*b*");
    }

    [TestMethod]
    public void CyclicDependency_ThreeNodes_ThrowsInvalidOperation()
    {
        // A → B → C → A (3-node cycle)
        var agents = new List<SubAgentConfig>
        {
            new() { Id = "a", Prompt = "p", DependsOn = ["c"] },
            new() { Id = "b", Prompt = "p", DependsOn = ["a"] },
            new() { Id = "c", Prompt = "p", DependsOn = ["b"] },
        };

        var act = () => AgentOrchestrator.TopologicalSort(agents);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Circular dependency*");
    }

    [TestMethod]
    public void SelfDependency_ThrowsInvalidOperation()
    {
        // A depends on itself
        var agents = new List<SubAgentConfig>
        {
            new() { Id = "a", Prompt = "p", DependsOn = ["a"] },
        };

        var act = () => AgentOrchestrator.TopologicalSort(agents);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Circular dependency*");
    }

    // ── Partial cycle (some agents are fine, some cycle) ─────

    [TestMethod]
    public void PartialCycle_ThrowsWithCyclicNodes()
    {
        // x is independent, a→b→a is a cycle
        var agents = new List<SubAgentConfig>
        {
            new() { Id = "x", Prompt = "p" },
            new() { Id = "a", Prompt = "p", DependsOn = ["b"] },
            new() { Id = "b", Prompt = "p", DependsOn = ["a"] },
        };

        var act = () => AgentOrchestrator.TopologicalSort(agents);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Circular dependency*a*")
            .WithMessage("*b*");
    }

    // ── Null DependsOn is treated as no dependencies ─────────

    [TestMethod]
    public void NullDependsOn_TreatedAsNoDependencies()
    {
        var agents = new List<SubAgentConfig>
        {
            new() { Id = "a", Prompt = "p", DependsOn = null },
            new() { Id = "b", Prompt = "p", DependsOn = null },
        };

        var waves = AgentOrchestrator.TopologicalSort(agents);

        waves.Should().HaveCount(1);
        waves[0].Should().HaveCount(2);
    }

    // ── Empty DependsOn list treated as no dependencies ──────

    [TestMethod]
    public void EmptyDependsOnList_TreatedAsNoDependencies()
    {
        var agents = new List<SubAgentConfig>
        {
            new() { Id = "a", Prompt = "p", DependsOn = [] },
            new() { Id = "b", Prompt = "p", DependsOn = [] },
        };

        var waves = AgentOrchestrator.TopologicalSort(agents);

        waves.Should().HaveCount(1);
        waves[0].Should().HaveCount(2);
    }

    // ── Ordering preservation within a wave ──────────────────

    [TestMethod]
    public void WithinWave_AgentsAreAllPresent()
    {
        var agents = new List<SubAgentConfig>
        {
            new() { Id = "z", Prompt = "p" },
            new() { Id = "m", Prompt = "p" },
            new() { Id = "a", Prompt = "p" },
        };

        var waves = AgentOrchestrator.TopologicalSort(agents);

        waves.Should().HaveCount(1);
        waves[0].Select(a => a.Id).Should().BeEquivalentTo("z", "m", "a");
    }

    // ── Complex DAG ──────────────────────────────────────────

    [TestMethod]
    public void ComplexDag_MultiplePathsToSameNode()
    {
        //  a ──► c ──► e
        //  b ──► d ──► e
        //  a ──► d       (a feeds both c and d)
        var agents = new List<SubAgentConfig>
        {
            new() { Id = "a", Prompt = "p" },
            new() { Id = "b", Prompt = "p" },
            new() { Id = "c", Prompt = "p", DependsOn = ["a"] },
            new() { Id = "d", Prompt = "p", DependsOn = ["a", "b"] },
            new() { Id = "e", Prompt = "p", DependsOn = ["c", "d"] },
        };

        var waves = AgentOrchestrator.TopologicalSort(agents);

        waves.Should().HaveCount(3);
        waves[0].Select(a => a.Id).Should().BeEquivalentTo("a", "b");
        waves[1].Select(a => a.Id).Should().BeEquivalentTo("c", "d");
        waves[2].Should().ContainSingle().Which.Id.Should().Be("e");
    }
}