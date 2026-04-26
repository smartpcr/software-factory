import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Phase 0.18 — fidelity baseline.
// 1. Captures the prototype's topbar + sidebar (per theme) into baselines/.
// 2. Asserts the React render of the same regions matches each baseline
//    pixel-for-pixel (subject to a tiny diff ratio for font hinting).
//
// Phase 0 covers shell only — the page-head / view body land in later phases
// and have their own fidelity steps in the implementation plan.

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const baselinesDir = path.resolve(__dirname, '../baselines');
const prototypeUrl = 'file://' + path
  .resolve(__dirname, '../../../../docs/examples/forge-board.html')
  .replace(/\\/g, '/');

const themes = ['dark', 'light'] as const;
const regions = [
  { name: 'topbar',  selector: 'header.topbar' },
  { name: 'sidebar', selector: 'aside.sidebar' },
] as const;

fs.mkdirSync(baselinesDir, { recursive: true });

for (const theme of themes) {
  for (const region of regions) {
    const baselinePath = path.join(baselinesDir, `${region.name}.${theme}.png`);

    test(`${region.name} fidelity (${theme})`, async ({ page }) => {
      // (1) capture the prototype baseline.
      await page.goto(prototypeUrl);
      await page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
      await page.waitForLoadState('networkidle');
      await page.evaluate(() => document.fonts.ready);

      const protoShot = await page.locator(region.selector).screenshot();
      fs.writeFileSync(baselinePath, protoShot);

      // (2) assert the React app's rendering matches the baseline.
      await page.goto('/plan');
      await page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
      await page.waitForLoadState('networkidle');
      await page.evaluate(() => document.fonts.ready);

      const reactShot = await page.locator(region.selector).screenshot();
      expect(reactShot).toMatchSnapshot(`${region.name}.${theme}.png`, { maxDiffPixelRatio: 0.05 });
    });
  }
}
