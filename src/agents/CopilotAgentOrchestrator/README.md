# CopilotAgentOrchestrator

Subagent orchestration on top of the [GitHub Copilot SDK](https://www.nuget.org/packages/GitHub.Copilot.SDK).

Spawn several specialist agents, each in its own fresh `CopilotSession` context window, run
them in parallel (or as a dependency DAG), then aggregate their outputs. This avoids the
context-pollution problem where one session's exploration overhead starves the synthesis budget.

## Install

```bash
dotnet add package CopilotAgentOrchestrator
```

The package is published to GitHub Packages. Add the feed once:

```xml
<!-- nuget.config -->
<add key="GitHub" value="https://nuget.pkg.github.com/smartpcr/index.json" />
```

## Quick start

```csharp
using CopilotAgentOrchestrator;
using GitHub.Copilot.SDK;

await using var client = new CopilotClient();
await using var orchestrator = new AgentOrchestrator(client);

var result = await orchestrator.RunAsync(
    new[]
    {
        new SubAgentConfig { Id = "di",     AgentType = AgentType.Explore, Prompt = "Analyze DI registration..." },
        new SubAgentConfig { Id = "serial", AgentType = AgentType.Explore, Prompt = "Analyze serialization..." },
    },
    AggregationStrategy.Synthesize);

Console.WriteLine(result.AggregatedContent);
Console.WriteLine($"{result.SuccessCount} succeeded, {result.FailureCount} failed");
```

## Agent types and model defaults

Every model is reached through the Copilot enterprise subscription, so there is no separate
API key to manage. `AgentDefaults.GetDefaultModel` picks a model per agent type; override it
per agent with `SubAgentConfig.Model`.

| `AgentType`      | Default model      | Intended use                                   |
|------------------|--------------------|------------------------------------------------|
| `Explore`        | `claude-haiku-4.5` | Cheap, parallel codebase exploration           |
| `Task`           | `claude-sonnet-4`  | Verbose commands — tests, builds, lints        |
| `GeneralPurpose` | `claude-opus-4.6`  | Complex multi-step analysis and synthesis      |
| `CodeReview`     | `claude-sonnet-4`  | High signal-to-noise code review               |

## Aggregation strategies

| `AggregationStrategy` | Behavior                                                           |
|-----------------------|--------------------------------------------------------------------|
| `Concatenate`         | Joins results under section headers. No extra LLM call — cheapest. |
| `Synthesize`          | Runs a fresh synthesis agent over the distilled outputs.           |
| `Structured`          | Returns results keyed by agent id for programmatic handling.        |

## Scheduling

- `OrchestratorConfig.MaxConcurrency` (default `7`) caps parallel sessions.
- `SubAgentConfig.DependsOn` turns a flat batch into a DAG; the orchestrator topologically
  sorts it and runs each wave in parallel.
- `SubAgentConfig.MaxRetries` retries a failed agent.
- `OrchestratorConfig.ContinueOnFailure` (default `true`) keeps the batch going when one
  agent fails.

## Background agents

```csharp
var id = orchestrator.SpawnBackground(new SubAgentConfig { Id = "audit", Prompt = "..." });
// ... do other work ...
AgentResult audit = await orchestrator.WaitForAgentAsync(id);
```

`orchestrator.LiveProgress` exposes per-agent status, model and token counters, updated as
streaming events arrive.

## License

MIT
