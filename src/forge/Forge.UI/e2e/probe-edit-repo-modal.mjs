// Standalone Playwright probe to verify the EditRepoModal layout fix.
// Run with: node e2e/probe-edit-repo-modal.mjs
//
// Asserts:
//   1. /products loads (the import-broken regression is gone)
//   2. Clicking "Edit" on a repo opens the modal
//   3. Validation tab shows all 6 form fields (Init, Restore, Build, Test, Build TO, Test TO)
//   4. Init+Restore land on the same horizontal row (2-col grid works)
//   5. The form is fully visible without scrolling the outer container
//   6. If validation history exists, ONLY the history div has overflow:auto
//
// Captures screenshots to e2e/probe-screenshots/ so we can eyeball the
// result even when running headless.

import { chromium } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

const SHOTS = resolve('e2e/probe-screenshots');
mkdirSync(SHOTS, { recursive: true });

const BASE = 'http://localhost:5181';
const fail = (m) => { console.error(`✗ ${m}`); process.exitCode = 1; };
const pass = (m) => console.log(`✓ ${m}`);

const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ viewport: { width: 1440, height: 1080 } });
const page = await ctx.newPage();
const consoleErrs = [];
page.on('pageerror', (e) => consoleErrs.push(`pageerror: ${e.message}`));
page.on('console', (m) => { if (m.type() === 'error') consoleErrs.push(`console.error: ${m.text()}`); });

