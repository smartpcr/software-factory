# Forge crew souls

One file per crew member. Each `<id>/soul.md` is the system prompt the runtime
loads at task start (Phase 11.3). Front-matter declares the agent's tool
allowlist + model preference + character handle; the body is the persona.

The persona block has four sections by convention:

1. **Tone** — voice and register the agent writes in.
2. **Temper** — how the agent reacts under pressure (failing tests, conflict,
   unclear specs).
3. **Humor** — what makes them laugh, what they don't joke about.
4. **Capability — software factory adaptation** — the canonical Ne Zha trait
   translated into concrete engineering work.

Then **Operating principles** (3-7 bullets) and **Refusals** (when they push
back). Keep each soul under ~80 lines so the prompt cache stays cheap.

## Prototype-id → character-id mapping

The DB seed (`ForgeSeed.cs`) creates eight agents with the prototype's old
A1/A2/E4/… ids. Each row's `SoulPath` is set to the matching character file
in this directory:

| Prototype id | Character id | Name              | Role |
|--------------|--------------|-------------------|------|
| A1           | YS           | Yuan Shi Tianzun  | arch |
| A2           | WZ           | Wen Zhong         | arch |
| E4           | NZ           | Ne Zha            | eng  |
| E7           | AB           | Ao Bing           | eng  |
| E9           | ZR           | Zhu Rong          | eng  |
| R3           | SG           | Shen Gongbao      | rev  |
| R5           | YX           | Yun Xiao          | rev  |
| Q2           | SF           | Shun Feng'er      | qa   |

The remaining twelve files (`TY`, `LJ`, `BG`, `GG`, `YL`, `MB`, `JZ`, `YL2`,
`QK`, `HD`, `ZZ`) are pre-authored personas waiting for their agent rows —
they ship as docs today and become real on the next Phase-11 seed expansion.
