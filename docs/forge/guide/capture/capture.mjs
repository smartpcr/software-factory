// -----------------------------------------------------------------------
// Forge guide capture — screenshots, GIF clips and a captioned walkthrough
// video for the six operator scenarios, driven by Playwright against the
// running Forge UI with the real `code-intelligence` product.
//
//   1. Operator: product-owner role, provider connections, crews, models
//   2. Product: repos, sync, build + test gate
//   3. Work items: sync, live execution
//   4. Story: spawn, phases (spec → impl → review → release), workstreams
//   5. Guardrails: sloppy detection, verified claims, prompt / context audit
//   6. Reports: cost, performance
//
// Usage (repo root; Forge.Api on :5180 and Forge.UI on :5181):
//
//   node docs/forge/guide/capture/capture.mjs            # everything
//   node docs/forge/guide/capture/capture.mjs shots      # screenshots only
//   node docs/forge/guide/capture/capture.mjs gifs       # GIF clips only
//   node docs/forge/guide/capture/capture.mjs video      # walkthrough video only
//   node docs/forge/guide/capture/capture.mjs intro      # 2-minute intro video (story spawn → spec → impl)
//
// Environment:
//   FORGE_UI_URL    base URL of the UI              (default http://localhost:5181)
//   FORGE_PRODUCT   product id to scope every view  (default code-intelligence)
//   FORGE_CHROME    path to a chrome.exe            (default: Playwright's bundled build)
//   FORGE_FFMPEG    path to a full ffmpeg build     (needed for .mp4 / .gif; otherwise
//                                                    the raw .webm recordings are kept)
//   FORGE_REDACT    "0" keeps the operator e-mail visible (default: replaced in-page)
//
// The browser signs in through the local Windows-account flow, so this must
// run on the machine hosting Forge.Api. Nothing here mutates Forge: only
// navigation, tabs, dialogs that are cancelled, and read-only filters.
// -----------------------------------------------------------------------

import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '../../../..');
const guideDir = path.resolve(here, '..');
const imagesDir = path.join(guideDir, 'images');
const mediaDir = path.join(guideDir, 'media');
const workDir = path.join(os.tmpdir(), 'forge-guide-capture');
const { chromium } = require(path.join(repoRoot, 'src/forge/Forge.UI/node_modules/playwright'));

const BASE = process.env.FORGE_UI_URL || 'http://localhost:5181';
const PRODUCT = process.env.FORGE_PRODUCT || 'code-intelligence';
const REDACT = process.env.FORGE_REDACT !== '0';
const VIEW = { width: 1440, height: 900 };
const what = new Set(process.argv.slice(2).length ? process.argv.slice(2) : ['shots', 'gifs', 'video']);

// Real code-intelligence records used for the detail views.
const STORY_DESIGN = 'code-intelligence:AGENT-MEMORY';   // 4 plan docs, 33 workstreams, done
const STORY_IMPL = 'code-intelligence:3D-RENDER';        // 12 impl + 12 e2e workstreams, done
const WS_ENG = 'ws-code-intelligence-3d-render-phase-3d-anchor-spring-layout-backend-stage-layout3d-engine-kernel'; // 12 iters, passed at 91
const WS_FAILED = 'ws-code-intelligence-agent-memory-phase-static-ingestion-pipeline-stage-method-and-block-embedding-publication'; // sloppy-parked, 3 reassignments
const DOC_ARCH = 'plan-doc:spawn-code-intelligence-AGENT-MEMORY:architecture.md'; // 18 iters, passed at 94

for (const d of [imagesDir, mediaDir, workDir]) fs.mkdirSync(d, { recursive: true });

// ── helpers ───────────────────────────────────────────────────────────────

function findChrome() {
  if (process.env.FORGE_CHROME) return process.env.FORGE_CHROME;
  const root = path.join(os.homedir(), 'AppData/Local/ms-playwright');
  if (!fs.existsSync(root)) return undefined;
  return fs.readdirSync(root)
    .filter((n) => /^chromium-\d+$/.test(n))
    .map((n) => path.join(root, n, 'chrome-win64/chrome.exe'))
    .filter((p) => fs.existsSync(p))
    .sort()
    .at(-1);
}

function findFfmpeg() {
  if (process.env.FORGE_FFMPEG) return process.env.FORGE_FFMPEG;
  try { return require('ffmpeg-static'); } catch { /* not installed */ }
  try { execFileSync('ffmpeg', ['-version'], { stdio: 'ignore' }); return 'ffmpeg'; } catch { return undefined; }
}