try {
  console.log(`\n→ Navigating to ${BASE}/products`);
  await page.goto(`${BASE}/products`, { waitUntil: 'networkidle', timeout: 15_000 });
  await page.screenshot({ path: `${SHOTS}/01-products.png`, fullPage: false });

  // 1. No import-related console error.
  const importErr = consoleErrs.find((e) => /EditRepoModal|does not provide an export/.test(e));
  if (importErr) {
    fail(`Import regression still present: ${importErr}`);
    throw new Error('module load broken');
  }
  pass('No EditRepoModal import error on console');

  // 2. Find the Edit-REPO button (title contains "(path, branch, pipeline)").
  //    There are also Edit-product buttons (title="Edit product") we must skip.
  await page.waitForSelector('button[title*="(path, branch, pipeline)"]', { timeout: 10_000 });
  const editButtons = await page.locator('button[title*="(path, branch, pipeline)"]').all();
  console.log(`  Found ${editButtons.length} Edit-repo button(s) on /products`);
  if (editButtons.length === 0) {
    fail('No Edit-repo button found — no repos attached to any product?');
    await page.screenshot({ path: `${SHOTS}/products-empty.png`, fullPage: true });
    throw new Error('no repos to edit');
  }
  await editButtons[0].click();

  // 3. Modal opens.
  await page.waitForSelector('[role="dialog"][aria-labelledby="edit-repo-title"]', { timeout: 5_000 });
  pass('Edit repo modal opened');
  await page.screenshot({ path: `${SHOTS}/02-general-tab.png`, fullPage: false });

  // 4. Click Validation tab.
  const valTab = page.locator('button', { hasText: /^Validation/ });
  await valTab.click();
  await page.waitForSelector('[data-testid="repo-init-command"]', { timeout: 3_000 });
  pass('Validation tab rendered');
  await page.screenshot({ path: `${SHOTS}/03-validation-tab.png`, fullPage: false });

  // 5. All 6 fields are present and visible.
  const fields = [
    'repo-init-command',
    'repo-restore-command',
    'repo-build-command',
    'repo-test-command',
    'repo-build-timeout',
    'repo-test-timeout',
  ];
  const boxes = {};
  for (const id of fields) {
    const loc = page.locator(`[data-testid="${id}"]`);
    const count = await loc.count();
    if (count !== 1) { fail(`Expected 1 ${id}, found ${count}`); continue; }
    const visible = await loc.isVisible();
    if (!visible) { fail(`${id} present but not visible`); continue; }
    const b = await loc.boundingBox();
    boxes[id] = b;
    pass(`${id} visible at (${b.x.toFixed(0)},${b.y.toFixed(0)}) ${b.width.toFixed(0)}×${b.height.toFixed(0)}`);
  }

  // 6. 2-col grid check: Init and Restore should be roughly on the same Y
  //    (within 5px) and side-by-side (Restore.x > Init.x + Init.width/2).
  if (boxes['repo-init-command'] && boxes['repo-restore-command']) {
    const i = boxes['repo-init-command'];
    const r = boxes['repo-restore-command'];
    const sameRow = Math.abs(i.y - r.y) < 5;
    const sideBySide = r.x > i.x + i.width * 0.5;
    if (sameRow && sideBySide) pass(`Init|Restore are 2-col (Δy=${(r.y - i.y).toFixed(1)}px, Restore.x=${r.x.toFixed(0)} > Init.right=${(i.x + i.width).toFixed(0)})`);
    else fail(`Init|Restore NOT 2-col. Init=(${i.x.toFixed(0)},${i.y.toFixed(0)} w=${i.width.toFixed(0)}) Restore=(${r.x.toFixed(0)},${r.y.toFixed(0)} w=${r.width.toFixed(0)}) sameRow=${sameRow} sideBySide=${sideBySide}`);
  }
  if (boxes['repo-build-command'] && boxes['repo-test-command']) {
    const b = boxes['repo-build-command'];
    const t = boxes['repo-test-command'];
    const sameRow = Math.abs(b.y - t.y) < 5;
    const sideBySide = t.x > b.x + b.width * 0.5;
    if (sameRow && sideBySide) pass(`Build|Test are 2-col`);
    else fail(`Build|Test NOT 2-col. Build.y=${b.y.toFixed(0)} Test.y=${t.y.toFixed(0)}`);
  }
  if (boxes['repo-build-timeout'] && boxes['repo-test-timeout']) {
    const b = boxes['repo-build-timeout'];
    const t = boxes['repo-test-timeout'];
    const sameRow = Math.abs(b.y - t.y) < 5;
    const sideBySide = t.x > b.x + b.width * 0.5;
    if (sameRow && sideBySide) pass(`BuildTimeout|TestTimeout are 2-col`);
    else fail(`BuildTimeout|TestTimeout NOT 2-col. BT.y=${b.y.toFixed(0)} TT.y=${t.y.toFixed(0)}`);
  }

  // 7. Scroll-scope check. Walk DOM from the modal root and count how many
  //    descendants have computed overflow-y === 'auto' (or 'scroll'). We
  //    want at MOST: (a) the outer flex-content wrapper (which only scrolls
  //    if total content > 90vh, but doesn't have a visible scrollbar today),
  //    and (b) the ValidationHistory wrapper (only if history is populated).
  //    Modal.scrollHeight === modal.clientHeight in the normal case means
  //    no outer scroll is active.
  const scrollMap = await page.evaluate(() => {
    const modal = document.querySelector('[role="dialog"][aria-labelledby="edit-repo-title"]');
    if (!modal) return null;
    const results = [];
    modal.querySelectorAll('*').forEach((el) => {
      const cs = getComputedStyle(el);
      if ((cs.overflowY === 'auto' || cs.overflowY === 'scroll') && el.scrollHeight > el.clientHeight + 1) {
        const cls = el.getAttribute('class') || '';
        const test = el.getAttribute('data-testid') || '';
        results.push({
          tag: el.tagName.toLowerCase(),
          cls: cls.slice(0, 40),
          test,
          clientH: el.clientHeight,
          scrollH: el.scrollHeight,
          maxH: cs.maxHeight,
          // first text node of first child gives us a hint of which section
          firstText: (el.textContent || '').trim().slice(0, 60),
        });
      }
    });
    return {
      activeScrollers: results,
      modalScrolls: modal.scrollHeight > modal.clientHeight + 1,
    };
  });
  if (scrollMap) {
    console.log(`  Active scrollers inside modal: ${scrollMap.activeScrollers.length}`);
    for (const s of scrollMap.activeScrollers) {
      console.log(`    - <${s.tag}> testid="${s.test}" cls="${s.cls}" maxH=${s.maxH} ${s.clientH}/${s.scrollH}px  "${s.firstText}"`);
    }
    if (scrollMap.activeScrollers.length === 0) {
      pass('No active scrollbars inside modal (form fits cleanly)');
    } else {
      // It's OK if the ONLY active scroller is the ValidationHistory wrapper
      // (its first text starts with "History (").
      const onlyHistory = scrollMap.activeScrollers.every((s) => s.firstText.startsWith('History ('));
      if (onlyHistory) pass(`Only History section scrolls — exactly what we wanted`);
      else fail(`Form area is scrolling, not just history. See scrollers above.`);
    }
  }

  await page.screenshot({ path: `${SHOTS}/04-validation-tab-final.png`, fullPage: false });
} catch (e) {
  console.error(`! FATAL: ${e.message}`);
  process.exitCode = 1;
  await page.screenshot({ path: `${SHOTS}/99-error.png`, fullPage: true }).catch(() => {});
} finally {
  if (consoleErrs.length > 0) {
    console.log(`\nBrowser console errors (${consoleErrs.length}):`);
    for (const e of consoleErrs) console.log(`  ${e}`);
  }
  await browser.close();
  console.log(`\nScreenshots: ${SHOTS}\n`);
}
