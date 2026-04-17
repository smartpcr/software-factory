import { describe, it, expect } from 'vitest';
import { ESLint } from 'eslint';
import * as path from 'path';

describe('ESLint no-direct-generated-import', () => {
  it('forbids direct import of engine-contract.generated', async () => {
    const eslint = new ESLint({
      overrideConfigFile: path.resolve(__dirname, '../../.eslintrc.cjs'),
      cwd: path.resolve(__dirname, '../..'),
    });

    const results = await eslint.lintText(
      "import { RuntimeType } from './engine-contract.generated';\n",
      { filePath: 'src/foo.ts' },
    );

    const messages = results[0]?.messages ?? [];
    const restricted = messages.find(
      (m) => m.ruleId === 'no-restricted-imports',
    );
    expect(restricted).toBeDefined();
    expect(restricted?.severity).toBe(2); // error level
    expect(restricted?.message).toContain('facade');
  });

  it('allows facade file to import engine-contract.generated', async () => {
    const eslint = new ESLint({
      overrideConfigFile: path.resolve(__dirname, '../../.eslintrc.cjs'),
      cwd: path.resolve(__dirname, '../..'),
    });

    const results = await eslint.lintText(
      "import type { RuntimeType } from './engine-contract.generated';\n",
      { filePath: 'src/engine-contract.ts' },
    );

    const messages = results[0]?.messages ?? [];
    const restricted = messages.filter(
      (m) => m.ruleId === 'no-restricted-imports',
    );
    expect(restricted).toHaveLength(0);
  });
});
