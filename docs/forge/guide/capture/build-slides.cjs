// -----------------------------------------------------------------------
// Builds docs/forge/guide/forge-guide-slides.pptx from the captured
// screenshots (six operator scenarios on code-intelligence). Run after
// capture.mjs:
//
//   node docs/forge/guide/capture/build-slides.cjs
//
// Needs `pptxgenjs` resolvable from the current directory or NODE_PATH.
// -----------------------------------------------------------------------

const path = require('path');
const pptxgen = require('pptxgenjs');

const guideDir = path.resolve(__dirname, '..');
const img = (n) => path.join(guideDir, 'images', n);
const out = path.join(guideDir, 'forge-guide-slides.pptx');

// Forge palette (dark UI): near-black canvas, amber accent, soft grey text.
const C = { bg: '0F1115', panel: '171A20', line: '2A2E36', amber: 'F6AB40', text: 'E6E7E8', muted: 'A7ADB7', green: '5FD38D', red: 'FF8A80' };
const FONT = 'Calibri';

const pres = new pptxgen();
pres.layout = 'LAYOUT_WIDE'; // 13.33 x 7.5 in
pres.author = 'Forge';
pres.title = 'Forge — operator guide';

const W = 13.33, H = 7.5, M = 0.6;

function base(title, eyebrow) {
  const s = pres.addSlide();
  s.background = { color: C.bg };
  if (eyebrow) s.addText(eyebrow.toUpperCase(), { x: M, y: 0.32, w: 9, h: 0.3, fontFace: 'Courier New', fontSize: 11, color: C.amber, charSpacing: 3, isTextBox: true, margin: 0 });
  s.addText([{ text: '◆ ', options: { color: C.amber } }, { text: title, options: { color: C.text } }],
    { x: M, y: 0.58, w: W - 2 * M, h: 0.8, fontFace: FONT, fontSize: 30, bold: true, isTextBox: true, margin: 0 });
  s.addText('Forge · operator guide · code-intelligence', { x: M, y: H - 0.5, w: 5, h: 0.3, fontFace: FONT, fontSize: 10, color: C.muted, isTextBox: true, margin: 0 });
  s.addText(String(pres.slides.length), { x: W - M - 1, y: H - 0.5, w: 1, h: 0.3, fontFace: FONT, fontSize: 10, color: C.muted, align: 'right', isTextBox: true, margin: 0 });
  return s;
}

function shot(s, file, x, y, w, aspect = 1440 / 900) {
  const h = w / aspect;
  s.addShape(pres.ShapeType.rect, { x: x - 0.04, y: y - 0.04, w: w + 0.08, h: h + 0.08, fill: { color: C.line }, line: { color: C.line, width: 0 } });
  s.addImage({ path: img(file), x, y, w, h });
  return h;
}

function bullets(s, items, x, y, w, h, size = 13) {
  s.addText(items.map((t, i) => ({ text: t, options: { bullet: { code: '25C6' }, breakLine: i < items.length - 1, paraSpaceAfter: 6 } })),
    { x, y, w, h, fontFace: FONT, fontSize: size, color: C.text, valign: 'top', isTextBox: true, margin: 0 });
}

function card(s, x, y, w, h, head, body, accent = C.amber) {
  s.addShape(pres.ShapeType.roundRect, { x, y, w, h, rectRadius: 0.08, fill: { color: C.panel }, line: { color: C.line, width: 0.75 } });
  s.addText(head, { x: x + 0.2, y: y + 0.15, w: w - 0.4, h: 0.4, fontFace: FONT, fontSize: 15, bold: true, color: accent, isTextBox: true, margin: 0 });
  s.addText(body, { x: x + 0.2, y: y + 0.55, w: w - 0.4, h: h - 0.7, fontFace: FONT, fontSize: 12, color: C.text, valign: 'top', isTextBox: true, margin: 0 });
}

// Screenshot left, explanation right.
function viewSlide(eyebrow, title, file, items, note) {
  const s = base(title, eyebrow);
  const w = 7.9;
  shot(s, file, M, 1.55, w);
  bullets(s, items, M + w + 0.4, 1.6, W - M - (M + w + 0.4), 4.9, 13);
  if (note) s.addText(note, { x: M, y: 6.6, w: W - 2 * M, h: 0.35, fontFace: FONT, fontSize: 11, italic: true, color: C.muted, isTextBox: true, margin: 0 });
  return s;
}

