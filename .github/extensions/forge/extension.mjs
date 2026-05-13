import { access, stat } from "node:fs/promises"
import path from "node:path"

import { joinSession } from "@github/copilot-sdk/extension"

const FORGE_DOC_REL = path.join("docs", "forge_implementation.md")
const sessionCwds = new Map()

const session = await joinSession({
    commands: [
        {
            name: "forge",
            description: "Load docs/forge_implementation.md as authoritative Forge implementation context. Optional arg focuses the summary (e.g. `/forge phase=42`, `/forge ui`, `/forge phase=55 stage=2`).",
            handler: async (context) => {
                await loadForgeContext(context)
            },
        },
    ],
    hooks: {
        onSessionStart: async (input, invocation) => {
            if (input?.cwd) sessionCwds.set(invocation.sessionId, input.cwd)
        },
        onUserPromptSubmitted: async (input, invocation) => {
            if (input?.cwd) sessionCwds.set(invocation.sessionId, input.cwd)
        },
        onSessionEnd: async (_input, invocation) => {
            sessionCwds.delete(invocation.sessionId)
        },
    },
})

async function loadForgeContext(context) {
    const cwd = sessionCwds.get(context.sessionId) || process.cwd()
    const docPath = await resolveForgeDoc(cwd)

    if (!docPath) {
        await session.log(
            `forge: could not find ${FORGE_DOC_REL} starting from ${cwd}. Run /forge from inside the workflow repo.`,
            { level: "error" }
        )
        return
    }

    let sizeKb = null
    try {
        const info = await stat(docPath)
        sizeKb = Math.round((info.size / 1024) * 10) / 10
    } catch {
        // best effort
    }

    const focus = (context.args || "").trim()
    const focusBlock = focus
        ? [
            `**Operator focus:** \`${focus}\`.`,
            `Locate the matching phase / stage / step in the spec, summarize its current status (Done / In progress / Pending / Blocked), list any open gaps, and surface the exact "Acceptance" or "Tests / proof" bullets attached to that scope. Quote section anchors (e.g. \`### Phase 55 — …\`) so subsequent prompts can reference them.`,
        ].join(" ")
        : [
            `**No focus given.** Briefly summarize:`,
            `1) the high-level Forge architecture (Forge.Api + Forge.UI under \`src/forge/\`),`,
            `2) the phase the document is currently anchored on (the latest phase with active TODOs / unchecked acceptance bullets),`,
            `3) any "Hard constraint" / "Operational rules" callouts that must be respected this session.`,
            `Keep it under ~25 lines.`,
        ].join(" ")

    const prompt = [
        `# /forge — load implementation plan`,
        ``,
        `Treat the attached \`${FORGE_DOC_REL}\` as the **authoritative implementation plan** for the Forge project (Forge.Api + Forge.UI under \`src/forge/\`) for the rest of this session. Trust it over your prior assumptions about Forge.`,
        ``,
        `Read it now (skim, then deep-read the sections you cite). When you answer:`,
        `- prefer the spec's terminology (phases, stages, steps, "Spawn", "Workstream", "Crew", "Pair", etc.),`,
        `- cite section anchors when you reference scope,`,
        `- never invent acceptance criteria — quote the spec or say "not in spec".`,
        ``,
        focusBlock,
        ``,
        `After the summary, **stop and wait** for the operator's next instruction. Do **not** start editing code, opening PRs, or running long jobs yet.`,
    ].join("\n")

    await session.log(
        `forge: loading ${path.relative(cwd, docPath) || FORGE_DOC_REL}${sizeKb !== null ? ` (${sizeKb} KB)` : ""} into session context${focus ? ` with focus '${focus}'` : ""}...`,
        { level: "info" }
    )

    try {
        await session.send({
            prompt,
            attachments: [
                {
                    type: "file",
                    path: docPath,
                    displayName: FORGE_DOC_REL,
                },
            ],
        })
    } catch (error) {
        await session.log(`forge: failed to load context: ${error.message}`, { level: "error" })
    }
}

// Walk up from cwd looking for docs/forge_implementation.md so /forge works from
// any sub-directory of the workflow repo (e.g. src/forge/Forge.Api).
async function resolveForgeDoc(startDir) {
    let dir = path.resolve(startDir)
    const root = path.parse(dir).root

    while (true) {
        const candidate = path.join(dir, FORGE_DOC_REL)
        try {
            await access(candidate)
            return candidate
        } catch {
            // keep walking
        }

        if (dir === root) return null
        const parent = path.dirname(dir)
        if (parent === dir) return null
        dir = parent
    }
}
