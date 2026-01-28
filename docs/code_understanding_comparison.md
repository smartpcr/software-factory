# Code Intelligence Comparison: AI Coding Tools

> Last updated: 2026-01-23

A deep dive analysis comparing how leading AI coding tools gain code intelligence and understand entire repositories, not just open files.

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Claude Code](#claude-code)
3. [GitHub Copilot (Web/API)](#github-copilot)
4. [GitHub Copilot IDE Extensions](#github-copilot-ide-extensions)
5. [Copilot Multi-Model Architecture](#copilot-multi-model-architecture)
6. [OpenAI Codex CLI](#openai-codex-cli)
7. [OpenCode](#opencode)
8. [Comparison Matrix](#comparison-matrix)
9. [Trade-offs Analysis](#trade-offs-analysis)
10. [Conclusion](#conclusion)

---

## Executive Summary

| Tool | Primary Method | Semantic Understanding | Real-time Errors |
|------|---------------|----------------------|------------------|
| **Claude Code** | Grep/Glob + LLM reasoning | No (inference only) | No (must run compiler) |
| **GitHub Copilot (Web)** | Embeddings + RAG | Partial (semantic search) | No |
| **Copilot IDE Extension** | Local index + Open files + Embeddings | Partial (semantic search) | No (doesn't use LSP) |
| **Codex CLI** | File reading + LLM reasoning | No (inference only) | No (must run tests) |
| **OpenCode** | LSP + Grep/Glob | **Yes** (compiler-level) | **Yes** (real-time) |

---

## Claude Code

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                        Claude Code                              │
│                                                                 │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────────────┐  │
│  │  CLAUDE.md  │───►│   Tools     │───►│  Claude (Sonnet/    │  │
│  │  Context    │    │  Grep/Glob  │    │  Opus) 200K ctx     │  │
│  │             │    │  Read/Bash  │    │                     │  │
│  └─────────────┘    └─────────────┘    └─────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

### How It Gains Code Intelligence

**1. Initial Repository Scanning**

When Claude Code starts, it performs an initial scan to build understanding:
- Reads `CLAUDE.md` files (project configuration/context)
- Analyzes project structure via directory listing
- Identifies key files (README, package.json, etc.)

**2. Tool-Based Search**

| Tool | Purpose | Implementation |
|------|---------|----------------|
| **Grep** | Content search | Ripgrep-based regex search |
| **Glob** | File pattern matching | Fast glob patterns (`**/*.ts`) |
| **Read** | File reading | Direct file content access |
| **Bash** | Command execution | Run tests, linters, compilers |

**3. CLAUDE.md Configuration System**

The `CLAUDE.md` file serves as the agent's "constitution":
- Project structure documentation
- Coding standards and conventions
- Build/test commands
- Architecture decisions

> *"CLAUDE.md files are naively dropped into context up front"* — Anthropic Engineering

**4. Sub-Agent Architecture**

Claude Code uses specialized sub-agents:
- **Explore agent** — Codebase search (Haiku model, lightweight)
- **Plan agent** — Architecture planning
- **Custom agents** — User-defined in `.claude/agents/`

### Workflow for Code Understanding

```
User Query: "Where is user authentication handled?"
                    │
                    ▼
    ┌───────────────────────────────┐
    │  1. Grep for "auth", "login"  │
    │     "authenticate", etc.      │
    └───────────────┬───────────────┘
                    │
                    ▼
    ┌───────────────────────────────┐
    │  2. Read matching files       │
    │     (batch multiple files)    │
    └───────────────┬───────────────┘
                    │
                    ▼
    ┌───────────────────────────────┐
    │  3. LLM reasons about code    │
    │     structure and flow        │
    └───────────────┬───────────────┘
                    │
                    ▼
    ┌───────────────────────────────┐
    │  4. Return explanation with   │
    │     file paths and context    │
    └───────────────────────────────┘
```

### Limitations

- **No type resolution** — Can't know return types without reading definitions
- **No real-time errors** — Must run `tsc`, `eslint` via Bash to catch errors
- **Token cost** — Reading many files consumes context window (~20K base tokens)
- **Grep false positives** — String matches don't distinguish usage from comments

### Sources

- [Claude Code GitHub](https://github.com/anthropics/claude-code)
- [Using CLAUDE.md Files](https://claude.com/blog/using-claude-md-files)
- [Claude Code Built-in Tools Reference](https://www.vtrivedy.com/posts/claudecode-tools-reference)
- [Analysis of Claude Code's Execution](https://weaxsey.org/en/articles/2025-10-12/)

---

## GitHub Copilot

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                      GitHub Copilot                             │
│                                                                 │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────────────┐  │
│  │ Local Files │    │  GitHub     │    │                     │  │
│  │ + Open Tabs │───►│  Embeddings │───►│   GPT-4 / Codex     │  │
│  │             │    │  Index      │    │                     │  │
│  └─────────────┘    └─────────────┘    └─────────────────────┘  │
│                           │                                     │
│                     Vector Search                               │
│                     (Semantic RAG)                              │
└─────────────────────────────────────────────────────────────────┘
```

### How It Gains Code Intelligence

**1. Repository Indexing (Server-Side)**

Copilot creates a **semantic index** of your codebase on GitHub's servers:
- Triggered automatically when opening Copilot Chat
- Builds embeddings from default branch (main/master)
- Updates automatically as changes are pushed
- Uses OpenAI embedding model specialized for code

> *"GitHub converts your repo's code into vector embeddings so Copilot can semantically search and reference it."*

**2. Dual Search Strategy**

When you ask a question, Copilot runs **parallel searches**:

| Search Type | Source | Timeout | Purpose |
|-------------|--------|---------|---------|
| **Remote** | GitHub's `/embeddings/code_search` API | — | Semantically ranked chunks from indexed commit |
| **Local Diff** | Your working tree | 15s | Recently modified files not yet pushed |

**3. Instant Indexing**

For unindexed repositories:
- Attempts instant indexing on first query
- Polls every second for up to 8 seconds
- Small repos can complete within this window
- Large repos: initial indexing takes up to 60 seconds

**4. Eligibility Gates**

The search strategy has prerequisites:
- Workspace must have GitHub remote
- Repository must be indexed on GitHub's servers
- Local diff < 2,000 files
- Diff < 70% of total workspace
- Valid GitHub authentication tokens

### Local Context Sources

| Source | Description |
|--------|-------------|
| **Current file** | File you're editing |
| **Open tabs** | All files open in editor |
| **Cursor position** | Surrounding code context |
| **Git diff** | Uncommitted changes |
| **File paths** | Directory structure hints |

### Limitations

- **Index staleness** — Only indexes pushed commits on default branch
- **Local file limit** — Falls back to basic indexing beyond 2,500 files
- **No type information** — Embeddings capture patterns, not compiler semantics
- **No error awareness** — Doesn't see TypeScript/ESLint diagnostics
- **GitHub dependency** — Requires GitHub remote and authentication

### Sources

- [Indexing Repositories for Copilot Chat](https://docs.github.com/copilot/concepts/indexing-repositories-for-copilot-chat)
- [How GitHub Copilot Knows Your Code](https://yasithrashan.medium.com/how-github-copilot-knows-your-code-inside-its-indexing-magic-aba59a0ce0e8)
- [Instant Semantic Code Search Indexing](https://github.blog/changelog/2025-03-12-instant-semantic-code-search-indexing-now-generally-available-for-github-copilot/)
- [How Copilot Chat Uses Context](https://learn.microsoft.com/en-us/visualstudio/ide/copilot-context-overview)

---

## GitHub Copilot IDE Extensions

### Overview

GitHub Copilot's IDE extensions (VS Code, Visual Studio, JetBrains) run alongside your editor and have access to richer local context than the web-based Copilot. However, they still **do not use LSP** for semantic code understanding.

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────┐
│                  GitHub Copilot IDE Extension                           │
│                                                                         │
│  ┌───────────────────────────────────────────────────────────────────┐  │
│  │                      Context Sources                              │  │
│  │                                                                   │  │
│  │  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐  ┌──────────┐  │  │
│  │  │  Current    │  │   Open      │  │  Workspace  │  │  Remote  │  │  │
│  │  │  Editor     │  │   Tabs      │  │   Index     │  │  Index   │  │  │
│  │  │             │  │   (3-5)     │  │  (Local)    │  │ (GitHub) │  │  │
│  │  └──────┬──────┘  └──────┬──────┘  └──────┬──────┘  └────┬─────┘  │  │
│  │         │                │                │              │        │  │
│  │         └────────────────┴────────────────┴──────────────┘        │  │
│  │                                   │                               │  │
│  └───────────────────────────────────┼───────────────────────────────┘  │
│                                      ▼                                  │
│  ┌───────────────────────────────────────────────────────────────────┐  │
│  │                    Context Assembly                               │  │
│  │                                                                   │  │
│  │  • Cursor position + surrounding lines                            │  │
│  │  • Selected text / visible code                                   │  │
│  │  • File paths and directory structure                             │  │
│  │  • Language/framework detection                                   │  │
│  │  • Git diff (uncommitted changes)                                 │  │
│  └───────────────────────────────────┬───────────────────────────────┘  │
│                                      │                                  │
│                                      ▼                                  │
│                            ┌─────────────────┐                          │
│                            │   GPT-4 / o1    │                          │
│                            │   (OpenAI API)  │                          │
│                            └─────────────────┘                          │
└─────────────────────────────────────────────────────────────────────────┘
```

### How It Gains Code Intelligence

**1. Multi-Tier Indexing System**

The IDE extension uses a **three-tier indexing** approach:

| Index Type | Location | File Limit | Best For |
|------------|----------|------------|----------|
| **Remote Index** | GitHub servers | Unlimited | Large repos, team sharing |
| **Local Advanced Index** | Your machine | 2,500 files | Medium repos, fast queries |
| **Basic Index** | Your machine | Unlimited | Fallback for large local repos |

> *"If you can't use a remote index, VS Code can use an advanced semantic index stored on your local machine. Currently, local indexes are limited to 2,500 indexable files."*

**2. Workspace Context Sources**

The extension searches the same sources a developer would use:

| Source | Description | Access Method |
|--------|-------------|---------------|
| **Workspace Index** | All indexable files (except .gitignore'd) | Embeddings search |
| **Directory Structure** | Nested folders and file names | Direct scan |
| **Code Symbols** | Classes, functions, variables | Basic parsing |
| **Active Editor** | Selected text, visible code | Direct access |
| **Open Tabs** | Files currently open | Direct access |

**3. Hybrid Remote + Local Strategy**

For GitHub-hosted repos:

```
User Query
    │
    ├──────────────────────────────────────┐
    │                                      │
    ▼                                      ▼
┌─────────────────────┐          ┌─────────────────────┐
│    Remote Search    │          │   Local Diff Search │
│  (GitHub Embeddings │          │   (Modified files   │
│   API)              │          │    since last push) │
│                     │          │                     │
│  • Indexed commit   │          │  • 15s timeout      │
│  • Semantic ranking │          │  • <2000 files      │
│  • Fast for large   │          │  • TF-IDF fallback  │
│    repos            │          │                     │
└─────────┬───────────┘          └──────────┬──────────┘
          │                                 │
          └─────────────┬───────────────────┘
                        │
                        ▼
              Combined Results
```

**4. Context Types**

| Type | Description | Example |
|------|-------------|---------|
| **Calculated** | Auto-determined by extension | `@workspace` analyzes prompt, selects files |
| **Explicit** | User-specified references | `#selection`, `#editor`, attached files |
| **Implicit** | Always included | Current file, cursor position |

**5. Open Files Impact**

> *"Copilot's suggestions weight local context heavily. Closing noisy files cuts nonsense. Keeping just 3–5 relevant files open boosted 'first-try useful' suggestions from ~62% to ~74%."*

By default, only the active file is attached. Users can manually attach more via "Attach Context" button.

### What Copilot IDE Extension Does NOT Do

**No LSP Integration** (as of late 2025):

> *"GitHub Copilot relies on search and grep for code analysis, limiting context understanding. Add LSP support for better symbol resolution, type checking, and cross-references to improve suggestion accuracy."* — [VS Code Feature Request #285177](https://github.com/microsoft/vscode/issues/285177)

| Capability | Status | Impact |
|------------|--------|--------|
| Type resolution | ❌ Not available | Can't know function return types |
| Error detection | ❌ Not available | Doesn't see TypeScript/ESLint errors |
| Import resolution | ❌ Not available | Can't resolve path aliases |
| Go to definition | ❌ Not available | Uses embedding similarity instead |
| Find references | ❌ Not available | Uses text search instead |

### VS Code vs Visual Studio Differences

| Feature | VS Code Extension | Visual Studio Extension |
|---------|-------------------|------------------------|
| Local indexing | ✅ Advanced + Basic | ✅ Advanced + Basic |
| Remote indexing | ✅ GitHub/Azure DevOps | ✅ GitHub/Azure DevOps |
| @workspace chat | ✅ Full support | ✅ Full support |
| MCP support | ✅ (newer versions) | Limited |
| Inline suggestions | ✅ | ✅ |
| Chat panel | ✅ | ✅ |

### MCP Integration (Recent Addition)

Copilot now supports Model Context Protocol for extensibility:

> *"You can install Model Context Protocol (MCP) servers or tools from Marketplace extensions to further enhance the capabilities of the autonomous coding experience—for example, pull information from a database or connect to external APIs."*

### Limitations

- **No LSP** — Doesn't leverage the language servers already running in your IDE
- **Index staleness** — Remote index only reflects pushed commits
- **Local file limit** — Advanced indexing caps at 2,500 files
- **Open file dependency** — Quality heavily depends on having right files open
- **No error awareness** — Can suggest code that won't compile

### Why No LSP?

Possible reasons (not officially stated):

1. **Performance** — LSP queries can be slow for large codebases
2. **Complexity** — Would need to support every language server
3. **Reliability** — LSP servers can crash or return inconsistent results
4. **Architecture** — Copilot's cloud-based design doesn't easily integrate with local LSP

> *"The lack of LSP integration is noted as 'oddly also a strong point for working in larger code bases where spinning up an LSP server/client can add performance issues.'"*

### Sources

- [GitHub Copilot in VS Code](https://code.visualstudio.com/docs/copilot/overview)
- [Workspace Context for Copilot](https://code.visualstudio.com/docs/copilot/reference/workspace-context)
- [Manage Context for AI](https://code.visualstudio.com/docs/copilot/chat/copilot-chat-context)
- [VS Code LSP Feature Request](https://github.com/microsoft/vscode/issues/285177)
- [MCP Integration for Copilot](https://docs.github.com/en/copilot/using-github-copilot/using-extensions-to-integrate-external-tools-with-copilot-chat)

---

## Copilot Multi-Model Architecture

### How Copilot Works with Claude Opus 4.5 (and Other Models)

As of late 2025, GitHub Copilot supports multiple AI models including **Claude Opus 4.5**, GPT-4, GPT-4o, and o1. Understanding how repo-level code intelligence flows to these models is key to understanding Copilot's architecture.

### The Critical Insight: Model-Agnostic Context Assembly

**The AI model (whether Claude, GPT, or any other) does NOT directly access your repository.**

Instead, Copilot uses a **proxy architecture** where:

1. Context is assembled **before** reaching any AI model
2. The same context pipeline serves **all** models
3. Models receive pre-packaged prompts, not raw repo access

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                    Copilot Multi-Model Architecture                         │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │                    1. Context Assembly (IDE Extension)              │    │
│  │                                                                     │    │
│  │   ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────────────┐    │    │
│  │   │ Current  │  │  Open    │  │  Local   │  │ Remote Embeddings│    │    │
│  │   │  File    │  │  Tabs    │  │  Index   │  │    (GitHub)      │    │    │
│  │   └────┬─────┘  └────┬─────┘  └────┬─────┘  └────────┬─────────┘    │    │
│  │        │             │             │                  │             │    │
│  │        └─────────────┴─────────────┴──────────────────┘             │    │
│  │                              │                                      │    │
│  │                              ▼                                      │    │
│  │                 ┌────────────────────────┐                          │    │
│  │                 │   Prompt Construction  │                          │    │
│  │                 │   • Score snippets     │                          │    │
│  │                 │   • Rank by relevance  │                          │    │
│  │                 │   • Fit token budget   │                          │    │
│  │                 └───────────┬────────────┘                          │    │
│  └─────────────────────────────┼───────────────────────────────────────┘    │
│                                │                                            │
│                                ▼                                            │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │                    2. GitHub Proxy Service                          │    │
│  │                    (Azure-hosted, GitHub-owned)                     │    │
│  │                                                                     │    │
│  │   • Rate limiting        • Toxic content filtering                  │    │
│  │   • Authentication       • Compliance checks                        │    │
│  │   • Model routing        • No data retention                        │    │
│  └─────────────────────────────┬───────────────────────────────────────┘    │
│                                │                                            │
│                    ┌───────────┴───────────┐                                │
│                    │    Model Selection    │                                │
│                    │    (User's choice)    │                                │
│                    └───────────┬───────────┘                                │
│           ┌────────────────────┼────────────────────┐                       │
│           │                    │                    │                       │
│           ▼                    ▼                    ▼                       │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐              │
│  │   Claude Opus   │  │    GPT-4 /      │  │      o1         │              │
│  │      4.5        │  │    GPT-4o       │  │   (reasoning)   │              │
│  │   (Anthropic)   │  │   (OpenAI)      │  │   (OpenAI)      │              │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘              │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### What Data Is Sent to Claude (or Any Model)?

When you use Copilot with Claude Opus 4.5, the model receives a **constructed prompt** containing:

| Data Type | Source | Example |
|-----------|--------|---------|
| **User prompt** | Your question/request | "How does the auth flow work?" |
| **Current file content** | Active editor | Lines around cursor |
| **Selected code** | User selection | Highlighted code block |
| **Relevant snippets** | Embeddings search | Top-ranked code chunks |
| **File paths** | Workspace structure | `src/auth/oauth.ts` |
| **Custom instructions** | `.github/copilot-instructions.md` | Project-specific guidance |
| **Previous messages** | Chat history | Prior Q&A in session |

### What Is NOT Sent

| Data | Why Not |
|------|---------|
| **Full repository** | Token limits, privacy |
| **Unrelated files** | Relevance scoring excludes them |
| **LSP type information** | Not integrated (as discussed) |
| **Compiler errors** | Not available to Copilot |
| **Git history** | Only current file state |

### Prompt Construction Process

The IDE extension builds prompts through a sophisticated pipeline:

```
1. GATHER CANDIDATES
   │
   ├── Current file (prefix + suffix around cursor)
   ├── Open tabs (neighboring files)
   ├── Symbol references (function calls, imports)
   ├── Embedding matches (semantic similarity)
   └── Import graph (related modules)

2. SCORE & RANK
   │
   ├── Proximity to cursor
   ├── Semantic similarity to query
   ├── Symbol relevance
   ├── Recency of access
   └── File importance

3. BUDGET ALLOCATION
   │
   ├── Model token limit (8K-32K+)
   ├── Reserve space for response
   ├── Prioritize highest-scored snippets
   └── Truncate or exclude low-value content

4. PROMPT ASSEMBLY
   │
   ├── System instructions
   ├── Custom repo instructions
   ├── Ranked code snippets
   ├── User query
   └── Chat history (if applicable)
```

> *"Copilot does not include full files indiscriminately; instead, it breaks them into logical snippet blocks, each scored based on proximity to the cursor, semantic similarity, symbol relevance, and recency of access."*

### Model-Specific Behavior

| Aspect | Claude Opus 4.5 | GPT-4 / GPT-4o | o1 |
|--------|-----------------|----------------|-----|
| **Context window** | 200K tokens | 8K-128K tokens | 128K tokens |
| **Prompt handling** | Same pipeline | Same pipeline | Same pipeline |
| **Reasoning** | Hybrid (instant/extended) | Standard | Extended thinking |
| **Pricing** | Premium multiplier | Standard | Premium multiplier |
| **Strengths** | Complex reasoning, agentic | Balanced | Deep reasoning |

### Key Takeaway: Models Are "Blind" to Your Repo

**Claude Opus 4.5 (or any model) cannot:**

- Browse your repository directly
- Query your LSP for type information
- See compiler errors
- Access files not included in the prompt
- Remember context across sessions (unless explicitly included)

The model only sees what Copilot's context assembly pipeline chooses to include. This is why:

1. **Open files matter** — They're weighted heavily in context
2. **Embeddings search is critical** — It finds relevant code not open
3. **Token limits constrain** — Large repos can't fit entirely
4. **No type safety** — Models infer types, don't know them

### Multi-Model Selection in Practice

Users can select models in the IDE:

```
VS Code: Click model name in Copilot Chat → Select from dropdown
         Options: Auto, GPT-4o, Claude Opus 4.5, o1, etc.

"Auto" mode: Copilot selects based on:
  - Query complexity
  - Model availability
  - Cost optimization
```

### Admin Controls (Enterprise)

For Copilot Business/Enterprise:
- Admins must **explicitly enable** Claude Opus 4.5
- Policy controls which models are available
- Audit logs track model usage

### Sources

- [Claude Opus 4.5 in GitHub Copilot](https://github.blog/changelog/2025-12-18-claude-opus-4-5-is-now-generally-available-in-github-copilot/)
- [Supported AI Models in Copilot](https://docs.github.com/en/copilot/reference/ai-models/supported-models)
- [GitHub Copilot Data Pipeline Security](https://resources.github.com/learn/pathways/copilot/essentials/how-github-copilot-handles-data/)
- [The Life of a Copilot Prompt](https://devblogs.microsoft.com/all-things-azure/github-copilot-chat-explained-the-life-of-a-prompt/)
- [How Copilot Handles Multi-File Context](https://dzone.com/articles/github-copilot-multi-file-context-internal-architecture)

---

## OpenAI Codex CLI

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                      Codex CLI (Rust)                           │
│                                                                 │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────────────┐  │
│  │ AGENTS.md   │    │  Built-in   │    │                     │  │
│  │ Hierarchy   │───►│  Tools      │───►│  GPT-5.2-Codex      │  │
│  │             │    │  + MCP      │    │                     │  │
│  └─────────────┘    └─────────────┘    └─────────────────────┘  │
│                                                                 │
│                    Full-Screen TUI                              │
│                    Human-in-the-Loop                            │
└─────────────────────────────────────────────────────────────────┘
```

### How It Gains Code Intelligence

**1. AGENTS.md Configuration System**

Codex uses `AGENTS.md` files (similar to Claude's `CLAUDE.md`) with hierarchical inheritance:

```
Discovery Order:
1. ~/.codex/AGENTS.override.md (global)
2. ~/.codex/AGENTS.md (global fallback)
3. <repo-root>/AGENTS.md
4. <repo-root>/<subdir>/AGENTS.md
5. ... (down to current working directory)
```

> *"Messages are injected near the top of the conversation history, before the user prompt, in root-to-leaf order."*

**2. Directory-Level Inheritance**

- Each package can have its own `AGENTS.md`
- Nearest file takes precedence
- The OpenAI repo itself has 88 AGENTS.md files

**3. Built-in Tools**

Codex CLI can:
- Read files in the selected directory
- Make edits to code
- Run commands (with sandboxing/approval)
- Execute in cloud sandbox environments

**4. MCP Integration**

Codex supports Model Context Protocol servers:
- Configure in `~/.codex/config.toml`
- STDIO or streaming HTTP servers
- Launches automatically when session starts
- Extends tools beyond built-ins

**5. GPT-5.2-Codex Model Capabilities**

The model itself has improved:
- Long-context understanding
- Reliable tool calling
- Native compaction (context efficiency)
- Repo-scale reasoning

> *"With these improvements, Codex is more capable at working in large repositories over extended sessions with full context intact."*

### Workflow

```
┌───────────────────────────────────────────────────────┐
│                 Codex CLI Session                     │
│                                                       │
│  1. Load AGENTS.md hierarchy (global → repo → cwd)    │
│  2. User provides task                                │
│  3. Codex reads relevant files                        │
│  4. Plans approach                                    │
│  5. Makes edits (with human approval)                 │
│  6. Runs tests/commands                               │
│  7. Iterates until complete                           │
└───────────────────────────────────────────────────────┘
```

### Limitations

- **No semantic indexing** — Relies on file reading, not embeddings
- **No LSP integration** — Can't get type info without reading source
- **No real-time errors** — Must run tests/compiler to catch issues
- **Context window limits** — Even with compaction, large repos can exceed limits

### Sources

- [Codex CLI Features](https://developers.openai.com/codex/cli/features/)
- [Custom Instructions with AGENTS.md](https://developers.openai.com/codex/guides/agents-md)
- [Codex CLI GitHub](https://github.com/openai/codex)
- [Introducing GPT-5.2-Codex](https://openai.com/index/introducing-gpt-5-2-codex/)

---

## OpenCode

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                        OpenCode                                 │
│                                                                 │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────────────┐  │
│  │    LSP      │    │   Tools     │    │  Any LLM Provider   │  │
│  │  Servers    │───►│  Grep/Glob  │───►│  (Claude, GPT,      │  │
│  │  (15+)      │    │  Read/Bash  │    │   Gemini, etc.)     │  │
│  └─────────────┘    └─────────────┘    └─────────────────────┘  │
│        │                                                        │
│        ▼                                                        │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │              Real-time Semantic Analysis                │    │
│  │  • Type resolution    • Cross-file navigation           │    │
│  │  • Error detection    • Symbol search                   │    │
│  │  • Import resolution  • Call hierarchy                  │    │
│  └─────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────┘
```

### How It Gains Code Intelligence

**1. Language Server Protocol (LSP) Integration**

OpenCode spawns and manages **15+ language servers**:

| Language | Server | Auto-detected via |
|----------|--------|-------------------|
| TypeScript/JS | `typescript-language-server` | `package.json`, lockfiles |
| Python | `pyright` | `pyproject.toml`, `requirements.txt` |
| Rust | `rust-analyzer` | `Cargo.toml` |
| Go | `gopls` | `go.mod` |
| C/C++ | `clangd` | `compile_commands.json` |
| Java | `jdtls` | `pom.xml`, `build.gradle` |
| And more... | | |

**2. Semantic Operations**

LSP provides compiler-level understanding:

```typescript
// Available operations:
LSP.diagnostics()      // Real-time errors and warnings
LSP.definition()       // Jump to definition
LSP.references()       // Find all usages
LSP.hover()            // Type information
LSP.workspaceSymbol()  // Search symbols by name
LSP.documentSymbol()   // File structure
LSP.incomingCalls()    // Who calls this function?
LSP.outgoingCalls()    // What does this function call?
```

**3. Real-Time Error Detection**

After every edit:
```
Edit made → LSP.touchFile() → Language server analyzes
                                      │
                                      ▼
                         Diagnostics published
                                      │
                                      ▼
                    AI sees errors immediately
                    Can fix before committing
```

**4. Traditional Search Tools**

OpenCode also has grep/glob/read tools like other tools:
- **Grep** — Ripgrep-based content search
- **Glob** — File pattern matching
- **Read** — Direct file reading
- **Bash** — Command execution

**5. Configuration System**

Uses `opencode.json` / `opencode.jsonc`:
- Custom agents with specific permissions
- MCP server configuration
- LSP server overrides
- Plugin system

### Unique Advantages

**Type-Aware Editing:**
```typescript
// user.ts
export interface User {
  role: 'admin' | 'user'
}

// service.ts
function promote(user: User) {
  user.role = 'superadmin'  // ❌ LSP immediately reports error!
}
```

**Precise Navigation:**
```
"Find all usages of UserService"

Other tools: grep "UserService" → includes comments, strings, imports
OpenCode:    LSP.references()   → exact usages from compiler
```

**Cross-File Understanding:**
```typescript
import { helper } from '@/utils/helper'
//                    ^^^^^^^^^^^^^^^^
// LSP resolves path alias to actual file
// Knows helper's signature, return type, etc.
```

### Sources

- [OpenCode LSP Implementation](packages/opencode/src/lsp/)
- [LSP Deep Dive Document](.claude/context/lsp-deep-dive.md)

---

## Comparison Matrix

### Code Understanding Methods

| Capability | Claude Code | Copilot Web | Copilot IDE | Codex CLI | OpenCode |
|------------|-------------|-------------|-------------|-----------|----------|
| **File Search** | Grep/Glob | Embeddings | Embeddings + Local | File read | Grep/Glob |
| **Semantic Search** | ❌ | ✅ (RAG) | ✅ (RAG) | ❌ | ✅ (LSP symbols) |
| **Type Resolution** | ❌ LLM inference | ❌ | ❌ | ❌ LLM inference | ✅ Compiler |
| **Import Resolution** | ❌ Must read files | Partial | Partial | ❌ Must read files | ✅ Automatic |
| **Error Detection** | Run compiler | ❌ | ❌ | Run tests | ✅ Real-time |
| **Cross-file Navigation** | Grep + reasoning | Embedding similarity | Embedding similarity | Read + reasoning | ✅ LSP definition/references |
| **Call Hierarchy** | ❌ | ❌ | ❌ | ❌ | ✅ |
| **Uses LSP** | ❌ | ❌ | ❌ | ❌ | ✅ |
| **Open Files Context** | N/A | N/A | ✅ (3-5 recommended) | N/A | N/A |

### Configuration Systems

| Feature | Claude Code | Copilot Web | Copilot IDE | Codex CLI | OpenCode |
|---------|-------------|-------------|-------------|-----------|----------|
| **Config File** | CLAUDE.md | — | — | AGENTS.md | opencode.json |
| **Hierarchical** | ✅ | — | — | ✅ | ✅ |
| **Custom Agents** | ✅ .claude/agents/ | ❌ | ❌ | Via AGENTS.md | ✅ .opencode/agents/ |
| **MCP Support** | ✅ | ❌ | ✅ (recent) | ✅ | ✅ |
| **Custom Tools** | ✅ .claude/tools/ | ❌ | Via MCP | Via MCP | ✅ .opencode/tools/ |

### Infrastructure

| Aspect | Claude Code | Copilot Web | Copilot IDE | Codex CLI | OpenCode |
|--------|-------------|-------------|-------------|-----------|----------|
| **Server Required** | ❌ Local | ✅ GitHub | ✅ GitHub + Local | Optional | ✅ Local |
| **Index Required** | ❌ | ✅ GitHub | ✅ (Remote or Local) | ❌ | ❌ |
| **Local Index Limit** | N/A | N/A | 2,500 files | N/A | N/A |
| **LLM Provider** | Anthropic only | OpenAI only | OpenAI only | OpenAI only | Any (20+ providers) |
| **Open Source** | ❌ | ❌ | ❌ | ✅ | ✅ |
| **Offline Capable** | ❌ (needs API) | ❌ | ❌ | ❌ (needs API) | Partial (local models) |

### Context Management

| Aspect | Claude Code | Copilot Web | Copilot IDE | Codex CLI | OpenCode |
|--------|-------------|-------------|-------------|-----------|----------|
| **Context Window** | ~200K tokens | Varies | Varies | GPT-5.2 extended | Provider-dependent |
| **Base Overhead** | ~20K tokens | — | — | — | Lower (LSP reduces file reads) |
| **Compaction** | Via sub-agents | N/A | N/A | Native in model | ✅ Session compaction |
| **@workspace Query** | N/A | ✅ | ✅ | N/A | N/A |

---

## Trade-offs Analysis

### Claude Code: Simplicity vs Precision

**Pros:**
- Simple architecture, fewer moving parts
- No language server setup/maintenance
- Works on any file, even broken syntax
- Large context window (200K)

**Cons:**
- Must read many files to understand types
- Errors caught only when running compiler
- Grep can have false positives
- Higher token usage for exploration

**Best for:** Quick tasks, unfamiliar codebases, polyglot projects

---

### GitHub Copilot (Web): Integration vs Dependency

**Pros:**
- Semantic search via embeddings
- No local setup required
- Shared index across team
- Works in browser

**Cons:**
- Requires GitHub hosting
- Index only on default branch
- No type-level understanding
- No local file context

**Best for:** Quick questions about GitHub-hosted code, browser-based workflows

---

### GitHub Copilot (IDE Extension): Rich Context vs No Semantics

**Pros:**
- Semantic search via embeddings (remote + local)
- Open files provide strong context signal
- @workspace for full repo queries
- MCP support for extensibility
- Hybrid remote/local search strategy

**Cons:**
- **No LSP integration** — Doesn't use type information from language servers
- Local index limited to 2,500 files
- Remote index only reflects pushed commits
- Can suggest code that doesn't compile
- Quality depends heavily on open files

**Best for:** IDE-centric workflows, GitHub-hosted projects, teams

> **Key insight:** Despite running inside VS Code/Visual Studio which has full LSP support, Copilot does NOT leverage it. This is a deliberate architectural choice, possibly for performance/reliability reasons.

---

### Codex CLI: Flexibility vs Context Limits

**Pros:**
- Open source, customizable
- Strong AGENTS.md inheritance
- MCP extensibility
- Cloud sandbox option

**Cons:**
- No semantic understanding
- Must read files for type info
- OpenAI-only

**Best for:** OpenAI users, highly customized workflows, cloud-first teams

---

### OpenCode: Power vs Complexity

**Pros:**
- **Compiler-level type understanding**
- **Real-time error detection**
- Precise navigation (definition, references, calls)
- Provider-agnostic (use any LLM)
- Reduces token usage (LSP provides info without reading)

**Cons:**
- Language server setup overhead
- Servers can crash/hang
- Memory usage for multiple servers
- Doesn't work on syntax-broken files

**Best for:** Large codebases, type-heavy languages (TypeScript, Rust, Go), accuracy-critical work

---

## Conclusion

### The Spectrum of Code Understanding

```
Simple                                                           Sophisticated
│                                                                           │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐  ┌──────────┐  ┌──────┐│
│  │ Claude Code │  │ Codex CLI   │  │Copilot IDE  │  │ Copilot  │  │Open- ││
│  │             │  │             │  │  Extension  │  │   Web    │  │ Code ││
│  │ Grep + LLM  │  │ Files + LLM │  │  Open Files │  │Embeddings│  │ LSP  ││
│  │ Reasoning   │  │ + AGENTS.md │  │+ Embeddings │  │  + RAG   │  │      ││
│  └─────────────┘  └─────────────┘  └─────────────┘  └──────────┘  └──────┘│
│                                                                           │
│  • Fast startup    • Flexible       • IDE context    • Semantic  • Ground │
│  • Works anywhere  • Customizable   • Open files     • Team      • truth  │
│  • Token-heavy     • Context limits • NO LSP!        • shared    • Types  │
└───────────────────────────────────────────────────────────────────────────┘
```

### Key Insight

The fundamental difference is **where intelligence comes from**:

| Approach | Intelligence Source | Accuracy | Tools Using It |
|----------|---------------------|----------|----------------|
| **Grep + LLM** | Model reasons about text patterns | Good, but can hallucinate | Claude Code, Codex CLI |
| **Embeddings** | Semantic similarity in vector space | Good for finding related code | Copilot (all) |
| **Open Files** | Direct editor access | Good for active work | Copilot IDE Extension |
| **LSP** | Actual compiler/type checker | **Ground truth** | **OpenCode only** |

### The Copilot IDE Paradox

A surprising finding: **GitHub Copilot IDE extensions do NOT use LSP**, despite running inside editors that have full language server support. This means:

- Copilot in VS Code doesn't know about TypeScript errors
- Copilot can suggest code that fails type checking
- The rich semantic information from language servers is unused

This is likely a deliberate trade-off for performance and reliability, but it represents a significant gap that OpenCode fills.

### Recommendation

| Use Case | Best Tool | Why |
|----------|-----------|-----|
| **Quick exploration** | Claude Code, Codex CLI | Simple, flexible, works anywhere |
| **Team workflows** | Copilot IDE | Shared index, tight integration |
| **Browser-based** | Copilot Web | No setup needed |
| **Production changes** | **OpenCode** | Type safety, real-time error detection |
| **Large type-heavy codebases** | **OpenCode** | LSP provides compiler-level accuracy |

### The Future

The trend in AI coding tools is moving toward **more semantic understanding**. While embeddings-based approaches (Copilot) and LLM reasoning (Claude Code, Codex) work well for many tasks, they fundamentally lack access to **ground truth** about types and errors.

OpenCode's LSP integration represents the current state-of-the-art for accuracy. As AI coding tools mature, we expect to see more adoption of LSP or similar semantic analysis, especially for:

- Production codebases where correctness matters
- Strongly-typed languages (TypeScript, Rust, Go)
- Large-scale refactoring
- Automated code review

The open question: Will tools like Copilot eventually add LSP support, or will the performance trade-offs keep them on the embeddings-only path?
