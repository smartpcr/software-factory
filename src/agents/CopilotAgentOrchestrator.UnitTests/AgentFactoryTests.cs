// -----------------------------------------------------------------------
// 
// -----------------------------------------------------------------------

namespace CopilotAgentOrchestrator.UnitTests;

using CopilotAgentOrchestrator;

/// <summary>
/// Tests for <see cref="AgentFactory"/> — convenience factory methods for subagent configs.
/// </summary>
[TestClass]
public class AgentFactoryTests
{
    // ── Explore ──────────────────────────────────────────────

    [TestMethod]
    public void Explore_SetsCorrectDefaults()
    {
        var agent = AgentFactory.Explore("test-explore", "What is X?");

        agent.Id.Should().Be("test-explore");
        agent.Prompt.Should().Be("What is X?");
        agent.AgentType.Should().Be(AgentType.Explore);
        agent.Description.Should().Be("Explore: test-explore");
        agent.FileAttachments.Should().BeNull();
        agent.WorkingDirectory.Should().BeNull();
        agent.DependsOn.Should().BeNull();
        agent.Model.Should().BeNull();
        agent.SystemPrompt.Should().BeNull();
    }

    [TestMethod]
    public void Explore_WithAllParameters()
    {
        var files = new List<string> { "file1.cs", "file2.cs" };
        var deps = new List<string> { "dep-1", "dep-2" };

        var agent = AgentFactory.Explore(
            "e1", "prompt",
            files: files,
            workingDirectory: @"C:\code",
            dependsOn: deps);

        agent.FileAttachments.Should().BeEquivalentTo(files);
        agent.WorkingDirectory.Should().Be(@"C:\code");
        agent.DependsOn.Should().BeEquivalentTo(deps);
    }

    // ── Task ─────────────────────────────────────────────────

    [TestMethod]
    public void Task_SetsCorrectDefaults()
    {
        var agent = AgentFactory.Task("build-task", "dotnet build");

        agent.Id.Should().Be("build-task");
        agent.Prompt.Should().Be("dotnet build");
        agent.AgentType.Should().Be(AgentType.Task);
        agent.Description.Should().Be("Task: build-task");
        agent.WorkingDirectory.Should().BeNull();
        agent.DependsOn.Should().BeNull();
    }

    [TestMethod]
    public void Task_WithDependsOn()
    {
        var deps = new List<string> { "restore" };
        var agent = AgentFactory.Task("build", "dotnet build", dependsOn: deps);

        agent.DependsOn.Should().BeEquivalentTo(deps);
    }

    // ── GeneralPurpose ───────────────────────────────────────

    [TestMethod]
    public void GeneralPurpose_SetsCorrectDefaults()
    {
        var agent = AgentFactory.GeneralPurpose("analysis", "Analyze X");

        agent.Id.Should().Be("analysis");
        agent.AgentType.Should().Be(AgentType.GeneralPurpose);
        agent.Model.Should().BeNull();
        agent.Description.Should().Be("General: analysis");
    }

    [TestMethod]
    public void GeneralPurpose_WithModelOverride()
    {
        var agent = AgentFactory.GeneralPurpose(
            "analysis", "Analyze X",
            model: "gpt-5");

        agent.Model.Should().Be("gpt-5");
    }

    [TestMethod]
    public void GeneralPurpose_WithAllParameters()
    {
        var files = new List<string> { "src.cs" };
        var deps = new List<string> { "explore" };

        var agent = AgentFactory.GeneralPurpose(
            "deep", "Analyze deeply",
            files: files,
            model: "custom-model",
            workingDirectory: @"C:\repo",
            dependsOn: deps);

        agent.FileAttachments.Should().BeEquivalentTo(files);
        agent.Model.Should().Be("custom-model");
        agent.WorkingDirectory.Should().Be(@"C:\repo");
        agent.DependsOn.Should().BeEquivalentTo(deps);
    }

    // ── CodeReview ───────────────────────────────────────────

    [TestMethod]
    public void CodeReview_SetsCorrectDefaults()
    {
        var agent = AgentFactory.CodeReview("review-1", "Review caching layer");

        agent.Id.Should().Be("review-1");
        agent.AgentType.Should().Be(AgentType.CodeReview);
        agent.Description.Should().Be("Review: review-1");
    }

    [TestMethod]
    public void CodeReview_WithFilesAndDependencies()
    {
        var files = new List<string> { "cache.cs" };
        var deps = new List<string> { "explore-cache" };

        var agent = AgentFactory.CodeReview(
            "review", "Review thread safety",
            files: files,
            dependsOn: deps);

        agent.FileAttachments.Should().BeEquivalentTo(files);
        agent.DependsOn.Should().BeEquivalentTo(deps);
    }

    // ── Custom ───────────────────────────────────────────────

    [TestMethod]
    public void Custom_SetsAllParameters()
    {
        var files = new List<string> { "a.cs" };
        var timeout = TimeSpan.FromMinutes(10);
        var deps = new List<string> { "pre-step" };

        var agent = AgentFactory.Custom(
            "custom-1", "Do something",
            model: "gpt-5",
            systemPrompt: "You are a specialist.",
            files: files,
            timeout: timeout,
            workingDirectory: @"C:\work",
            dependsOn: deps);

        agent.Id.Should().Be("custom-1");
        agent.AgentType.Should().Be(AgentType.GeneralPurpose);
        agent.Model.Should().Be("gpt-5");
        agent.SystemPrompt.Should().Be("You are a specialist.");
        agent.FileAttachments.Should().BeEquivalentTo(files);
        agent.Timeout.Should().Be(timeout);
        agent.WorkingDirectory.Should().Be(@"C:\work");
        agent.DependsOn.Should().BeEquivalentTo(deps);
        agent.Description.Should().Be("Custom: custom-1");
    }

    [TestMethod]
    public void Custom_DefaultTimeout_IsZero()
    {
        var agent = AgentFactory.Custom(
            "c", "prompt", "model", "system");

        agent.Timeout.Should().Be(TimeSpan.Zero);
    }
}