/**
 * Generates src/schema.json from the programmatic schema definition.
 *
 * Run via: npx tsx scripts/generate-schema.ts
 */
import { writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { graphMemorySchema } from '../src/schema.js';

const __dirname = dirname(fileURLToPath(import.meta.url));
const outPath = resolve(__dirname, '..', 'src', 'schema.json');

writeFileSync(outPath, JSON.stringify(graphMemorySchema, null, 2) + '\n', 'utf-8');

console.log(`Wrote ${outPath}`);