// Two screenshots side by side with a caption strip.
function pairSlide(eyebrow, title, left, right, items) {
  const s = base(title, eyebrow);
  shot(s, left, M, 1.55, 5.95);
  shot(s, right, M + 6.2, 1.55, 5.95);
  bullets(s, items, M, 5.5, W - 2 * M, 1.4, 12);
  return s;
}

// Scenario divider.
function divider(num, title, goal) {
  const s = pres.addSlide();
  s.background = { color: C.bg };
  s.addText(`SCENARIO ${num}`, { x: M, y: 2.4, w: 8, h: 0.4, fontFace: 'Courier New', fontSize: 14, color: C.amber, charSpacing: 6, isTextBox: true, margin: 0 });
  s.addText(title, { x: M, y: 2.9, w: 11, h: 1.0, fontFace: FONT, fontSize: 40, bold: true, color: C.text, isTextBox: true, margin: 0 });
  s.addText(goal, { x: M, y: 4.0, w: 10.5, h: 1.2, fontFace: FONT, fontSize: 16, color: C.muted, isTextBox: true, margin: 0 });
  return s;
}

// ── Title ────────────────────────────────────────────────────────────────
{
  const s = pres.addSlide();
  s.background = { color: C.bg };
  s.addImage({ path: img('37-story-impl-pairs.png'), x: 6.4, y: 0, w: 6.93, h: 7.5, sizing: { type: 'cover', w: 6.93, h: 7.5 }, transparency: 15 });
  s.addText('FORGE', { x: M, y: 2.0, w: 5.5, h: 0.5, fontFace: 'Courier New', fontSize: 16, color: C.amber, charSpacing: 8, isTextBox: true, margin: 0 });
  s.addText('Operator guide', { x: M, y: 2.5, w: 5.6, h: 1.2, fontFace: FONT, fontSize: 48, bold: true, color: C.text, isTextBox: true, margin: 0 });
  s.addText('Six scenarios on the code-intelligence product: setup, gates, live execution, stories, guardrails, reports.',
    { x: M, y: 3.75, w: 5.4, h: 1.3, fontFace: FONT, fontSize: 16, color: C.muted, isTextBox: true, margin: 0 });
  s.addText('docs/forge/guide · usage-guide.md · forge-walkthrough.mp4', { x: M, y: 6.6, w: 5.6, h: 0.3, fontFace: 'Courier New', fontSize: 10, color: C.muted, isTextBox: true, margin: 0 });
}

// ── What Forge is ────────────────────────────────────────────────────────
{
  const s = base('What Forge is', 'The idea');
  const stats = [['4', 'crews', 'Architecture · Engineering · Review · QA'], ['2', 'agents per task', 'a generator writes, an evaluator scores — on different models'], ['90', 'passing score', 'below 90 the pair iterates; below 70 it is rejected'], ['3', 'pair attempts', 'a parked task is handed to a fresh pair; then it fails for good']];
  stats.forEach(([n, l, d], i) => {
    const x = M + i * 3.05, y = 1.7;
    s.addShape(pres.ShapeType.roundRect, { x, y, w: 2.85, h: 2.5, rectRadius: 0.08, fill: { color: C.panel }, line: { color: C.line, width: 0.75 } });
    s.addText(n, { x: x + 0.2, y: y + 0.2, w: 2.4, h: 1.0, fontFace: FONT, fontSize: 60, bold: true, color: C.amber, isTextBox: true, margin: 0 });
    s.addText(l, { x: x + 0.2, y: y + 1.2, w: 2.4, h: 0.4, fontFace: FONT, fontSize: 15, bold: true, color: C.text, isTextBox: true, margin: 0 });
    s.addText(d, { x: x + 0.2, y: y + 1.6, w: 2.4, h: 0.8, fontFace: FONT, fontSize: 11, color: C.muted, valign: 'top', isTextBox: true, margin: 0 });
  });
  bullets(s, [
    'A story is spawned from a Feature or PBI. Architects write four plan docs; the plan is decomposed into workstreams; engineer pairs implement each in its own worktree and PR; reviewers critique; QA adds end-to-end tests.',
    'Every iteration runs the repo\'s build and test gate before the evaluator scores, and every prompt is persisted for audit.',
    'code-intelligence, the product in this guide: one GitHub repo, nine finished stories, about 480 workstreams, all merged.',
  ], M, 4.55, W - 2 * M, 2.2, 14);
}