async function launch() {
  try { return await chromium.launch(); }
  catch {
    const exe = findChrome();
    if (!exe) throw new Error('No Chromium available. Run `npx playwright install chromium` in src/forge/Forge.UI or set FORGE_CHROME.');
    return chromium.launch({ executablePath: exe });
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const enc = encodeURIComponent;

// Scopes the UI to the product, redacts the operator e-mail, and installs the
// caption bar + cursor dot used by GIF / video recordings.
const initScript = ({ overlay, product }) => `
(() => {
  try { localStorage.setItem('forge.activeProductId', ${JSON.stringify(product)}); localStorage.removeItem('forge.activeRepoId'); } catch {}
  const rules = ${REDACT ? `[[/[a-z0-9._-]+@microsoft\\.com/gi, 'operator@example.com'], [/\\bxiaodoli\\b/g, 'operator']]` : '[]'};
  const scrub = (node) => {
    if (!rules.length) return;
    if (node.nodeType === 3) { let t = node.nodeValue, n = t; for (const [re, to] of rules) n = n.replace(re, to); if (n !== t) node.nodeValue = n; return; }
    if (node.nodeType !== 1) return;
    for (const f of (node.matches && node.matches('input,textarea') ? [node] : node.querySelectorAll('input,textarea'))) {
      let v = f.value, n = v; for (const [re, to] of rules) n = n.replace(re, to); if (n !== v) f.value = n;
    }
    const w = document.createTreeWalker(node, NodeFilter.SHOW_TEXT); const list = []; while (w.nextNode()) list.push(w.currentNode); list.forEach(scrub);
  };
  const start = () => {
    scrub(document.body);
    if (rules.length) {
      new MutationObserver((ms) => ms.forEach((m) => { if (m.type === 'characterData') scrub(m.target); m.addedNodes.forEach(scrub); }))
        .observe(document.body, { subtree: true, childList: true, characterData: true });
      setInterval(() => scrub(document.body), 400);
    }
    ${overlay ? `
    const cap = document.createElement('div'); cap.id = 'forge-guide-caption';
    Object.assign(cap.style, { position: 'fixed', left: '50%', bottom: '26px', transform: 'translateX(-50%)', zIndex: 99999,
      padding: '10px 18px', borderRadius: '8px', background: 'rgba(15,17,21,.93)', color: '#f3f4f6', border: '1px solid #f6ab40',
      font: '500 16px/1.35 "Segoe UI", system-ui, sans-serif', boxShadow: '0 6px 24px rgba(0,0,0,.45)', pointerEvents: 'none',
      display: 'none', maxWidth: '960px', textAlign: 'center' });
    document.body.appendChild(cap);
    const tag = document.createElement('div'); tag.id = 'forge-guide-scene';
    Object.assign(tag.style, { position: 'fixed', right: '18px', bottom: '26px', zIndex: 99999, padding: '6px 12px', borderRadius: '6px',
      background: 'rgba(246,171,64,.95)', color: '#14161a', font: '700 12px/1.2 "Segoe UI", system-ui, sans-serif', letterSpacing: '.08em',
      textTransform: 'uppercase', pointerEvents: 'none', display: 'none' });
    document.body.appendChild(tag);
    const cur = document.createElement('div'); cur.id = 'forge-guide-cursor';
    Object.assign(cur.style, { position: 'fixed', left: '0', top: '0', width: '18px', height: '18px', zIndex: 99999, pointerEvents: 'none',
      borderRadius: '50%', background: 'rgba(246,171,64,.85)', border: '2px solid #fff', boxShadow: '0 0 0 4px rgba(246,171,64,.25)',
      transform: 'translate(-50%,-50%)', transition: 'width .12s, height .12s' });
    document.body.appendChild(cur);
    window.addEventListener('mousemove', (e) => { cur.style.left = e.clientX + 'px'; cur.style.top = e.clientY + 'px'; }, true);
    window.addEventListener('mousedown', () => { cur.style.width = '12px'; cur.style.height = '12px'; }, true);
    window.addEventListener('mouseup', () => { cur.style.width = '18px'; cur.style.height = '18px'; }, true);` : ''}
  };
  if (document.body) start(); else document.addEventListener('DOMContentLoaded', start);
})();`;

async function newContext(browser, { overlay = false, video = null, product = PRODUCT } = {}) {
  const ctx = await browser.newContext({ viewport: VIEW, colorScheme: 'dark', deviceScaleFactor: 1, recordVideo: video ? { dir: video, size: VIEW } : undefined });
  await ctx.addInitScript(initScript({ overlay, product }));
  const page = await ctx.newPage();
  return { ctx, page };
}

async function go(page, route, wait = 1800) {
  await page.goto(new URL(route, BASE).toString(), { waitUntil: 'networkidle' }).catch(() => {});
  await sleep(wait);
}

async function signIn(page) {
  await go(page, '/', 2500);
  const gate = page.locator('#operator-title');
  if (await gate.count()) {
    const btn = page.getByRole('button', { name: /Sign in with this Windows account/ });
    if (await btn.count()) { await btn.click(); await sleep(3000); }
  }
  if (await gate.count()) throw new Error('Could not sign in automatically. Pair the browser once from the host machine and retry.');
}

const main = (page) => page.locator('main.main');
const modal = (page) => page.locator('.modal.show').last();
const nav = (page, label) => page.locator('.nav-item', { hasText: label }).first();
const shot = (page, name, opts = {}) => page.screenshot({ path: path.join(imagesDir, name), ...opts });

async function click(loc, wait = 1200) {
  try { await loc.first().click({ timeout: 8000 }); await sleep(wait); return true; }
  catch (e) { console.warn('  click skipped:', e.message.split('\n')[0].slice(0, 120)); return false; }
}

// Smooth pointer travel + click, for recordings.
async function glide(page, locator, { click: doClick = true, dwell = 350, wait = 1200 } = {}) {
  const el = locator.first();
  try {
    await el.scrollIntoViewIfNeeded({ timeout: 5000 });
    const box = await el.boundingBox();
    if (!box) return false;
    await page.mouse.move(box.x + Math.min(box.width / 2, 160), box.y + Math.min(box.height / 2, 22), { steps: 22 });
    await sleep(dwell);
    if (doClick) { await page.mouse.down(); await sleep(70); await page.mouse.up(); await sleep(wait); }
    return true;
  } catch (e) { console.warn('  glide skipped:', e.message.split('\n')[0].slice(0, 120)); return false; }
}

async function caption(page, text, scene) {
  await page.evaluate(([t, s]) => {
    const c = document.getElementById('forge-guide-caption'); if (c) { c.textContent = t; c.style.display = t ? 'block' : 'none'; }
    const g = document.getElementById('forge-guide-scene'); if (g && s !== undefined) { g.textContent = s; g.style.display = s ? 'block' : 'none'; }
  }, [text, scene]).catch(() => {});
}

// Scrolls the first scrollable descendant of a container (modals, detail panes).
async function scrollInner(page, selector, top) {
  await page.locator(selector).last().evaluate((el, y) => {
    const s = [el, ...el.querySelectorAll('*')].find((n) => n.scrollHeight > n.clientHeight + 40 && /(auto|scroll)/.test(getComputedStyle(n).overflowY));
    if (s) s.scrollTop = y;
  }, top).catch(() => {});
  await sleep(600);
}

const storyUrl = (id, mode, extra = '') => `/workstreams/${enc(id)}?mode=${mode}${extra}`;
const wsUrl = (story, ws, tab) => storyUrl(story, 'impl', `&master=open&sel=phase&selId=${enc(ws)}${tab ? `&tab=${tab}` : ''}`);
const docUrl = (story, doc, tab) => storyUrl(story, 'design', `&master=open&sel=doc&selId=${enc(doc)}${tab ? `&tab=${tab}` : ''}`);
const liveUrl = (id) => `/work-items/${enc(id)}/live`;

// ── screenshots ───────────────────────────────────────────────────────────

async function screenshots(browser) {
  console.log('▶ screenshots');
  {
    // Sign-in gate: block automatic sign-in so the gate stays visible.
    const { ctx, page } = await newContext(browser);
    await page.route('**/api/operator/local-sign-in', (r) => r.fulfill({ status: 403, contentType: 'application/json', body: '{}' }));
    await go(page, '/', 2500);
    await shot(page, '00-sign-in.png');
    await ctx.close();
  }
  const { ctx, page } = await newContext(browser);
  await signIn(page);

  // S1 — operator, providers, crews, models
  await go(page, '/');                                             await shot(page, '01-overview.png');
  await click(page.locator('header .avatar, .topbar .avatar'));   await shot(page, '02-user-menu-role.png');
  await page.keyboard.press('Escape'); await sleep(300);
  await go(page, '/settings'); await click(main(page).getByText('Repository providers', { exact: true }), 1500); await shot(page, '03-settings-providers.png');
  await go(page, '/crews');                                        await shot(page, '04-crews-roster.png');
  await click(main(page).getByText('Pair', { exact: true }));      await shot(page, '05-crews-pair-models.png');
  await click(main(page).getByText('Runtime', { exact: true }));   await shot(page, '06-crews-runtime.png');
  await click(main(page).getByText('Quota', { exact: true }));     await shot(page, '07-crews-quota.png');
  await click(main(page).getByText('Capabilities', { exact: true })); await shot(page, '08-crews-capabilities.png');
  await click(main(page).getByText('Roster', { exact: true }), 800);
  await click(main(page).locator('img').nth(1), 1800);             await shot(page, '09-agent-edit-soul.png');
  await page.keyboard.press('Escape'); await sleep(400);

  // S2 — product, repos, sync, gates
  await go(page, '/products');                                     await shot(page, '10-products.png');
  await click(page.getByText('New product', { exact: true }), 900); await shot(page, '11-new-product.png');
  await page.keyboard.press('Escape'); await sleep(400);
  await click(main(page).locator('button', { hasText: 'Attach repo' }).nth(3), 900); await shot(page, '12-attach-repo.png');
  await page.keyboard.press('Escape'); await sleep(400);
  await click(main(page).locator(`button[title^="Edit ${PRODUCT}"]`), 1200);        await shot(page, '13-edit-repo-general.png');
  await click(modal(page).locator('button', { hasText: /^Sync$/ }), 900);           await shot(page, '14-edit-repo-sync.png');
  await click(modal(page).locator('button', { hasText: /^Validation/ }), 1200);     await shot(page, '15-edit-repo-validation-gate.png');
  await scrollInner(page, '.modal.show', 900);                                      await shot(page, '16-edit-repo-validation-tests.png');
  await page.keyboard.press('Escape'); await sleep(400);
  const hist = main(page).locator(`button[title$="history for ${PRODUCT}"]`);
  if (await click(hist, 2000)) { await hist.first().scrollIntoViewIfNeeded().catch(() => {}); await sleep(400); await shot(page, '17-build-history.png'); }
  await page.keyboard.press('Escape'); await sleep(300);

  // S3 — work items and live execution
  await go(page, '/work-items');                                   await shot(page, '20-work-items-board.png');
  await click(main(page).getByText('Tree', { exact: true }));      await shot(page, '21-work-items-tree.png');
  await click(main(page).getByText('Exec', { exact: true }));      await shot(page, '22-work-items-exec.png');
  await go(page, liveUrl(WS_ENG), 3000);                           await shot(page, '23-live-execution.png');
  await click(main(page).getByText(/^Summary$/), 1500);           await shot(page, '24-live-summary.png');
  await click(main(page).getByText(/^Build$/), 1500);             await shot(page, '25-live-build-gate.png');
  await click(main(page).getByText(/^Review$/), 1500);            await shot(page, '26-live-review.png');
  await click(main(page).getByText(/^Progress$/), 1500);          await shot(page, '27-live-progress-verdicts.png');
  await click(main(page).getByText(/^Tokens$/), 1500);            await shot(page, '28-live-tokens.png');

  // S4 — story lifecycle
  await go(page, '/workstreams');                                  await shot(page, '30-workstreams-index.png');
  await click(main(page).getByText(/SPAWN STORY/), 1200);          await shot(page, '31-spawn-story-pick.png');
  await click(page.getByText(/Pick →/), 1500);                     await shot(page, '32-spawn-story-scope.png');
  await scrollInner(page, '.modal.show, [role="dialog"]', 600);    await shot(page, '33-spawn-story-scope-2.png');
  await page.keyboard.press('Escape'); await sleep(400);
  await go(page, storyUrl(STORY_DESIGN, 'design'), 3000);          await shot(page, '34-story-design-plan.png');
  await go(page, docUrl(STORY_DESIGN, 'doc:architecture', 'document'), 3000); await shot(page, '35-story-design-document.png');
  await go(page, docUrl(STORY_DESIGN, 'doc:architecture', 'questions'), 2500); await shot(page, '36-story-design-questions.png');
  await go(page, storyUrl(STORY_IMPL, 'impl'), 3000);              await shot(page, '37-story-impl-pairs.png');
  await go(page, wsUrl(STORY_IMPL, WS_ENG), 3000);                 await shot(page, '38-story-workstream-overview.png');
  await go(page, wsUrl(STORY_IMPL, WS_ENG, 'conv'), 3000);         await shot(page, '39-story-workstream-kpis.png');
  await go(page, wsUrl(STORY_IMPL, WS_ENG, 'pr'), 3000);           await shot(page, '40-story-workstream-pr-review.png');
  await go(page, storyUrl(STORY_IMPL, 'release'), 3000);           await shot(page, '41-story-release.png');
  await go(page, wsUrl(STORY_IMPL, WS_ENG, 'trace'), 3000);        await shot(page, '42-story-trace.png');
  await go(page, '/prs'); await click(main(page).getByText(/ADO lifecycle/), 2000); await shot(page, '44-prs-lifecycle.png');

  // S5 — guardrails, prompts and context
  await go(page, wsUrl(STORY_IMPL, WS_ENG, 'conv'), 3000);
  await click(main(page).locator('.sc-detail-panel button', { hasText: /^Prompt$/ }), 2500); await shot(page, '50-prompt-audit.png');
  await click(main(page).locator('.sc-detail-panel').getByText(/system prompt/i), 1500);    await shot(page, '51-prompt-system-soul.png');
  await scrollInner(page, '.sc-detail-panel', 1400);                                          await shot(page, '52-prompt-user-context.png');
  await scrollInner(page, '.sc-detail-panel', 3200);                                          await shot(page, '53-prompt-user-rules.png');
  await go(page, docUrl(STORY_DESIGN, 'doc:architecture', 'conv'), 3000);
  await click(main(page).locator('.sc-detail-panel button', { hasText: /^Prompt$/ }), 2500); await shot(page, '54-prompt-evaluator-feedback.png');
  await go(page, liveUrl(WS_FAILED), 3000);                        await shot(page, '55-sloppy-parked.png');
  await click(main(page).getByText(/^Progress$/), 1500);          await shot(page, '56-sloppy-progress.png');
  await go(page, liveUrl(DOC_ARCH), 3000); await click(main(page).getByText(/^Tokens$/), 1500); await shot(page, '57-prompt-size-trend.png');
  await go(page, '/policies');                                     await shot(page, '58-policies.png');

  // S6 — cost and performance
  await go(page, '/cost');                                         await shot(page, '60-cost.png');
  await go(page, '/performance', 2500);
  await click(main(page).getByText(/^Product: All/), 700); await click(page.getByText(PRODUCT, { exact: true }).last(), 2500);
  await click(main(page).locator('h1.page-title'), 600);          await shot(page, '61-performance-ladder.png');
  await page.mouse.wheel(0, 900); await sleep(700);                await shot(page, '62-performance-scorecard.png');
  await go(page, '/crews'); await click(main(page).getByText('Performance', { exact: true }), 2000); await shot(page, '63-crews-performance.png');
  await click(main(page).getByText('History', { exact: true }), 2000); await shot(page, '64-crews-history.png');
  await go(page, '/swarm');                                        await shot(page, '65-swarm.png');
  await go(page, '/audit');                                        await shot(page, '66-audit.png');
  await ctx.close();
}

// ── recordings ────────────────────────────────────────────────────────────

async function record(browser, name, run, { product = PRODUCT } = {}) {
  const dir = path.join(workDir, name);
  fs.rmSync(dir, { recursive: true, force: true });
  const { ctx, page } = await newContext(browser, { overlay: true, video: dir, product });
  await signIn(page);
  try { await run(page); } catch (e) { console.warn(`  ${name}: ${e.message.split('\n')[0]}`); }
  const video = page.video();
  await ctx.close();
  const dst = path.join(workDir, `${name}.webm`);
  fs.copyFileSync(await video.path(), dst);
  return dst;
}

async function gifs(browser) {
  console.log('▶ gif clips');
  const clips = {};

  clips['switch-product'] = await record(browser, 'switch-product', async (page) => {
    await go(page, '/products', 1200);
    await caption(page, 'Top bar: the product switcher scopes every view');
    await glide(page, page.locator('#bc-current'), { click: false, dwell: 600 });
    await page.selectOption('#bc-current', PRODUCT); await sleep(1800);
    await caption(page, 'Products: Activate makes a product the default for the sidebar');
    await glide(page, main(page).locator('button', { hasText: 'Activate' }).first(), { click: false, dwell: 1500 });
  }, { product: 'local' });

  clips['repo-gate'] = await record(browser, 'repo-gate', async (page) => {
    await go(page, '/products', 1200);
    await caption(page, 'Edit repo → Validation: restore, build and test commands are the gate');
    await glide(page, main(page).locator(`button[title^="Edit ${PRODUCT}"]`));
    await glide(page, modal(page).locator('button', { hasText: /^Sync$/ }), { wait: 1500 });
    await glide(page, modal(page).locator('button', { hasText: /^Validation/ }), { wait: 1500 });
    await scrollInner(page, '.modal.show', 900); await sleep(1500);
    await page.keyboard.press('Escape'); await sleep(600);
  });

  clips['live-execution'] = await record(browser, 'live-execution', async (page) => {
    await go(page, liveUrl(WS_ENG), 2500);
    await caption(page, 'Live execution: generator ⇄ evaluator pair, iteration 12 of 50, gates');
    await glide(page, main(page).getByText(/^Summary$/), { wait: 1500 });
    await caption(page, 'Step through iterations: score climbs 53 → 86 → 91');
    for (const chip of [/^1\s/, /^10\s/, /^12\s/]) await glide(page, main(page).locator('nav.live-exec__iter-pager button', { hasText: chip }), { wait: 1500 });
    await glide(page, main(page).getByText(/^Build$/), { wait: 1800 });
    await caption(page, 'Build gate and test gate must pass before the evaluator scores');
    await sleep(1200);
  });

  clips['spawn-story'] = await record(browser, 'spawn-story', async (page) => {
    await go(page, '/workstreams', 1200);
    await caption(page, 'Spawn story: pick a Feature or PBI, confirm scope, spawn');
    await glide(page, main(page).getByText(/SPAWN STORY/), { wait: 1500 });
    await glide(page, page.getByText(/Pick →/), { wait: 1800 });
    await scrollInner(page, '.modal.show, [role="dialog"]', 600); await sleep(1500);
    await caption(page, 'Cancel here — the real story already ran');
    await page.keyboard.press('Escape'); await sleep(700);
  });

  clips['story-modes'] = await record(browser, 'story-modes', async (page) => {
    await go(page, storyUrl(STORY_DESIGN, 'design'), 2000);
    await caption(page, 'Design & Plan: architect pairs write four plan docs, each merged as a PR');
    await sleep(2200);
    await go(page, storyUrl(STORY_DESIGN, 'impl'), 2000);
    await caption(page, 'Impl ⇄ Review: one engineer pair per workstream, implementation and review PRs');
    await sleep(2200);
    await go(page, storyUrl(STORY_DESIGN, 'release'), 2000);
    await caption(page, 'Release: QA pairs and end-to-end PRs');
    await sleep(2000);
  });

  clips['prompt-audit'] = await record(browser, 'prompt-audit', async (page) => {
    await go(page, wsUrl(STORY_IMPL, WS_ENG, 'conv'), 2500);
    await caption(page, 'Conversation → Prompt: the exact system and user prompt of each iteration');
    await glide(page, main(page).locator('.sc-detail-panel button', { hasText: /^Prompt$/ }), { wait: 2000 });
    await glide(page, main(page).locator('.sc-detail-panel').getByText(/system prompt/i), { wait: 1500 });
    await caption(page, 'System prompt = the agent soul; user prompt = brief, notes, evaluator feedback, rules');
    await scrollInner(page, '.sc-detail-panel', 1200); await sleep(1500);
    await scrollInner(page, '.sc-detail-panel', 2600); await sleep(1500);
  });

  return clips;
}

async function video(browser) {
  console.log('▶ walkthrough video');
  return record(browser, 'walkthrough', async (page) => {
    // S1
    await go(page, '/', 1200);
    await caption(page, 'Forge on the code-intelligence product: an agent swarm that plans, builds, reviews and tests', '1 · Operator setup'); await sleep(3200);
    await glide(page, page.locator('header .avatar, .topbar .avatar'));
    await caption(page, 'Your role on the active product: product-owner unlocks crew, repo and story mutations'); await sleep(3000);
    await page.keyboard.press('Escape'); await sleep(400);
    await go(page, '/settings', 1200); await glide(page, main(page).getByText('Repository providers', { exact: true }));
    await caption(page, 'Settings → Repository providers: ADO and GitHub credentials, both connected'); await sleep(3000);
    await glide(page, nav(page, 'Crews'), { wait: 1800 });
    await caption(page, 'Crews: Architecture, Engineering, Review and QA rosters'); await sleep(2600);
    await glide(page, main(page).getByText('Pair', { exact: true }));
    await caption(page, 'Pair: generator and evaluator models must differ so the review pass catches what the generator missed'); await sleep(3200);
    await glide(page, main(page).getByText('Runtime', { exact: true }));
    await caption(page, 'Runtime: preferred model, backend (copilot / claude) and invoke mode per crew'); await sleep(3000);
    await glide(page, main(page).getByText('Quota', { exact: true }));
    await caption(page, 'Quota: concurrency, weekly task cap, daily and per-task budgets'); await sleep(2800);

    // S2
    await glide(page, nav(page, 'Products'), { wait: 1500 }).catch(() => {});
    await go(page, '/products', 1200);
    await caption(page, 'Products & repos: code-intelligence is one GitHub repo, cloned under E:\\forge, synced 2h ago, gated', '2 · Product setup'); await sleep(3200);
    await glide(page, main(page).locator(`button[title^="Edit ${PRODUCT}"]`));
    await caption(page, 'Edit repo → General: display name, local clone path, default branch'); await sleep(2600);
    await glide(page, modal(page).locator('button', { hasText: /^Sync$/ }));
    await caption(page, 'Sync: area paths, agent-ready default team, sync cadence for work items, PRs and builds'); await sleep(3000);
    await glide(page, modal(page).locator('button', { hasText: /^Validation/ }));
    await caption(page, 'Validation: restore, build and test commands with timeouts — this is the build + test gate'); await sleep(3200);
    await scrollInner(page, '.modal.show', 900);
    await caption(page, 'Impacted-test narrowing, e2e test style, and the run history of the gate'); await sleep(3000);
    await page.keyboard.press('Escape'); await sleep(600);

    // S3
    await glide(page, nav(page, 'Work items'), { wait: 1800 });
    await caption(page, 'Work items: 500 synced items — Sync now pulls from the provider immediately', '3 · Work items & live execution'); await sleep(3000);
    await glide(page, main(page).getByText('Tree', { exact: true }));
    await caption(page, 'Tree: Feature → workstream steps, each with its execution status'); await sleep(2600);
    await go(page, liveUrl(WS_ENG), 2500);
    await caption(page, 'Live execution: Cheng Yu (generator) ⇄ Wang Jing (evaluator), iteration 12 of 50, completed'); await sleep(3200);
    await glide(page, main(page).getByText(/^Summary$/));
    await caption(page, 'Summary per iteration: what the generator changed, how it resolved prior feedback, verification'); await sleep(3200);
    await glide(page, main(page).locator('nav.live-exec__iter-pager button', { hasText: /^1\s/ }));
    await caption(page, 'Iteration 1 scored 53 — the pair kept iterating until 91'); await sleep(2600);
    await glide(page, main(page).getByText(/^Build$/));
    await caption(page, 'Build gate and test gate ran on the worktree before the evaluator scored'); await sleep(2800);
    await glide(page, page.locator('main.main').getByText(/^Progress$/));
    await caption(page, 'Progress: the evaluator verdict and notes for every iteration'); await sleep(2800);
    await glide(page, page.locator('main.main').getByText(/^Tokens$/));
    await caption(page, 'Tokens: cost per role and prompt size across iterations'); await sleep(2600);

    // S4
    await glide(page, nav(page, 'Workstreams'), { wait: 1800 });
    await caption(page, 'Workstreams: nine stories on code-intelligence, all done — phases, score, controls', '4 · Story lifecycle'); await sleep(3000);
    await glide(page, main(page).getByText(/SPAWN STORY/), { wait: 1500 });
    await caption(page, 'Spawn story: pick a Feature or PBI…'); await sleep(2000);
    await glide(page, page.getByText(/Pick →/), { wait: 1500 });
    await caption(page, '…confirm title, points, architect prompt, repo, base branch and auto-merge, then Spawn'); await sleep(3200);
    await page.keyboard.press('Escape'); await sleep(500);
    await go(page, storyUrl(STORY_DESIGN, 'design'), 2500);
    await caption(page, 'Design & Plan: architecture, tech spec, implementation plan and e2e scenarios, each by a pair, each a merged PR'); await sleep(3400);
    await go(page, docUrl(STORY_DESIGN, 'doc:architecture', 'document'), 2500);
    await caption(page, 'Document tab: the rendered architecture.md — 18 iterations, score 94'); await sleep(3000);
    await go(page, storyUrl(STORY_IMPL, 'impl'), 2500);
    await caption(page, 'Impl ⇄ Review: one engineer pair per workstream; 36 merged implementation and review PRs'); await sleep(3200);
    await go(page, wsUrl(STORY_IMPL, WS_ENG, 'conv'), 2500);
    await caption(page, 'Workstream conversation: per-iteration score, tokens, cost, files, context usage, pair'); await sleep(3200);
    await glide(page, main(page).locator('.sc-detail-panel .tabs button', { hasText: 'PR' }));
    await caption(page, 'PR tab: the review crew\'s comments, all resolved before merge'); await sleep(2800);
    await go(page, storyUrl(STORY_IMPL, 'release'), 2500);
    await caption(page, 'Release: QA pairs add end-to-end tests per stage'); await sleep(2600);
    await go(page, wsUrl(STORY_IMPL, WS_ENG, 'trace'), 2500);
    await caption(page, 'Trace: every workstream run with status, verdict and score'); await sleep(2800);

    // S5
    await go(page, wsUrl(STORY_IMPL, WS_ENG, 'conv'), 2500);
    await glide(page, main(page).locator('.sc-detail-panel button', { hasText: /^Prompt$/ }), { wait: 2000 });
    await caption(page, 'Guardrails start with the prompt: every iteration\'s system + user prompt is persisted and auditable', '5 · Guardrails & context'); await sleep(3200);
    await glide(page, main(page).locator('.sc-detail-panel').getByText(/system prompt/i), { wait: 1500 });
    await caption(page, 'System prompt = the agent\'s soul.md; user prompt = brief, prior-iteration notes, evaluator feedback, hard rules'); await sleep(3400);
    await scrollInner(page, '.sc-detail-panel', 1200); await sleep(2200);
    await scrollInner(page, '.sc-detail-panel', 2600);
    await caption(page, 'Rules in the prompt: verify it compiles before DONE, no deleting production code, notes required each iteration'); await sleep(3400);
    await go(page, liveUrl(WS_FAILED), 2500);
    await caption(page, 'Sloppy detection: no progress across iterations → parked, reassigned to a fresh pair, failed after 3 pairs'); await sleep(3600);
    await glide(page, page.locator('main.main').getByText(/^Progress$/));
    await caption(page, 'The evaluator is skipped when the generator fails, so tokens are not burned on broken output'); await sleep(3200);
    await go(page, '/policies', 1500);
    await caption(page, 'Policies: drag rules, human gates and branch protection sit on top of the agent guardrails'); await sleep(3000);

    // S6
    await glide(page, nav(page, 'Costs'), { wait: 1800 });
    await caption(page, 'Costs: spend vs daily cap, burn rate, per-agent burn, alerts', '6 · Cost & performance'); await sleep(3000);
    await glide(page, nav(page, 'Performance'), { wait: 2200 });
    await glide(page, main(page).getByText(/^Product: All/), { wait: 700 }); await glide(page, page.getByText(PRODUCT, { exact: true }).last(), { wait: 2000 });
    await glide(page, main(page).locator('h1.page-title'), { wait: 500 });
    await caption(page, 'Performance filtered to code-intelligence: promotion ladder and scorecards from evaluator scores'); await sleep(3400);
    await glide(page, nav(page, 'Crews'), { wait: 1500 }); await glide(page, main(page).getByText('Performance', { exact: true }), { wait: 1800 });
    await caption(page, 'Crew performance: payroll vs token spend, per-member level and changes'); await sleep(3000);
    await glide(page, nav(page, 'Audit'), { wait: 1800 });
    await caption(page, 'Audit: every write, with user, idempotency key and body snippet. Docs: docs/forge/guide/usage-guide.md'); await sleep(3200);
  });
}

async function intro(browser) {
  console.log('▶ intro video (story spawn → spec → impl)');
  return record(browser, 'intro', async (page) => {
    await go(page, '/workstreams', 900);
    await caption(page, 'Forge in two minutes: a story goes from spec to merged code without a human in the loop', 'Forge · intro'); await sleep(2900);
    await caption(page, 'Workstreams: one row per story — phases, workstreams, target branch', '1 · Spawn'); await sleep(2600);
    await glide(page, main(page).getByText(/SPAWN STORY/), { wait: 1200 });
    await caption(page, 'Spawn story: pick a Feature or PBI from the product'); await sleep(2400);
    await glide(page, page.getByText(/Pick →/), { wait: 1200 });
    await caption(page, 'Confirm scope: points, the architect prompt, repo, base branch, auto-merge — then Spawn'); await sleep(3000);
    await scrollInner(page, '.modal.show, [role="dialog"]', 600); await sleep(1200);
    await page.keyboard.press('Escape'); await sleep(500);

    await go(page, storyUrl(STORY_DESIGN, 'design'), 2200);
    await caption(page, 'Design & Plan: architect pairs write architecture, tech spec, implementation plan and e2e scenarios', '2 · Spec'); await sleep(3000);
    await caption(page, 'Each doc is a generator ⇄ evaluator loop: iterations, score, cost — and its own merged PR'); await sleep(3000);
    await go(page, docUrl(STORY_DESIGN, 'doc:architecture', 'document'), 2200);
    await caption(page, 'The rendered architecture.md: 18 iterations, score 94, merged to the plan branch'); await sleep(2900);
    await go(page, docUrl(STORY_DESIGN, 'doc:architecture', 'questions'), 2000);
    await caption(page, 'Open questions the architects raised are answered here — unanswered ones block the evaluator'); await sleep(3000);

    await go(page, storyUrl(STORY_IMPL, 'impl'), 2200);
    await caption(page, 'Impl ⇄ Review: the plan is decomposed into workstreams, one engineer pair each, in parallel worktrees', '3 · Impl'); await sleep(3000);
    await caption(page, 'Every workstream ends in an implementation PR and a review PR — 36 merged here'); await sleep(2600);
    await go(page, wsUrl(STORY_IMPL, WS_ENG), 2400);
    await caption(page, 'One workstream: three pairs, 12 iterations, build ✓ test ✓, score 91, branch and PR'); await sleep(3000);
    await glide(page, main(page).locator('.sc-detail-panel .tabs button', { hasText: 'Conversation' }), { wait: 2200 });
    await caption(page, 'Per iteration: score, tokens, cost, files, context usage — the pair iterates until ≥ 90'); await sleep(3000);
    await glide(page, main(page).locator('.sc-detail-panel .tabs button', { hasText: 'PR' }), { wait: 2000 });
    await caption(page, 'The Review crew comments on the PR; the generator fixes; the evaluator re-scores; then it merges'); await sleep(3000);

    await go(page, storyUrl(STORY_IMPL, 'release'), 2200);
    await caption(page, 'Release: QA pairs add end-to-end tests per stage — all green before the story is done', '4 · Release'); await sleep(2900);
    await go(page, '/workstreams', 1600);
    await caption(page, 'Nine stories, about 480 workstreams, all merged. Full guide: docs/forge/guide/usage-guide.md', 'Forge'); await sleep(3000);
  });
}

// ── encoding ──────────────────────────────────────────────────────────────

function encodeGif(ffmpeg, webm, out) {
  execFileSync(ffmpeg, ['-y', '-loglevel', 'error', '-i', webm,
    '-vf', 'fps=8,scale=880:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=96:stats_mode=diff[p];[b][p]paletteuse=dither=none:diff_mode=rectangle',
    out], { stdio: 'inherit' });
}

function encodeMp4(ffmpeg, webm, out) {
  execFileSync(ffmpeg, ['-y', '-loglevel', 'error', '-i', webm, '-c:v', 'libx264', '-preset', 'medium', '-crf', '23', '-pix_fmt', 'yuv420p',
    '-movflags', '+faststart', '-r', '25', out], { stdio: 'inherit' });
}

// Size-bounded encode: 1280 px wide, 24 fps, capped bitrate so 2 min stays under 5 MB.
function encodeMp4Small(ffmpeg, webm, out) {
  execFileSync(ffmpeg, ['-y', '-loglevel', 'error', '-i', webm, '-vf', 'scale=1280:-2', '-c:v', 'libx264', '-preset', 'slow', '-tune', 'stillimage',
    '-b:v', '260k', '-maxrate', '300k', '-bufsize', '600k', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', '-r', '24', out], { stdio: 'inherit' });
}

// ── main ──────────────────────────────────────────────────────────────────

const browser = await launch();
try {
  if (what.has('shots')) await screenshots(browser);
  const ffmpeg = findFfmpeg();
  if (what.has('gifs')) {
    for (const [name, webm] of Object.entries(await gifs(browser))) {
      if (ffmpeg) { encodeGif(ffmpeg, webm, path.join(mediaDir, `${name}.gif`)); console.log(`  ${name}.gif`); }
      else { fs.copyFileSync(webm, path.join(mediaDir, `${name}.webm`)); console.log(`  ${name}.webm (no ffmpeg — GIF skipped)`); }
    }
  }
  if (what.has('video')) {
    const webm = await video(browser);
    if (ffmpeg) { encodeMp4(ffmpeg, webm, path.join(mediaDir, 'forge-walkthrough.mp4')); console.log('  forge-walkthrough.mp4'); }
    else fs.copyFileSync(webm, path.join(mediaDir, 'forge-walkthrough.webm'));
  }
  if (what.has('intro')) {
    const webm = await intro(browser);
    if (ffmpeg) { encodeMp4Small(ffmpeg, webm, path.join(mediaDir, 'forge-intro.mp4')); console.log('  forge-intro.mp4'); }
    else fs.copyFileSync(webm, path.join(mediaDir, 'forge-intro.webm'));
  }
} finally {
  await browser.close();
}
console.log('done →', guideDir);
