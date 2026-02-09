The search results conflate two very different products:

  ┌─────────────────────────────┬───────────────────────────────┬───────────────────────────────────┐
  │ Capability                  │ Claude.ai Chat Projects (web) │ Claude Code (CLI/IDE)             │
  ├─────────────────────────────┼───────────────────────────────┼───────────────────────────────────┤
  │ Subagent spawning           │ ❌ No                         │ ✅ Yes (Task tool)                │
  ├─────────────────────────────┼───────────────────────────────┼───────────────────────────────────┤
  │ Parallel workers            │ ❌ No                         │ ✅ Yes (background agents)        │
  ├─────────────────────────────┼───────────────────────────────┼───────────────────────────────────┤
  │ Fresh context per worker    │ ❌ No                         │ ✅ Yes (isolated context windows) │
  ├─────────────────────────────┼───────────────────────────────┼───────────────────────────────────┤
  │ Aggregation by orchestrator │ ❌ No                         │ ✅ Yes (main agent reads results) │
  └─────────────────────────────┴───────────────────────────────┴───────────────────────────────────┘

  What Chat Projects actually give you

   - A single agent with a system prompt + knowledge files attached
   - One shared, linear context window that accumulates every tool call
   - No ability to delegate, fork, or parallelize

  This is exactly why your direct-Opus output was better

  When you ran Opus directly (likely via Claude Code or the API), you were either:

   1. Feeding source files directly into the prompt (no exploration overhead), or
   2. Using Claude Code's Task tool which spawns subagents with fresh context — explore agents read files, return summaries, and the main agent synthesizes  
   with its full context budget intact.