// ── Lifecycle ────────────────────────────────────────────────────────────
{
  const s = base('How a story moves', 'Lifecycle');
  const steps = [['Plan', 'Architecture pairs write architecture, tech spec, implementation plan and e2e scenarios; each merges as a PR.'], ['Decompose', 'The plan becomes workstreams: one per stage, each PR-sized, with dependencies.'], ['Implement', 'Engineer pairs work unblocked workstreams in parallel, each in a git worktree.'], ['Review', 'Review crew critiques the PR; the generator fixes; the evaluator re-scores.'], ['QA', 'QA pairs add e2e tests per stage; the gate must be green.'], ['Done', 'PRs merged onto the base branch, worktrees closed, items closed.']];
  const w = 1.95, gap = 0.12, y = 1.9;
  steps.forEach(([h, d], i) => {
    const x = M + i * (w + gap);
    s.addShape(pres.ShapeType.roundRect, { x, y, w, h: 2.5, rectRadius: 0.08, fill: { color: i === 5 ? '173322' : C.panel }, line: { color: i === 5 ? C.green : C.line, width: 0.75 } });
    s.addText(`0${i + 1}`, { x: x + 0.18, y: y + 0.15, w: 1, h: 0.35, fontFace: 'Courier New', fontSize: 11, color: C.amber, isTextBox: true, margin: 0 });
    s.addText(h, { x: x + 0.18, y: y + 0.5, w: w - 0.3, h: 0.45, fontFace: FONT, fontSize: 18, bold: true, color: C.text, isTextBox: true, margin: 0 });
    s.addText(d, { x: x + 0.18, y: y + 1.0, w: w - 0.32, h: 1.4, fontFace: FONT, fontSize: 11, color: C.muted, valign: 'top', isTextBox: true, margin: 0 });
  });
  s.addShape(pres.ShapeType.line, { x: M + 2 * (w + gap), y: 4.6, w: 3 * (w + gap) - gap, h: 0, line: { color: C.amber, width: 1, dashType: 'dash' } });
  s.addText('Implement ⇄ Review ⇄ QA loop until the stage scores ≥ 90', { x: M + 2 * (w + gap), y: 4.65, w: 3 * (w + gap) - gap, h: 0.4, fontFace: FONT, fontSize: 12, italic: true, color: C.amber, align: 'center', isTextBox: true, margin: 0 });
  s.addText('The story page follows the same shape with its Mode switch: Design & Plan · Impl ⇄ Review · Release.',
    { x: M, y: 5.5, w: W - 2 * M, h: 0.6, fontFace: FONT, fontSize: 13, color: C.text, isTextBox: true, margin: 0 });
}

// ── Scenario 1 ───────────────────────────────────────────────────────────
divider(1, 'Operator setup', 'Become product owner on code-intelligence, connect ADO and GitHub, check the four crews, and set each crew\'s generator and evaluator model.');
pairSlide('1 · Operator setup', 'Role and provider connections', '02-user-menu-role.png', '03-settings-providers.png', [
  'Avatar menu: pick Product owner under "My role on code-intelligence". The role is per product and unlocks crew, repo and story writes; other roles read.',
  'Settings → Repository providers: ADO and GitHub cards with connected state, last sync, error count, rate-limit budget, default org / project / owner and preferred account.',
]);
pairSlide('1 · Operator setup', 'Crews and preferred models', '04-crews-roster.png', '05-crews-pair-models.png', [
  'Crews: Architecture, Engineering, Review, QA with rosters; add from the member catalog; Auto-assign unassigned.',
  'Pair: generator model and evaluator model per crew, with price and context window. They must differ — the server refuses a matching pair.',
]);
pairSlide('1 · Operator setup', 'Runtime, quota and the agent soul', '06-crews-runtime.png', '09-agent-edit-soul.png', [
  'Runtime: crew model, backend (Copilot SDK / Claude) and invoke mode. Quota: concurrency, weekly cap, daily and per-task budgets.',
  'Agent edit: runtime overrides that default to "inherit from crew", capabilities, and soul.md — the system prompt every iteration starts with.',
]);

