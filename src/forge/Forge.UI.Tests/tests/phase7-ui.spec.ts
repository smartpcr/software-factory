import { expect, test } from '@playwright/test';

// Phase 7 UI smoke — palette, keyboard map, error boundary, empty state.

test.describe('Phase 7', () => {
  test('Cmd+K opens the command palette and Esc closes it', async ({ page }) => {
    await page.goto('/plan');
    await page.keyboard.press('ControlOrMeta+K');
    await expect(page.locator('.command-palette')).toBeVisible();
    await expect(page.locator('.cmd-input')).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(page.locator('.command-palette')).toHaveCount(0);
  });

  test('palette filters by query and Enter navigates', async ({ page }) => {
    await page.goto('/plan');
    await page.keyboard.press('ControlOrMeta+K');
    await page.locator('.cmd-input').fill('audit');
    const first = page.locator('.cmd-item').first();
    await expect(first).toContainText(/Audit/i);
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/\/audit$/);
  });

  test('? opens the keyboard map', async ({ page }) => {
    await page.goto('/plan');
    // Type ? as a character so react-hotkeys-hook sees the right key.
    await page.locator('body').click();
    await page.keyboard.type('?');
    await expect(page.locator('.keymap-modal')).toBeVisible();
    await expect(page.locator('.keymap-modal kbd', { hasText: 'Esc' })).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('.keymap-modal')).toHaveCount(0);
  });
});
