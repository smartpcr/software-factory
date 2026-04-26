import { expect, test } from '@playwright/test';

// During an active drag, dnd-kit's <DragOverlay> renders a faint clone of the
// card that follows the cursor, so the user can see what they're moving.
test('drag overlay renders a faint clone while dragging', async ({ page }) => {
  await page.goto('/plan');
  await page.waitForSelector('main.main .card');

  const source = page.locator('main.main .card[data-id="#234"]').first();
  await source.scrollIntoViewIfNeeded();
  const sb = (await source.boundingBox())!;

  await page.mouse.move(sb.x + sb.width / 2, sb.y + sb.height / 2);
  await page.mouse.down();
  // Move past the activation distance so dnd-kit promotes pointer-down to a drag.
  await page.mouse.move(sb.x + 60, sb.y + 60, { steps: 10 });

  // The overlay clone is rendered in a portal at the document root with the
  // same .card markup wrapped in .drag-overlay.
  const overlay = page.locator('.drag-overlay .card');
  await expect(overlay).toBeVisible();
  await expect(overlay).toHaveAttribute('data-id', '#234');

  await page.mouse.up();
  // After drop, the overlay disappears.
  await expect(overlay).toHaveCount(0);
});