// ── Scenario 2 ───────────────────────────────────────────────────────────
divider(2, 'Product setup', 'Create the product, attach the GitHub repo, set the local clone and sync cadence, and define the build + test gate.');
pairSlide('2 · Product setup', 'Products, repos, attach and sync', '10-products.png', '12-attach-repo.png', [
  'Products & repos: one card per product; repo rows show provider, last sync, a "gated" badge, and History · Sync · Edit · Detach. Activate scopes the sidebar.',
  'Attach repo: pick from the provider account or paste a clone URL; Import from ADO attaches several. Sync tab: area paths, agent-ready team, cadence for items, PRs, builds.',
]);
viewSlide('2 · Product setup', 'The build + test gate', '15-edit-repo-validation-gate.png', [
  'Edit repo → Validation: Init, Restore (go mod download), Build (go build ./...), Test (go test ./...) with timeouts.',
  'Runs in the workstream worktree before every evaluator verdict; a red gate cannot pass.',
  'Run elevated for tests that write machine state.',
  'Impacted-test mode narrows the test run to touched projects; Preview impacted shows what would run.',
  'E2E pattern: BDD (.feature + step bindings) or plain test methods.',
]);

// ── Scenario 3 ───────────────────────────────────────────────────────────
divider(3, 'Work items and live execution', 'See the synced registry, then follow one workstream while its pair iterates through gates, reviews and verdicts.');
pairSlide('3 · Work items', 'The registry, five ways', '21-work-items-tree.png', '22-work-items-exec.png', [
  'Board · Exec · Timeline · Tree · List. Filters: type, state, iteration, assignee, repo, story. Sync now pulls from the provider immediately.',
  'Tree: a Feature expands into its workstream steps with state, execution status and points. Exec: idle / queued / live / completed / failed / cancelled per story.',
]);
viewSlide('3 · Live execution', 'Watching a pair work', '23-live-execution.png', [
  'Header: the pair (Cheng Yu on claude-opus-5 ⇄ Wang Jing on gpt-5.6-sol), iteration 12 of 50, status, Resume · Restart · Re-queue · Unassign.',
  'Generator Prompts expands the exact prompts of the iteration.',
  'Transcript filters: Generator, Evaluator, Tools, Worktree, Build, Review, Prompt, Operator, Summary.',
  'Iteration pager with the score of every iteration: 53 → 86 → 91.',
  'Right pane: Worktree, Tokens, Progress, Comments, Builds.',
]);
pairSlide('3 · Live execution', 'Gate, summary and verdicts', '25-live-build-gate.png', '27-live-progress-verdicts.png', [
  'Build: the repo gate ran in the worktree — build gate and test gate passed with durations and logs.',
  'Progress: the evaluator\'s verdict per iteration with what improved, what still needs work and why it scored so. Summary: the generator\'s own account and its prior-feedback resolution.',
]);

// ── Scenario 4 ───────────────────────────────────────────────────────────
divider(4, 'Stories', 'Spawn a story, then follow it through Design & Plan, Impl ⇄ Review and Release, down to one workstream and its PR.');
pairSlide('4 · Stories', 'Spawn a story', '31-spawn-story-pick.png', '32-spawn-story-scope.png', [
  'Step 1: pick a Feature or PBI of the product. Step 2: confirm story id, title, points, the architect prompt, bound repo, base branch and auto-merge, then Spawn.',
  'Branches and worktrees are derived automatically; the Architecture crew starts on the plan branch with the description as its user prompt.',
]);
pairSlide('4 · Stories', 'Design & Plan', '34-story-design-plan.png', '35-story-design-document.png', [
  'Four plan docs, each by an Architecture pair with iterations, score and cost; then the decomposition into workstreams. Each doc is merged through its own PR.',
  'Document tab renders the doc with its PASS badge; Questions holds the structured open questions and their answers.',
]);
pairSlide('4 · Stories', 'Impl ⇄ Review', '37-story-impl-pairs.png', '39-story-workstream-kpis.png', [
  'One engineer pair per workstream with hours, iterations, best score and cost; 36 merged implementation and review PRs on the right.',
  'Workstream conversation: per-iteration score, tokens, cost, files, duration, context usage and pair; ⇄ marks where a fresh pair took over.',
]);
pairSlide('4 · Stories', 'Review PR and Release', '40-story-workstream-pr-review.png', '41-story-release.png', [
  'PR tab: files, commits, diff size and the Review crew\'s comments, each resolved before merge.',
  'Release: QA pairs per stage and their e2e PRs. Trace lists every workstream run with status, verdict and score.',
]);

