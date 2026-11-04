import { expect, test, type Page } from '@playwright/test';

const ADMIN = { username: 'admin', password: process.env.ADMIN_PASSWORD ?? 'admin-change-me' };
const DEMO_PASSWORD = process.env.DEMO_PASSWORD ?? 'settleflow-demo';

async function login(page: Page, username: string, password: string) {
  await page.goto('/');
  await page.getByLabel('Username').fill(username);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

test.beforeEach(async ({ page }) => {
  // Any uncaught error in the browser fails the test.
  page.on('pageerror', err => {
    throw err;
  });
});

test('wrong password is rejected', async ({ page }) => {
  await login(page, 'admin', 'definitely-wrong');
  await expect(page.getByText('Invalid username or password.')).toBeVisible();
});

test('admin creates a payment and watches it settle live', async ({ page }) => {
  await login(page, ADMIN.username, ADMIN.password);
  await expect(page.getByRole('heading', { name: 'Payment Operations' })).toBeVisible();
  await expect(page.getByText('Live', { exact: true })).toBeVisible();

  // "Low risk" scenario, unique customer so the velocity rule doesn't interfere.
  await page.getByRole('button', { name: 'Low risk' }).click();
  await page.getByLabel('Customer').fill(`cust_pw_${Date.now()}`);
  await page.getByRole('button', { name: 'Submit payment' }).click();
  const accepted = page.getByText(/Accepted [0-9a-f]{8}…/);
  await expect(accepted).toBeVisible();
  const shortId = (await accepted.textContent())!.match(/Accepted ([0-9a-f]{8})/)![1];

  // The row appears and turns Settled without a reload (Server-Sent Events).
  const row = page.locator('button.tr.row', { hasText: shortId });
  await expect(row).toBeVisible();
  await expect(row.getByText('Settled')).toBeVisible();

  // The drawer shows the full timeline.
  await row.click();
  await expect(page.locator('.timeline')).toContainText('FraudScored');
  await expect(page.locator('.timeline')).toContainText('Settled');
});

test('viewer gets a read-only dashboard', async ({ page }) => {
  await login(page, 'viewer', DEMO_PASSWORD);
  await expect(page.getByText('Your role (viewer) is read-only.')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Merchants' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Audit log' })).toHaveCount(0);
});

test('every page renders without errors', async ({ page }) => {
  await login(page, ADMIN.username, ADMIN.password);
  for (const [route, heading] of [
    ['analytics', 'Analytics'],
    ['payments', 'Payments'],
    ['review', 'Review queue'],
    ['ledger', 'Ledger'],
    ['payouts', 'Payouts'],
    ['reconciliation', 'Reconciliation'],
    ['fraud', 'Fraud rules'],
    ['dlq', 'Dead letters'],
    ['webhooks', 'Webhooks'],
    ['merchants', 'Merchants'],
    ['users', 'Users'],
    ['audit', 'Audit log'],
  ]) {
    await page.goto(`/#/${route}`);
    await expect(page.getByRole('heading', { level: 1, name: heading })).toBeVisible();
  }
});
