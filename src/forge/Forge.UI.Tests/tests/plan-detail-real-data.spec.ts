import { test, expect } from '@playwright/test';

// Verify the Plan & Design DetailPanel no longer shows the prototype's
// hard-coded "Token rotation" reasoning trace, and "Assign to agent or
// team" deep-links to /work-items with the right id focused.

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem('forge.activeProductId', 'update');
  });
});

test('Plan & Design detail panel shows REAL description, not prototype filler', async ({ page }) => {
  await page.goto('/plan');
  await page.waitForTimeout(1500);

  // Click the first card on the board.
  const firstCard = page.locator('.board .card[data-id]').first();
  await firstCard.click();
  await page.waitForTimeout(500);

  // The legacy fake "Token rotation" content from the prototype must NOT
  // appear in the description block (it was hard-coded into STATIC_DESC).
  const descriptionText = await page.locator('.detail-desc').textContent().catch(() => '');
  expect(descriptionText, 'description must not be the prototype filler').not.toContain(
    'Implement automatic OIDC token rotation with key rollover. The handler should rotate',
  );

  // The fake "Read AC + parent epic. Identified 4 sub-concerns: rotation timer..."
  // reasoning trace must NOT appear either — it was tied to the prototype work item.
  const bodyText = await page.locator('aside.detail-panel').textContent().catch(() => '');
  expect(bodyText, 'reasoning trace must not be the prototype filler').not.toContain(
    'Read AC + parent epic. Identified 4 sub-concerns: rotation timer',
  );
});

test('"Assign to agent or team" navigates to /work-items?focus={id} and opens the detail panel', async ({ page }) => {
  await page.goto('/plan');
  await page.waitForTimeout(1500);

  // Click the first backlog card so DetailActions renders the
  // "Assign to agent or team" path.
  const backlogCard = page.locator('.card[data-col="backlog"]').first();
  if (!(await backlogCard.isVisible().catch(() => false))) {
    test.skip(true, 'no backlog card to test against');
  }
  const cardId = await backlogCard.getAttribute('data-id');
  await backlogCard.click();
  await page.waitForTimeout(300);

  const assignBtn = page.locator('button:has-text("Assign to agent or team")').first();
  await expect(assignBtn, 'assign button should exist on backlog cards').toBeVisible();
  await assignBtn.click();

  await expect(page).toHaveURL(/\/work-items\?focus=/, { timeout: 5000 });

  // The work item detail panel should auto-open for that id.
  await page.waitForTimeout(800);
  const panel = page.locator('.wit-detail').first();
  await expect(panel).toBeVisible();
  if (cardId) {
    const panelText = await panel.textContent().catch(() => '');
    expect(panelText, 'panel should show the focused work item id').toContain(cardId);
  }
});
