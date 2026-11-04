// Verifies the dashboard's hand-written API types match the backend's OpenAPI contract (openapi.json,
// kept in sync by an integration test). Fails if the API adds, removes or renames a field.
// No dependencies: reads the schema and the interfaces in src/types.ts directly.
import { readFileSync } from 'node:fs';

const openapi = JSON.parse(readFileSync(new URL('../openapi.json', import.meta.url)));
const types = readFileSync(new URL('../src/types.ts', import.meta.url), 'utf8');

// frontend interface → backend schema
const PAIRS = { Payment: 'PaymentDto', Refund: 'RefundDto' };

const interfaceFields = name => {
  const m = types.match(new RegExp(`export interface ${name} \\{([\\s\\S]*?)\\n\\}`));
  if (!m) throw new Error(`interface ${name} not found in src/types.ts`);
  return new Set([...m[1].matchAll(/^\s*(\w+)\??:/gm)].map(x => x[1]));
};

let failed = false;
for (const [ts, schema] of Object.entries(PAIRS)) {
  const s = openapi.components?.schemas?.[schema];
  if (!s) { console.error(`✗ schema ${schema} missing from openapi.json`); failed = true; continue; }
  const api = new Set(Object.keys(s.properties));
  const ui = interfaceFields(ts);
  const missing = [...api].filter(f => !ui.has(f));
  const extra = [...ui].filter(f => !api.has(f));
  if (missing.length || extra.length) {
    failed = true;
    console.error(`✗ ${ts} ≠ ${schema}` +
      (missing.length ? `\n    in API, missing in src/types.ts: ${missing.join(', ')}` : '') +
      (extra.length ? `\n    in src/types.ts, not in API: ${extra.join(', ')}` : ''));
  } else {
    console.log(`✓ ${ts} matches ${schema} (${api.size} fields)`);
  }
}
process.exit(failed ? 1 : 0);