// ── Scenario 5 ───────────────────────────────────────────────────────────
divider(5, 'Guardrails', 'What stops an agent from claiming work it did not do, looping without progress, or drowning in its own context — and how the system and user prompts are managed.');
pairSlide('5 · Guardrails', 'Every prompt is persisted and auditable', '50-prompt-audit.png', '51-prompt-system-soul.png', [
  'Conversation → Prompt: for every iteration the exact system prompt and user prompt per role, with model, backend and agent. Persisted rows come from the iteration-prompt audit table.',
  'The system prompt is the agent\'s soul.md and nothing else. All task context lives in the user prompt, so the system prompt stays small and cacheable.',
]);
{
  const s = base('The user prompt manages context deliberately', '5 · Guardrails');
  const blocks = [['1 · Notes from prior iterations', 'Each iteration ends by writing .forge/iter-notes.md: files touched, decisions, dead ends, open questions, what is left. Forge archives and prepends the history (newest first, ~16 KB cap).'], ['2 · Operator answers', 'Answers to the structured open questions the agent raised.'], ['3 · Evaluator feedback', 'The latest verdict and the iteration history, marked "strict per-item attention required".'], ['4 · Task context', 'Story, description, workstream, detailed requirements, attachments, target files, repository context.'], ['5 · Hard rules', 'Do not delete production code · edit in place · verify it compiles before DONE · do not run git · keep working notes · structured open questions.'], ['Evaluator side', 'Starts from ground truth: git status and commits vs the PR base plus captured build / test output. Must verify every "[x] FIXED" claim with grep -nF; does not trust the generator\'s resolution list.']];
  blocks.forEach(([h, d], i) => {
    const col = i % 3, row = Math.floor(i / 3);
    card(s, M + col * 4.1, 1.7 + row * 2.45, 3.9, 2.25, h, d, i === 5 ? C.green : C.amber);
  });
}
pairSlide('5 · Guardrails', 'Ground truth in the evaluator prompt', '52-prompt-user-context.png', '53-prompt-user-rules.png', [
  'The evaluator\'s prompt opens with the engineer\'s actual branch changes and the real test output, including failures, so it scores what is on disk.',
  'A diff with a red gate cannot pass regardless of what the generator claims.',
]);
{
  const s = base('Convergence guardrails: sloppy detection', '5 · Guardrails');
  const w = 6.7;
  shot(s, '55-sloppy-parked.png', M, 1.55, w);
  const rows = [['Score plateau', 'from iter 4, no new best in the last 3'], ['Token cap', '500K generator tokens in the budget window'], ['Iteration cap', 'per-task limit from points, no pass'], ['Consecutive zero', 'three scores of 0'], ['No progress at zero', 'score 0 and no files changed'], ['Agent refusal', 'DONE with zero tool calls'], ['Orchestrator no-progress', 'five re-spawns without a deliverable']];
  s.addTable([[{ text: 'Trigger', options: { bold: true, color: C.amber } }, { text: 'Rule', options: { bold: true, color: C.amber } }],
    ...rows.map(([a, b]) => [{ text: a, options: { color: C.text, bold: true } }, { text: b, options: { color: C.text } }])],
    { x: M + w + 0.4, y: 1.55, w: W - M - (M + w + 0.4), colW: [1.9, 3.53], fontFace: FONT, fontSize: 10.5, rowH: 0.36, border: { type: 'solid', color: C.line, pt: 0.5 }, fill: { color: C.panel }, margin: 0.05, valign: 'middle' });
  s.addText('A parked task goes to a fresh pair on different models. After three pairs it is a terminal failure: "consider refactor and break down the scope". When the generator fails outright the evaluator is skipped, so no tokens go to scoring broken output.',
    { x: M + w + 0.4, y: 4.6, w: W - M - (M + w + 0.4), h: 1.6, fontFace: FONT, fontSize: 12, color: C.text, valign: 'top', isTextBox: true, margin: 0 });
}
pairSlide('5 · Guardrails', 'Context budget and policies', '57-prompt-size-trend.png', '58-policies.png', [
  'Tokens tab charts prompt size per iteration; prompt-growth events log the delta against the threshold; agent memory compacts at the configured share of the model\'s context window.',
  'Policies add the human-facing layer: drag rules, human gates on sensitive paths, branch protection. Auto-merge never bypasses a gate.',
]);

