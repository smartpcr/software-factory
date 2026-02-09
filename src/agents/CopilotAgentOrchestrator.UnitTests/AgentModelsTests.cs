// -----------------------------------------------------------------------
// 
// -----------------------------------------------------------------------

namespace CopilotAgentOrchestrator.UnitTests;

using CopilotAgentOrchestrator;

/// <summary>
/// Tests for <see cref="SubAgentConfig"/>, <see cref="OrchestratorConfig"/>,
/// and <see cref="OrchestratedResult"/> model defaults and computed properties.
/// </summary>
[TestClass]
public class AgentModelsTests
{
    // ── SubAgentConfig defaults ──────────────────────────────

    [TestMethod]
    public void SubAgentConfig_DefaultAgentType_IsGeneralPurpose()
    {
        var config = new SubAgentConfig { Id = "test", Prompt = "test" };
        config.AgentType.Should().Be(AgentType.GeneralPurpose);
    }

    [TestMethod]
    public void SubAgentConfig_DefaultTimeout_IsZero()
    {
        var config = new SubAgentConfig { Id = "test", Prompt = "test" };
        config.Timeout.Should().Be(TimeSpan.Zero);
    }

    [TestMethod]
    public void SubAgentConfig_DefaultMaxRetries_IsZero()
    {
        var config = new SubAgentConfig { Id = "test", Prompt = "test" };
        config.MaxRetries.Should().Be(0);
    }

    [TestMethod]
    public void SubAgentConfig_DefaultDependsOn_IsNull()
    {
        var config = new SubAgentConfig { Id = "test", Prompt = "test" };
        config.DependsOn.Should().BeNull();
    }

    [TestMethod]
    public void SubAgentConfig_AllOptionalProperties_DefaultToNull()
    {
        var config = new SubAgentConfig { Id = "test", Prompt = "test" };

        config.Description.Should().BeNull();
        config.Model.Should().BeNull();
        config.SystemPrompt.Should().BeNull();
        config.FileAttachments.Should().BeNull();
        config.WorkingDirectory.Should().BeNull();
        config.Tools.Should().BeNull();
    }

    // ── OrchestratorConfig defaults ──────────────────────────

    [TestMethod]
    public void OrchestratorConfig_DefaultMaxConcurrency_IsSeven()
    {
        var config = new OrchestratorConfig();
        config.MaxConcurrency.Should().Be(7);
    }

    [TestMethod]
    public void OrchestratorConfig_DefaultSynthesisModel_IsPremium()
    {
        var config = new OrchestratorConfig();
        config.SynthesisModel.Should().Be(AgentDefaults.PremiumModel);
    }

    [TestMethod]
    public void OrchestratorConfig_DefaultTimeout_IsThirtyMinutes()
    {
        var config = new OrchestratorConfig();
        config.DefaultTimeout.Should().Be(TimeSpan.FromMinutes(30));
    }

    [TestMethod]
    public void OrchestratorConfig_DefaultContinueOnFailure_IsTrue()
    {
        var config = new OrchestratorConfig();
        config.ContinueOnFailure.Should().BeTrue();
    }

    // ── OrchestratedResult computed properties ───────────────

    [TestMethod]
    public void OrchestratedResult_SuccessCount_CountsSuccessfulAgents()
    {
        var result = new OrchestratedResult
        {
            AgentResults = new List<AgentResult>
            {
                new() { AgentId = "a", Success = true },
                new() { AgentId = "b", Success = false },
                new() { AgentId = "c", Success = true },
                new() { AgentId = "d", Success = true },
            },
            TotalDuration = TimeSpan.FromSeconds(10)
        };

        result.SuccessCount.Should().Be(3);
    }

    [TestMethod]
    public void OrchestratedResult_FailureCount_CountsFailedAgents()
    {
        var result = new OrchestratedResult
        {
            AgentResults = new List<AgentResult>
            {
                new() { AgentId = "a", Success = true },
                new() { AgentId = "b", Success = false },
                new() { AgentId = "c", Success = false },
            },
            TotalDuration = TimeSpan.FromSeconds(5)
        };

        result.FailureCount.Should().Be(2);
    }

    [TestMethod]
    public void OrchestratedResult_AllSuccess_FailureCountIsZero()
    {
        var result = new OrchestratedResult
        {
            AgentResults = new List<AgentResult>
            {
                new() { AgentId = "a", Success = true },
                new() { AgentId = "b", Success = true },
            },
            TotalDuration = TimeSpan.FromSeconds(3)
        };

        result.SuccessCount.Should().Be(2);
        result.FailureCount.Should().Be(0);
    }

    [TestMethod]
    public void OrchestratedResult_EmptyResults_CountsAreZero()
    {
        var result = new OrchestratedResult
        {
            AgentResults = new List<AgentResult>(),
            TotalDuration = TimeSpan.Zero
        };

        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(0);
    }

    // ── AgentResult properties ───────────────────────────────

    [TestMethod]
    public void AgentResult_SuccessResult_HasContent()
    {
        var result = new AgentResult
        {
            AgentId = "test",
            Success = true,
            Content = "Analysis complete",
            Duration = TimeSpan.FromSeconds(2),
            ModelUsed = "claude-opus-4.6"
        };

        result.AgentId.Should().Be("test");
        result.Success.Should().BeTrue();
        result.Content.Should().Be("Analysis complete");
        result.Error.Should().BeNull();
        result.ModelUsed.Should().Be("claude-opus-4.6");
    }

    [TestMethod]
    public void AgentResult_FailureResult_HasError()
    {
        var result = new AgentResult
        {
            AgentId = "test",
            Success = false,
            Error = "Timeout exceeded",
            Duration = TimeSpan.FromMinutes(5)
        };

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Timeout exceeded");
        result.Content.Should().BeNull();
    }
}