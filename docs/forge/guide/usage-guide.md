# Forge — usage guide

Forge runs an AI agent swarm that takes a software story from architecture
to merged, tested code without a human in the loop, and gives the operator
one dashboard to set it up, watch it and audit it. This guide follows six
scenarios on the real **code-intelligence** product (one GitHub repo, nine
finished stories, about 480 workstreams). Every screenshot was captured
from a running Forge instance with Playwright; the operator identity is
redacted, everything else is live data.

> Companion material: [slide deck](forge-guide-slides.pptx), a
> [two-minute intro video](media/forge-intro.mp4) (story spawn → spec →
> implementation), the full [walkthrough video](media/forge-walkthrough.mp4)
> (about five minutes, captioned), and the [capture script](capture/capture.mjs) that
> regenerates every image. Reference docs: [setup](../setup.md),
> [providers](../providers.md), [keyboard](../keyboard.md),
> [FAQ](../faq.md), [architecture](../architecture.md),
> [scoring and promotion](../scoring-and-promotion.md).

## Contents

0. [Vocabulary and sign-in](#0-vocabulary-and-sign-in)
1. [Operator setup: role, providers, crews, models](#1-operator-setup-role-providers-crews-models)
2. [Product setup: repo, sync, build and test gate](#2-product-setup-repo-sync-build-and-test-gate)
3. [Work items: sync and live execution](#3-work-items-sync-and-live-execution)
4. [Stories: spawn, phases and workstreams](#4-stories-spawn-phases-and-workstreams)
5. [Guardrails: against hallucination and sloppiness](#5-guardrails-against-hallucination-and-sloppiness)
6. [Reports: cost and performance](#6-reports-cost-and-performance)
7. [When something looks wrong](#7-when-something-looks-wrong)
8. [Regenerating this guide](#8-regenerating-this-guide)

---

## 0. Vocabulary and sign-in

| Term | Meaning |
|------|---------|
| **Product** | A named group of repos. The switcher in the top bar scopes every view and every write to one product. |
| **Repo** | A repository attached to a product from Azure DevOps (ADO) or GitHub, with a local clone that agents work in. |
| **Crew** | A team of agents with a lead and a roster. Four exist: **Architecture**, **Engineering**, **Review** and **QA**. |
| **Pair** | Two agents on one task: a **generator** that produces the work and an **evaluator** that scores it. Generator and evaluator must run on different models. |
| **Iteration** | One generator turn followed by one evaluator verdict. A pair iterates until the score reaches 90 or a guardrail stops it. |
| **Score** | 0–100 from the evaluator: correctness 40, quality 25, tests 15, scope 10, risk 10. At 90 or more the deliverable passes, 70–89 iterates, below 70 is rejected. |
| **Story** | A feature-sized unit spawned from a Feature or PBI. It moves **plan → decompose → implement ⇄ review → QA → done**. |
| **Plan docs** | The four artifacts the Architecture crew writes per story: architecture, tech spec, implementation plan and e2e scenarios. |
| **Workstream** | One stage of the implementation plan, worked in its own git worktree and branch by one engineer pair, ending in a PR. Each has a sibling QA workstream for end-to-end tests. |
| **Gate** | The repo's restore, build and test commands. They run on the worktree before the evaluator scores, and a red gate cannot pass. |
| **Sloppy** | A guardrail verdict: the pair is not converging. The task is parked and handed to a fresh pair on different models. |
| **Product owner** | The role that may change crews, repos and stories on a product. Other roles read. |

Forge is two processes, the API on port 5180 and the UI on port 5181:

```pwsh
.\scripts\run-forge-ui.ps1          # start both
.\scripts\run-forge-ui.ps1 -Status  # what is running
.\scripts\run-forge-ui.ps1 -Stop    # stop both
```

![Sign-in gate](images/00-sign-in.png)

*Open `http://localhost:5181/`. On the host machine Forge signs you in with
the Windows account automatically. From elsewhere, expand **Use a pairing
code** and paste the 64-character code sent to your chat. Sessions last
eight hours.*

## 1. Operator setup: role, providers, crews, models

**Goal:** you are product owner on code-intelligence, both source
providers are connected, the four crews have rosters, and each crew has a
preferred generator and evaluator model.

### 1.1 Set your role on the product

![User menu with role selector](images/02-user-menu-role.png)

Click your avatar in the top bar. The menu shows your display name, the
role pill, **My role on code-intelligence** and both provider connections.
Pick **Product owner** in the role dropdown. The role is per product: a
role set while code-intelligence is active applies there only, and the
default role applies to any product without an override.

Product owner is the role that unlocks writes. Crew edits, repo edits,
spawning and pausing stories, and answering agent questions all check
for it on the active product; other roles see the same views read-only.
The API also refuses any write while the product switcher is on "all",
so pick a specific product before changing anything.

### 1.2 Connect the repository providers

![Settings, repository providers](images/03-settings-providers.png)

**Settings → Repository providers** holds one card per provider. Each
shows connected state, the organisation it is connected to, last sync,
error count since that sync and remaining rate-limit budget. Below that:
sign-in state and a **Sign out** button, then the default target the
product will import from (ADO organisation URL and project, GitHub owner)
and the preferred account. **Save default** stores them. The same
connections are also visible in the avatar menu with **Re-auth** buttons
for when a token expires. Token scopes are listed in
[providers.md](../providers.md).

### 1.3 Crews and rosters

![Crews roster](images/04-crews-roster.png)

**Crews** lists the four crews on the left with member count and today's
spend. The header for the selected crew shows its model, backend and
invoke mode as pills, its spend today against the daily budget, and seven
tabs. **Roster** lists members with their generator or evaluator role and
a remove button; the member catalog on the right lists every agent with a
`+` to add one. **Auto-assign unassigned** places agents that have no
crew.

### 1.4 Preferred models per crew

![Crews pair tab, generator and evaluator models](images/05-crews-pair-models.png)

**Pair** is where the models are chosen. The **generator model** writes
the spec or the diff; the **evaluator model** reviews before the tree
commits or the PR comes off draft. Every option shows its price per
million tokens and context window. The two must differ ("diff required"):
the server refuses to save an evaluator model equal to the generator
model, because the review pass exists to catch what the generator's model
missed. Below the pickers, **Roster pairs** shows how the roster pairs up
under these models.

![Crews runtime tab](images/06-crews-runtime.png)

**Runtime** sets the crew-wide model, the **backend** (Copilot SDK or
Claude) and the **invoke mode** (SDK streaming with tools, or CLI).
Switching the model writes a crew-history row and starts a new prompt
cache.

![Crews quota tab](images/07-crews-quota.png)

**Quota** bounds the crew: max concurrent tasks, weekly task cap, daily
budget in USD (new invocations are refused beyond it) and max budget per
task (the runner refuses to start beyond it).

![Crews capabilities tab](images/08-crews-capabilities.png)

**Capabilities** lists the tools and domain skills the crew is allowed to
use and lets you add from the catalogue.

### 1.5 Individual agents and their system prompt

![Agent edit modal with soul.md](images/09-agent-edit-soul.png)

Click an avatar on the roster to edit one agent. The modal shows name and
role, **runtime overrides** (backend, invoke mode, generator and evaluator
model, each defaulting to "inherit from crew"), the capability list, and
the agent's **soul.md**. The soul is the agent's system prompt: front
matter with name, character, role and tools, then tone and temperament.
Section 5 shows it appearing verbatim as the system prompt of every
iteration.

## 2. Product setup: repo, sync, build and test gate

**Goal:** a product named code-intelligence with its GitHub repo attached,
cloned locally, syncing work items and PRs, and a build + test gate that
every workstream must pass.

### 2.1 Products page

![Switching product](media/switch-product.gif)

*The top-bar switcher scopes every view to one product; **Activate** on
the products page makes it the default.*

![Products and repos](images/10-products.png)

**Products & repos** shows one card per product with repo, workstream and
PR counts. Under each card the attached repos show their provider badge,
last sync, a **gated** badge when build and test commands are configured,
and four actions: **History**, **Sync**, **Edit**, **Detach**. **Activate**
makes a product the one the sidebar scopes to. The **Current sprint**
block binds the product to an iteration and area paths via **Manage
sprints**.

![New product dialog](images/11-new-product.png)

*New product: a name, a slug (used in API paths and as the SQLite file
name) and a description.*

![Attach repository dialog](images/12-attach-repo.png)

*Attach repo: pick from the repos the provider account can see, ADO or
GitHub, or paste a clone URL. **Import from ADO** attaches several at
once.*

### 2.2 Repo settings: local clone and sync

![Edit repo, general tab](images/13-edit-repo-general.png)

**Edit** on the repo opens three tabs. **General** holds the display name,
the **local repo path** (here `E:\forge\code-intelligence`, cloned
automatically when missing) and the default branch.

![Edit repo, sync tab](images/14-edit-repo-sync.png)

**Sync** holds area paths (ADO only), the **agent-ready default team**
that receives items tagged `agent-ready`, and the sync cadence for work
items, pull requests and builds. A background sync ticks on that cadence
and webhooks short-circuit it; **Sync** on the products page or **Sync
now** on Work items forces a tick.

### 2.3 The build and test gate

![Edit repo, validation tab](images/15-edit-repo-validation-gate.png)

**Validation** is the gate. Four commands run in the workstream's
worktree, in order: **Init** (optional), **Restore** (`go mod download`),
**Build** (`go build ./...`) and **Test** (`go test ./...`), each with a
timeout. The **Run build/test as Administrator** switch is for repos whose
tests write machine state. Below the commands:

![Edit repo, validation tab, test options](images/16-edit-repo-validation-tests.png)

- **Impacted-test mode** narrows `dotnet test` to projects touched since
  the workstream's base branch; **Preview impacted** shows what would run.
- **E2E test pattern** decides how QA workstreams write acceptance tests:
  BDD (a `.feature` file plus step bindings for the repo's language) or
  plain test methods in the existing framework.

![Opening the repo gate settings](media/repo-gate.gif)

Section 3 shows the gate running on a live workstream.

![Build and sync history](images/17-build-history.png)

*History on the repo row expands the build and sync history for that
repo.*

## 3. Work items: sync and live execution

**Goal:** see the synced work items for the product and follow one of
them while an engineer pair works it.

### 3.1 The work registry

![Work items board](images/20-work-items-board.png)

**Work items** holds every synced item for the product in five layouts:
**Board** by state, **Exec** by agent status, **Timeline** by iteration
span, **Tree** by parent → child and **List** as a table. Filters cover
type, state, iteration, assignee, repo and story. **Sync now** pulls from
the provider immediately; **New item** and **New story** create work.

![Work items tree](images/21-work-items-tree.png)

*Tree: a Feature such as `code-intelligence:3D-RENDER` expands into its
workstream steps, each with provider state, execution status and points.*

![Work items exec layout](images/22-work-items-exec.png)

*Exec: per story, items grouped by what the agents are doing: idle,
queued, live, completed, failed, cancelled.*

### 3.2 Live execution

Click into an item, or open `/work-items/<id>/live`, to watch its
execution.

![Live execution walkthrough](media/live-execution.gif)

![Live execution](images/23-live-execution.png)

The header names the work item, the **pair** (Cheng Yu on claude-opus-5
generating, Wang Jing on gpt-5.6-sol evaluating), the iteration counter
against its cap, the status, and the controls **Resume**, **Restart**,
**Re-queue** and **Unassign**. **Generator Prompts** expands to the exact
prompts of the current iteration (section 5). The transcript below can be
filtered to **Generator**, **Evaluator**, **Tools**, **Worktree**,
**Build**, **Review**, **Prompt**, **Operator** or **Summary**; the
iteration pager above it shows every iteration with its score. The right
pane has **Worktree**, **Tokens**, **Progress**, **Comments** and
**Builds**.

![Live execution, summary tab](images/24-live-summary.png)

*Summary: per iteration, the generator's own summary, how it resolved the
prior feedback item by item, and what it verified. Step back with the
pager to see iteration 1 at 53 climb to 91 at iteration 12.*

![Live execution, build gate](images/25-live-build-gate.png)

*Build: the repo gate from section 2 ran in the worktree. Both the build
gate and the test gate passed with their durations; **view log** opens the
captured output.*

![Live execution, review](images/26-live-review.png)

*Review: the Review crew's pass on the PR and the generator's review-fix
turn, with the reviewer's reasoning and the fix it chose.*

![Live execution, progress](images/27-live-progress-verdicts.png)

*Progress: the evaluator's verdict for every iteration, with what
improved, what still needs work and why it scored as it did.*

![Live execution, tokens](images/28-live-tokens.png)

*Tokens: input and output tokens and cost per role, the total against the
crew's daily budget, and prompt size across iterations.*

## 4. Stories: spawn, phases and workstreams

**Goal:** spawn a story and follow it through Design & Plan, Impl ⇄
Review and Release.

### 4.1 The story index

![Workstreams index](images/30-workstreams-index.png)

**Workstreams** counts stories by stage in the header and lists one row
per story: workstream totals, repo, start time, duration, target branch,
and a strip with one square per workstream (green complete). On the right
the stage badge, phase progress bar, score and the pause, delete and chat
controls. Stage chips filter by Plan, Decompose, Implement, Review, QA
and Done.

### 4.2 Spawning a story

![Spawn story dialog](media/spawn-story.gif)

![Spawn story, pick](images/31-spawn-story-pick.png)

**+ Spawn story** opens a two-step dialog. Step one lists the product's
Features and PBIs with id, state and points; **Pick →** selects one.

![Spawn story, confirm scope](images/32-spawn-story-scope.png)

Step two confirms the scope: story id, title, points, the **architect
prompt** (the item's description, which becomes the Architecture crew's
user prompt), the bound repo, the **base branch** that every PR of the
spawn targets, **auto-merge** after review passes, and optional reference
documents.

![Spawn story, spawn button](images/33-spawn-story-scope-2.png)

**Spawn** creates the story branch and worktree and queues the plan phase.
Nothing else is needed from the operator unless an agent raises a
question or a guardrail parks a task.

### 4.3 Design & Plan

![Switching story modes](media/story-modes.gif)

![Story in Design & Plan mode](images/34-story-design-plan.png)

The story page has a **Mode** switch: **Design & Plan**, **Impl ⇄
Review**, **Release**. In Design & Plan the left rail lists the four plan
docs, each worked by an Architecture pair with its iteration count, score
and cost, then the **decomposition** that turns the implementation plan
into workstreams. The right panel has **Overview** (the pair, cost,
tokens, iterations, and the prompts), **Document**, **Conversation**,
**PR**, **Worktree** and **Questions**.

![Architecture document rendered](images/35-story-design-document.png)

*Document: the rendered architecture.md with a table of contents, its
PASS badge, iteration, score and cost. Each plan doc is merged through its
own PR onto the base branch.*

![Open questions](images/36-story-design-questions.png)

*Questions: structured open questions the agents raised and the answers
given. An unanswered question is a hard gate for the evaluator.*

### 4.4 Impl ⇄ Review

![Story in Impl mode](images/37-story-impl-pairs.png)

In Impl ⇄ Review the overview lists one **engineer pair** per workstream
with hours, iterations, best score and cost, and the merged
**implementation and review PRs** on the right. Open the **phase board**
on the left to select a workstream.

![Workstream overview](images/38-story-workstream-overview.png)

*A selected workstream. The card shows its chain of pairs (this one went
through three pairs before passing), iteration, score, cost, build and
test gate ticks, question count, branch and PR. The e2e sibling below
depends on it. The detail tabs are **Overview**, **Conversation**, **PR**,
**Worktree**, **Questions**, **QA / E2E** and **Trace**.*

![Workstream KPIs](images/39-story-workstream-kpis.png)

*Conversation: one column per iteration with score, input and output
tokens, thinking tokens, cost, files changed, duration, context usage and
the pair. The ⇄ marker shows where a fresh pair took over.*

![Workstream PR with review comments](images/40-story-workstream-pr-review.png)

*PR: the implementation PR with files, commits, diff size and the Review
crew's comments, each resolved with a reply before merge.*

### 4.5 Release

![Story in Release mode](images/41-story-release.png)

Release lists the **QA pairs**, one per stage, and their **e2e PRs**. QA
writes the acceptance tests defined in the e2e scenarios doc for that
stage; a phase closes only when they pass.

![Story trace](images/42-story-trace.png)

*Trace: every workstream run in the story with time, status, verdict and
score.*

![Pull requests, provider lifecycle](images/44-prs-lifecycle.png)

*Pull Requests with the **ADO lifecycle** toggle shows the same PRs in the
provider's states. Auto-merge is armed by the spawn setting.*

## 5. Guardrails: against hallucination and sloppiness

**Goal:** understand what stops an agent from claiming work it did not
do, from looping without progress, and from drowning in its own context.

### 5.1 Every prompt is persisted and auditable

![Opening the prompt audit](media/prompt-audit.gif)

![Prompt audit](images/50-prompt-audit.png)

**Conversation → Prompt** on a workstream shows, for every iteration, the
exact **system prompt** and **user prompt** each role received, with the
model, backend and agent. Persisted rows come from the iteration-prompt
audit table; live rows come from the stream. This is the answer to "what
did the agent actually see".

![System prompt is the soul](images/51-prompt-system-soul.png)

The **system prompt** is the agent's soul.md from section 1.5 and nothing
else. All task context goes in the user prompt, so the system prompt stays
small and cacheable.

### 5.2 The user prompt manages context deliberately

The generator's user prompt for an engineering workstream is assembled in
a fixed order. From a real iteration-12 prompt on code-intelligence:

1. **Notes from prior iterations**, newest first. Each iteration must end
   by writing `.forge/iter-notes.md` (files touched, decisions, dead ends,
   open questions, what is left). Forge archives it and prepends the
   history, capped at about 16 KB, so a fresh session inherits its own
   working state instead of re-discovering the repo.
2. **Operator answers** to open questions.
3. **Latest evaluator feedback** and the iteration history.
4. **Story, description, workstream, detailed requirements, story
   attachments, target files, repository context.**
5. **Hard rules**: do not delete production code; edit existing files in
   place; write large files in chunks; **verify your work compiles before
   claiming DONE**; do not run git yourself; keep working notes; how to
   handle prior feedback; report open questions in a structured form.

![Evaluator prompt with ground truth](images/52-prompt-user-context.png)

The evaluator's prompt starts with **ground truth**: the actual `git
status` and commits on the branch versus the PR base, and the captured
build and test output. The evaluator scores what is on disk, not what the
generator says.

![Evaluator prompt, failing tests](images/53-prompt-user-rules.png)

*Real failing test output is pasted into the evaluator's prompt. A diff
with a red gate cannot pass regardless of what the generator claims.*

![Evaluator feedback carried into the generator prompt](images/54-prompt-evaluator-feedback.png)

*On a plan doc, iteration 18 of architecture.md: the generator prompt
opens with **evaluator feedback, strict per-item attention required**,
and the evaluator prompt instructs it not to trust the generator's "fixed"
claims but to verify each one with `grep -nF` against the file.*

### 5.3 Convergence guardrails

The deliverable loop checks every scored iteration and parks the task as
**sloppy** when any of these fire:

| Trigger | Rule |
|---------|------|
| Score plateau | From iteration 4 on, no new personal best in the last three iterations. |
| Token cap | The generator has used 500K input + output tokens since the budget window opened. |
| Iteration cap | The per-task iteration limit, derived from the item's points, is hit without a pass. |
| Consecutive zero | Three scores of 0 in a row. |
| No progress at zero | A score of 0 with no files added, modified or deleted. |
| Agent refusal | The generator answered DONE with zero tool calls. |
| Orchestrator no-progress | Five consecutive re-spawns produced no new deliverable. |

A parked task goes to the reassignment reconciler, which hands it to a
**fresh pair on different models**. After three pairs it is a terminal
failure with the advice to refactor and break down the scope.

![Sloppy-parked workstream](images/55-sloppy-parked.png)

*A workstream that hit the orchestrator no-progress rule: `sloppy-parked`
with the reason and prior pair, then `sloppy-terminal-failure` after three
reassignments.*

![Evaluator skipped after generator failure](images/56-sloppy-progress.png)

*When the generator fails outright the evaluator is skipped, so tokens are
not spent scoring broken output.*

### 5.4 Context budget

![Prompt size across iterations](images/57-prompt-size-trend.png)

The **Tokens** tab charts prompt size per iteration and the transcript
logs a `prompt-growth` event with the delta against the threshold. Agent
memory compacts a role's conversation when it reaches the configured
share of the model's context window (300K input tokens by default, capped
at half the window), and the context-usage figure in the KPI strip shows
how close each iteration came.

### 5.5 Policies on top

![Policies](images/58-policies.png)

**Policies** add human-facing guardrails: drag rules on the board, human
gates that hold PRs touching sensitive paths, and branch protection with
required checks and one approving review. Auto-merge never bypasses a
gate.

## 6. Reports: cost and performance

**Goal:** know what the swarm costs and how each agent performs.

### 6.1 Cost

![Costs](images/60-cost.png)

**Costs** shows today's spend against the daily cap, the past seven days,
burn rate and a seven-day forecast, spend by epic, per-agent burn, alerts
and the most expensive cards. Role chips filter to a crew; **Export CSV**
and **Adjust caps** are on the right. The code-intelligence stories
finished in June, so its rolling window reads zero here; the cost of each
finished piece is on the story pages: per pair in the story overview
(section 4.4), per iteration in the Conversation KPI strip (section 4.4)
and per role in the Tokens tab (section 3.2).

### 6.2 Performance

![Performance, promotion ladder](images/61-performance-ladder.png)

**Performance** opens with payroll and token spend today, the roster
average score and how many agents are ready for promotion. The
**promotion ladder** places every agent from L0 trainee to L10 fellow by
average evaluator score; L7 is the floor for a crew lead. Monthly
snapshots lock a score per month. The **product filter** narrows the
scoreboard to code-intelligence.

![Performance, roster scorecard](images/62-performance-scorecard.png)

*The roster scorecard: average, pass rate, throughput, iterations per
pass, rejects, cost per pass and trailing score. Click a row to inspect
the agent.*

![Crew performance](images/63-crews-performance.png)

**Crews → Performance** shows the crew's shift schedule, payroll versus
token spend for today, sprint and month, and each member's level.

![Crew history](images/64-crews-history.png)

**History** is the crew's change log: model switches, roster changes,
quota edits.

![Swarm](images/65-swarm.png)

**Swarm** is the live view: who is working, queued or idle right now, and
spend in the last 24 hours against the cap.

![Audit](images/66-audit.png)

**Audit** records every write through the API with user, method, path,
status, idempotency key and body snippet.

## 7. When something looks wrong

| Symptom | What to check |
|---------|---------------|
| Every edit button is disabled | Your role on the active product is not product owner. Change it in the avatar menu. |
| Writes fail with "select a specific product" | The product switcher is on all products. Pick one. |
| No work items or PRs | Settings → Repository providers must show connected with a sane error count; the repo's sync cadence must be set; press Sync now. |
| Gate never passes | Edit repo → Validation: run the build and test commands by hand in the local repo path; check the timeout. |
| A workstream shows `sloppy-parked` | Read the reason in the transcript. Three pair failures means the step is too big: split it in the implementation plan and re-queue. |
| Evaluator keeps saying "iterate" | Open Progress for the "still needs improvement" list, then Prompt to confirm the generator saw it. Answer any open question. |
| Automatic sign-in unavailable | You are not on the host, or the API is down. Use a pairing code or `run-forge-ui.ps1 -Status`. |
| Spend today is zero on a busy product | The cost page is a rolling window; use the story pages for finished work. |

## 8. Regenerating this guide

All images, GIFs and the video come from
[`capture/capture.mjs`](capture/capture.mjs), a Playwright script that
runs against a live Forge on the host machine. It signs in with the
Windows account, scopes the UI to the product, navigates, opens dialogs
and cancels them, and never mutates data. The operator e-mail is replaced
in-page before every capture.

```pwsh
# Prerequisites: Forge running, `npm ci` done in src/forge/Forge.UI,
# and a full ffmpeg build for MP4 / GIF encoding (for example the
# ffmpeg-static npm package, or ffmpeg on PATH).
$env:FORGE_FFMPEG = "C:\path\to\ffmpeg.exe"
node docs/forge/guide/capture/capture.mjs            # everything
node docs/forge/guide/capture/capture.mjs shots      # screenshots only
node docs/forge/guide/capture/capture.mjs gifs video # animations only
node docs/forge/guide/capture/capture.mjs intro      # 2-minute intro video

# Rebuild the slide deck from the screenshots (needs pptxgenjs on NODE_PATH)
node docs/forge/guide/capture/build-slides.cjs
```

`FORGE_PRODUCT` selects another product, `FORGE_UI_URL` another port,
`FORGE_CHROME` a specific Chromium build, and `FORGE_REDACT=0` keeps the
operator identity visible. The story and workstream ids used for the
detail views are constants at the top of the script.