// ── Scenario 6 ───────────────────────────────────────────────────────────
divider(6, 'Reports', 'Know what the swarm costs and how each agent performs, from the daily cap down to one iteration.');
pairSlide('6 · Reports', 'Cost', '60-cost.png', '28-live-tokens.png', [
  'Costs: today vs daily cap, past 7 days, burn rate, forecast, spend by epic, per-agent burn, alerts, most expensive cards; Export CSV; Adjust caps.',
  'Finished work keeps its cost on the story pages: per pair in the overview, per iteration in the KPI strip, per role in the Tokens tab.',
]);
pairSlide('6 · Reports', 'Performance', '61-performance-ladder.png', '62-performance-scorecard.png', [
  'Payroll and spend today, roster average, ready-for-promotion count; promotion ladder L0 → L10 by average evaluator score, lead floor L7; monthly snapshots; product filter.',
  'Roster scorecard: average, pass rate, throughput, iterations per pass, rejects, cost per pass. Crews → Performance adds payroll vs spend per crew; History is the change log.',
]);

// ── Troubleshooting ──────────────────────────────────────────────────────
{
  const s = base('When something looks wrong', 'Troubleshooting');
  const rows = [['Edit buttons disabled', 'Your role on the active product is not product owner. Change it in the avatar menu.'], ['"Select a specific product"', 'The switcher is on all products; writes need one product.'], ['No work items or PRs', 'Providers connected with a sane error count; sync cadence set; press Sync now.'], ['Gate never passes', 'Run the Validation commands by hand in the local repo path; check the timeout.'], ['Workstream sloppy-parked', 'Read the reason. Three pair failures means the step is too big: split it and re-queue.'], ['Evaluator keeps iterating', 'Progress shows the open items; Prompt confirms the generator saw them; answer open questions.'], ['Spend today is zero', 'Cost is a rolling window; finished work is costed on the story pages.']];
  s.addTable([[{ text: 'Symptom', options: { bold: true, color: C.amber } }, { text: 'What to check', options: { bold: true, color: C.amber } }],
    ...rows.map(([a, b]) => [{ text: a, options: { color: C.text, bold: true } }, { text: b, options: { color: C.text } }])],
    { x: M, y: 1.7, w: W - 2 * M, colW: [3.6, 8.53], fontFace: FONT, fontSize: 12, rowH: 0.55, border: { type: 'solid', color: C.line, pt: 0.5 }, fill: { color: C.panel }, margin: 0.08, valign: 'middle' });
}

// ── Closing ──────────────────────────────────────────────────────────────
{
  const s = pres.addSlide();
  s.background = { color: C.bg };
  s.addImage({ path: img('39-story-workstream-kpis.png'), x: 0, y: 0, w: 6.4, h: 7.5, sizing: { type: 'cover', w: 6.4, h: 7.5 }, transparency: 20 });
  s.addText('Where to go next', { x: 7.0, y: 1.8, w: 5.8, h: 0.9, fontFace: FONT, fontSize: 36, bold: true, color: C.text, isTextBox: true, margin: 0 });
  bullets(s, ['docs/forge/guide/usage-guide.md — the full scenario guide with animations',
    'docs/forge/guide/media/forge-walkthrough.mp4 — captioned tour of all six scenarios',
    'docs/forge/setup.md · providers.md · scoring-and-promotion.md — reference',
    'docs/forge/guide/capture/capture.mjs — regenerate every screenshot with Playwright'], 7.0, 2.9, 5.8, 3.2, 14);
  s.addText('FORGE', { x: 7.0, y: 6.4, w: 4, h: 0.4, fontFace: 'Courier New', fontSize: 12, color: C.amber, charSpacing: 8, isTextBox: true, margin: 0 });
}

pres.writeFile({ fileName: out }).then(() => console.log('wrote', out, `(${pres.slides.length} slides)`));
