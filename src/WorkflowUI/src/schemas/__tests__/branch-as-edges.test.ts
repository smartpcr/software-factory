import { describe, it, expect } from 'vitest';
import { noopNodeSchema } from '../node-noop.schema';
import { csharpNodeSchema } from '../node-csharp.schema';
import { csharpScriptNodeSchemaBase } from '../node-csharp-script.schema';
import { csharpTaskNodeSchema } from '../node-csharp-task.schema';
import { powershellNodeSchema } from '../node-powershell.schema';
import { powershellTaskNodeSchema } from '../node-powershell-task.schema';
import { ifElseNodeSchema } from '../node-ifelse.schema';
import { switchNodeSchema } from '../node-switch.schema';
import { forEachNodeSchema } from '../node-foreach.schema';
import { whileNodeSchema } from '../node-while.schema';
import { timerNodeSchema } from '../node-timer.schema';

const bannedKeys = ['trueBranch', 'falseBranch', 'defaultCase', 'cases.route', 'bodyNodeId', 'exitNodeId', 'thenNodeId', 'elseNodeId'];

const allSchemas = [
  { name: 'Noop', schema: noopNodeSchema },
  { name: 'CSharp', schema: csharpNodeSchema },
  { name: 'CSharpScript', schema: csharpScriptNodeSchemaBase },
  { name: 'CSharpTask', schema: csharpTaskNodeSchema },
  { name: 'PowerShell', schema: powershellNodeSchema },
  { name: 'PowerShellTask', schema: powershellTaskNodeSchema },
  { name: 'IfElse', schema: ifElseNodeSchema },
  { name: 'Switch', schema: switchNodeSchema },
  { name: 'ForEach', schema: forEachNodeSchema },
  { name: 'While', schema: whileNodeSchema },
  { name: 'Timer', schema: timerNodeSchema },
];

describe('BranchAsEdges invariant', () => {
  for (const { name, schema } of allSchemas) {
    for (const banned of bannedKeys) {
      it(`${name} schema does not contain key '${banned}'`, () => {
        expect(Object.keys(schema.shape)).not.toContain(banned);
      });
    }
  }

  it('covers all 11 non-recursive schemas', () => {
    expect(allSchemas).toHaveLength(11);
  });
});
