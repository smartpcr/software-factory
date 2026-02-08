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

### 2. Subagent with Tool Permissions on External Repos

*(Planned)* Create subagents that operate on external repositories with configurable tool permissions, enabling automated code analysis and modification workflows across multiple codebases.

### 3. Repository Q&A

*(Planned)* Answer questions about a target repository including:
- Diagram of repository structure
- Module inventory and dependencies
- Per-module implementation details

## Command-Line Options

```
-m, --model <ID>       Model ID to use (e.g., gpt-4o). Skips interactive selection.
-q, --question <N>     Question number from the Questions/ folder. Runs non-interactively and exits.
-r, --repo <PATH>      Path to a repository for context.
-f, --feature <NAME>   Feature name for questions that use the {feature} placeholder.
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
| Feature | `-f`, `--feature` | `CHAT_Chat__Feature` | `Chat.Feature` | Feature name for `{feature}` placeholder in question prompts. |
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

Add new questions by creating a new numbered `.md` file in the folder. Use `{feature}` as a placeholder in any question that needs a feature name — the user will be prompted if `-f` is not provided.

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

# Run question 3 (feature deep-dive) for a specific feature
dotnet run --project src/agents/Chat/Chat.csproj -- -q 3 -f "ETW parsing" -r /path/to/repo -m gpt-4o

# Via appsettings.json (edit src/agents/Chat/appsettings.json)
{
  "Chat": {
    "Model": "gpt-4o",
    "Question": 3,
    "Feature": "ETW parsing",
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
    ├── OutputWriter.cs         # Split response by ## headers, save to output/ files
    └── DemoPrompts.cs          # 6 built-in demo prompts
```

## Dependencies

| Package | Purpose |
|---------|---------|
| `GitHub.Copilot.SDK` | Copilot client, sessions, model listing, streaming events |
| `Spectre.Console` | Rich console output (FigletText banner, markup, prompts) |
| `Microsoft.Extensions.Configuration.Json` | Load settings from appsettings.json |
| `Microsoft.Extensions.Configuration.EnvironmentVariables` | Load settings from environment variables |
| `Microsoft.Extensions.Configuration.Binder` | Bind configuration sections to typed objects |
| `CommandLineParser` | Parse command-line arguments (-m, -q, -r, -f, -l) |

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

# Run a question non-interactively (output saved to output/{model}/{question}/)
dotnet run --project src/agents/Chat/Chat.csproj -- -q 2 -r /path/to/repo -m claude-opus-4.6
```