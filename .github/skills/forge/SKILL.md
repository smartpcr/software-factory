---
name: forge
description: Prime session context for the Forge subsystem (src/forge/Forge.Api, src/forge/Forge.UI) by reading docs/forge_implementation.md. Use when the user says "forge", "prime forge", "load forge context", "what phase are we on", or starts work on anything under src/forge/.
---

# Forge — Implementation Plan Primer

Read `docs/forge_implementation.md` (absolute: `E:\work\github\crp\workflow\docs\forge_implementation.md`). It is the single source of truth for the Forge subsystem — phases, decisions, and guardrails.

The file is large (~750 KB). Use **grep** to extract the phase index first (pattern `^## (Phase \d+|Phase R|Phase legend|Resolved decisions|Goals|Hard constraint|Stack)`), then **view** with `view_range` for the prologue (lines 1–250) and the specific phase(s) relevant to the task. Do not load it end-to-end.

After reading, briefly report the plan frontier, the phase the task touches, and any guardrails from the doc that apply.
