// -----------------------------------------------------------------------
// 
// -----------------------------------------------------------------------

namespace CopilotAgentOrchestrator.UnitTests;

using CopilotAgentOrchestrator;

/// <summary>
/// Tests for <see cref="AgentDefaults"/> — model defaults and system prompt mappings.
/// </summary>
[TestClass]
public class AgentDefaultsTests
{
    [TestMethod]
    [DataRow(AgentType.Explore, AgentDefaults.FastModel)]
    [DataRow(AgentType.Task, AgentDefaults.StandardModel)]
    [DataRow(AgentType.GeneralPurpose, AgentDefaults.PremiumModel)]
    [DataRow(AgentType.CodeReview, AgentDefaults.StandardModel)]
    public void GetDefaultModel_ReturnsExpectedModel(AgentType agentType, string expectedModel)
    {
        var model = AgentDefaults.GetDefaultModel(agentType);
        model.Should().Be(expectedModel);
    }

    [TestMethod]
    public void GetDefaultModel_UnknownType_ReturnStandardModel()
    {
        var model = AgentDefaults.GetDefaultModel((AgentType)999);
        model.Should().Be(AgentDefaults.StandardModel);
    }

    [TestMethod]
    [DataRow(AgentType.Explore)]
    [DataRow(AgentType.Task)]
    [DataRow(AgentType.GeneralPurpose)]
    [DataRow(AgentType.CodeReview)]
    public void GetDefaultSystemPrompt_ReturnsNonEmpty(AgentType agentType)
    {
        var prompt = AgentDefaults.GetDefaultSystemPrompt(agentType);
        prompt.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public void GetDefaultSystemPrompt_UnknownType_ReturnsGeneralPurposePrompt()
    {
        var prompt = AgentDefaults.GetDefaultSystemPrompt((AgentType)999);
        var expected = AgentDefaults.GetDefaultSystemPrompt(AgentType.GeneralPurpose);
        prompt.Should().Be(expected);
    }

    [TestMethod]
    public void DefaultSynthesisPrompt_ContainsResultsPlaceholder()
    {
        AgentDefaults.DefaultSynthesisPrompt.Should().Contain("{results}");
    }

    [TestMethod]
    public void ModelConstants_AreCorrect()
    {
        AgentDefaults.FastModel.Should().Be("claude-haiku-4.5");
        AgentDefaults.StandardModel.Should().Be("claude-sonnet-4");
        AgentDefaults.PremiumModel.Should().Be("claude-opus-4.6");
    }
}