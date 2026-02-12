# Chat - GitHub Copilot SDK Console Application

A console-based chat application built on the [GitHub Copilot SDK](https://github.com/github/copilot-sdk) with rich terminal UI via Spectre.Console.

## Purposes

### 1. Multi-Model Chat via GitHub Copilot SDK

Select from any model available through the Copilot SDK at runtime. The application dynamically lists models with billing multiplier info and lets you switch models mid-session.

**Flow:** Prerequisites check &rarr; Model selection &rarr; Session creation &rarr; Interactive chat loop

**Supported commands:**
- `model` &mdash; switch to a different model
- `clear` &mdash; start a new session (same model)
- `demo <1-6>` &mdash; run a built-in demo prompt (Code Review, Algorithm Help, Bug Finding, Design Pattern, API Design, Performance)
- `question <N>` &mdash; run a repo question from the `Questions/` folder
- `exit` / `quit` &mdash; end session

### 2. Multi-Agent Orchestration (Always On)

Every prompt is automatically decomposed into multiple subagents using the `CopilotAgentOrchestrator` library, executed as a DAG with dependency-aware scheduling, and post-processed — replicating the pattern that makes Claude Code's `Task` tool so effective.

#### The Problem (Before)

A single Copilot session burns 70–80% of its context window on tool-call narration ("Let me read…", "Now I'll check…"), leaving only 20–30% for actual synthesis. This is why the same Opus model produces dramatically different quality depending on how it's invoked:

| Capability | Single-Session Chat | Multi-Agent Orchestration |
|---|---|---|
| Subagent spawning | ❌ No | ✅ Yes — LLM planner decomposes prompt |
| Parallel workers | ❌ No | ✅ Yes — DAG scheduler, up to 7 concurrent |
| Fresh context per worker | ❌ No | ✅ Yes — each subagent gets an isolated session |
| Post-processing | ❌ No | ✅ Yes — TOC, knowledge graph, context doc |
| Output quality | Shallow, fragmented | Deep, comprehensive |

#### The Solution (After)

The planner LLM decomposes every prompt into a DAG of subagents. Each subagent runs in a fresh Copilot session with its full context budget dedicated to its specific task.

**Example orchestration plan** (for `feature="hybridcache"` deep-dive):

```
 Wave  │ Agent ID                │ Type           │ Model            │ Depends On           │ Description
───────┼─────────────────────────┼────────────────┼──────────────────┼──────────────────────┼──────────────────────────────────────────────────────────────
 W1 ║║ │ explore-di-registration │ Explore        │ claude-haiku-4.5 │ -                    │ Trace all DI registration: factories, maps, config binding
 W1 ║║ │ explore-serializers     │ Explore        │ claude-haiku-4.5 │ -                    │ Find custom serializers, converters, type handling
 W1 ║║ │ explore-storage-caching │ Explore        │ claude-haiku-4.5 │ -                    │ Identify storage and caching layers, keying, eviction
 W1 ║║ │ explore-entry-points    │ Explore        │ claude-haiku-4.5 │ -                    │ Find all entry points: controllers, jobs, event handlers
 W1 ║║ │ explore-resilience      │ Explore        │ claude-haiku-4.5 │ -                    │ Find retry policies, circuit breakers, timeout handling
 W1 ║║ │ explore-concurrency     │ Explore        │ claude-haiku-4.5 │ -                    │ Analyze thread safety, locks, async coordination
 W1 ║║ │ explore-messaging       │ Explore        │ claude-haiku-4.5 │ -                    │ Find event publishers, subscribers, message contracts
 W1 ║║ │ explore-observability   │ Explore        │ claude-haiku-4.5 │ -                    │ Find logging, metrics, tracing, health checks
 W1 ║║ │ explore-tests           │ Explore        │ claude-haiku-4.5 │ -                    │ Find unit tests, integration tests, and test gaps
 W1 ║║ │ explore-cross-cutting   │ Explore        │ claude-haiku-4.5 │ -                    │ Find validation, authorization, cross-cutting concerns
       │                         │                │                  │                      │
  W2   │ explore-call-chains     │ GeneralPurpose │ claude-opus-4.6  │ explore-entry-points │ Trace complete call chains from entry points
       │                         │                │                  │                      │
  W3   │ explore-performance     │ GeneralPurpose │ claude-opus-4.6  │ explore-call-chains  │ Analyze hot paths, memory, I/O, bottlenecks
```

**Wave 1** (parallel, up to 7 concurrent): Ten fast Haiku agents each explore one aspect of the feature — DI registration, serialization, storage, entry points, resilience, concurrency, messaging, observability, testing, and cross-cutting concerns — all running in parallel with no dependencies.

**Wave 2** (sequential): An Opus agent traces complete call chains from the entry points discovered in W1, using `{result:explore-entry-points}` to inject W1 output.

**Wave 3** (sequential): An Opus agent analyzes performance characteristics based on the call chains from W2.

**Post-processing** (automatic): All agent outputs are concatenated with a table of contents, then a distillation agent produces a knowledge graph (Mermaid) and a concise context document (`context.md`) for downstream agents.

> **Note:** The plan is generated dynamically by the LLM planner at runtime, so the exact agents, waves, and dependencies may vary between runs depending on the prompt and model.

#### Plan File Output

Every run writes artifacts to `PromptOutput/{model}/{questionSlug}/`:
- **`orchestration-plan.md`** — the original prompt, per-agent wave assignment, type, model, dependencies, full prompt text, and after execution: per-agent start/end timestamps, duration, token counts (input/output/cache), success/failure status, and a summary table
- **`context.md`** — a distilled context document with architecture overview, key components, critical invariants, and a Mermaid knowledge graph, designed for use as system-prompt context by downstream PR or implementation agents

### 3. Subagent with Tool Permissions on External Repos

*(Planned)* Create subagents that operate on external repositories with configurable tool permissions, enabling automated code analysis and modification workflows across multiple codebases.

### 4. Repository Q&A

Answer questions about a target repository including:
- Diagram of repository structure
- Module inventory and dependencies
- Per-module implementation details

## Command-Line Options

```
-m, --model <ID>       Model ID to use (e.g., gpt-4o). Skips interactive selection.
-q, --question <N>     Question number from the Questions/ folder. Runs non-interactively and exits.
-r, --repo <PATH>      Path to a repository for context.
-p, --param key=value  Question parameter. Repeatable. Resolves {key} placeholders in prompts.
-l, --list-models      Print all available models and exit.
--help                 Display help screen.
--version              Display version information.
```

## Configuration

Settings are loaded with the following priority (highest wins): **CLI args &rarr; environment variables &rarr; appsettings.json**.

| Setting | CLI Flag | Env Variable | appsettings.json | Description |
|---------|----------|-------------|------------------|-------------|
| Model | `-m`, `--model` | `CHAT_Chat__Model` | `Chat.Model` | Model ID (e.g., `gpt-4o`). Skips interactive selection. |
| Question | `-q`, `--question` | `CHAT_Chat__Question` | `Chat.Question` | Question number from the `Questions/` folder. Runs non-interactively and exits. |
| RepoPath | `-r`, `--repo` | `CHAT_Chat__RepoPath` | `Chat.RepoPath` | Path to a repository for context. |
| Params | `-p`, `--param` | `CHAT_Chat__Params__key` | `Chat.Params.key` | Key-value parameters for `{placeholder}` tokens in question prompts. Repeatable. |
| ListModels | `-l`, `--list-models` | `CHAT_Chat__ListModels` | `Chat.ListModels` | Print all available models and exit. |

### Questions Folder

Repository questions are stored as individual `.md` files in the `Questions/` folder. Files follow the naming convention `NN-title.md` where `NN` is the question number:

```
Questions/
├── 01-repo-structure.md         # Repository structure diagram
├── 02-feature-inventory.md      # Feature-level inventory and discovery
├── 03-feature-details.md        # Deep-dive into a specific feature (requires -f)
├── 04-build-and-test.md         # Build and test instructions
└── 05-architecture.md           # Overall architecture description
```

Add new questions by creating a new numbered `.md` file in the folder. Use `{placeholder}` tokens for dynamic values — they are resolved from `-p key=value` args, or the user is prompted interactively if not provided.

### Output

When running a question (via `-q` or interactive `question <N>`), the response is automatically saved to:

```
output/{modelName}/{questionSlug}/
├── 01-overview.md
├── 02-storage-persistence-layer.md
├── 03-security.md
└── ...
```

Each `##` section in the response becomes a separate `.md` file, one per subtask.

### Examples

```bash
# List all available models
dotnet run --project src/agents/Chat/Chat.csproj -- -l

# Interactive mode with a specific model
dotnet run --project src/agents/Chat/Chat.csproj -- -m gpt-4o

# Run question 1 (repo structure) against a repo and exit
dotnet run --project src/agents/Chat/Chat.csproj -- -q 1 -r /path/to/repo -m gpt-4o

# Run question 2 (feature inventory) non-interactively
dotnet run --project src/agents/Chat/Chat.csproj -- -q 2 -r /path/to/repo

# Run question 3 (feature deep-dive) with a parameter
dotnet run --project src/agents/Chat/Chat.csproj -- -q 3 -p feature="ETW parsing" -r /path/to/repo -m gpt-4o

# Run question 3 with multi-agent orchestration (always on)
dotnet run --project src/agents/Chat/Chat.csproj -- -q 3 -p feature="hybridcache" -r /path/to/repo -m claude-opus-4.6

# Multiple parameters (for future questions with multiple placeholders)
dotnet run --project src/agents/Chat/Chat.csproj -- -q 3 -p feature="ETW parsing" -p component=Parser

# Via appsettings.json (edit src/agents/Chat/appsettings.json)
{
  "Chat": {
    "Model": "gpt-4o",
    "Question": 3,
    "Params": {
      "feature": "ETW parsing"
    },
    "RepoPath": "/path/to/repo"
  }
}

# Via environment variables
export CHAT_Chat__Model=gpt-4o
export CHAT_Chat__RepoPath=/path/to/repo
export CHAT_Chat__Question=1
dotnet run --project src/agents/Chat/Chat.csproj
```

## Project Structure

```
Chat/
├── Program.cs                  # Entry point: config loading, init + interactive loop
├── ChatSettings.cs             # Configuration settings model
├── CommandLineOptions.cs       # CLI argument definitions (CommandLineParser)
├── appsettings.json            # Default configuration file
├── Questions/                  # Repository question prompts (NN-title.md)
│   ├── 01-repo-structure.md
│   ├── 02-feature-inventory.md
│   ├── 03-feature-details.md
│   ├── 04-build-and-test.md
│   └── 05-architecture.md
└── Helpers/
    ├── CliChecker.cs           # Copilot CLI install & auth verification
    ├── ModelSelector.cs        # Dynamic model listing & selection (+PrintModelsAsync)
    ├── ChatHelper.cs           # Streaming message send/receive (returns response text)
    ├── QuestionLoader.cs       # Load question prompts from Questions/ folder
    ├── OutputWriter.cs         # Split response by ## headers, save to PromptOutput/ files
    ├── OrchestrationRunner.cs  # Multi-agent orchestration: LLM planner + DAG executor + plan file
    └── DemoPrompts.cs          # 6 built-in demo prompts
```

## Dependencies

| Package | Purpose |
|---------|---------|
| `GitHub.Copilot.SDK` | Copilot client, sessions, model listing, streaming events |
| `CopilotAgentOrchestrator` | Multi-agent DAG orchestration, subagent scheduling, result aggregation |
| `Spectre.Console` | Rich console output (FigletText banner, markup, prompts) |
| `Microsoft.Extensions.Configuration.Json` | Load settings from appsettings.json |
| `Microsoft.Extensions.Configuration.EnvironmentVariables` | Load settings from environment variables |
| `Microsoft.Extensions.Configuration.Binder` | Bind configuration sections to typed objects |
| `CommandLineParser` | Parse command-line arguments (-m, -q, -r, -p, -l) |

## Prerequisites

1. **Copilot CLI** installed (`copilot --version` must succeed)
2. **Authentication** via one of:
   - `GH_TOKEN` environment variable with Copilot Requests permission
   - Interactive login: run `copilot` then type `/login`

## Running

```bash
# Interactive (default)
dotnet run --project src/agents/Chat/Chat.csproj

# With options
dotnet run --project src/agents/Chat/Chat.csproj -- -m gpt-4o -r /path/to/repo

# List models
dotnet run --project src/agents/Chat/Chat.csproj -- -l

# Run a question non-interactively (output saved to PromptOutput/{model}/{question}/)
dotnet run --project src/agents/Chat/Chat.csproj -- -q 2 -r /path/to/repo -m claude-opus-4.6

# Run question with multi-agent orchestration (plan + context.md saved alongside output)
dotnet run --project src/agents/Chat/Chat.csproj -- -q 3 -p feature="hybridcache" -r /path/to/repo -m claude-opus-4.6
```