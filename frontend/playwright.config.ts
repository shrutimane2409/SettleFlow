import { defineConfig } from '@playwright/test';

// Browser tests against a running stack (`make up`). Uses the locally installed Chrome, so no browser download.
export default defineConfig({
  testDir: './e2e',
  timeout: 45_000,
  expect: { timeout: 20_000 },
  retries: process.env.CI ? 1 : 0,
  reporter: [['list']],
  use: {
    baseURL: process.env.BASE_URL ?? 'http://localhost:3000',
    channel: process.env.PW_CHANNEL ?? 'chrome',
    headless: true,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
});
