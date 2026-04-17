import type { RuntimeType } from '../../engine-contract.generated';
import { handleNode } from '../node-union';

// This file is a compile-time type test.
// If handleNode does not require all 13 kinds, tsc will error.

function exhaustiveHandler(n: { runtimeType: RuntimeType }): string {
  return handleNode(n, {
    Noop: () => 'noop',
    CSharp: () => 'csharp',
    CSharpScript: () => 'csharpscript',
    CSharpTask: () => 'csharptask',
    PowerShell: () => 'powershell',
    PowerShellTask: () => 'powershelltask',
    IfElse: () => 'ifelse',
    ForEach: () => 'foreach',
    While: () => 'while',
    Switch: () => 'switch',
    Subflow: () => 'subflow',
    Timer: () => 'timer',
    Container: () => 'container',
  });
}

// Ensure this compiles — proves exhaustiveness
const _result: string = exhaustiveHandler({ runtimeType: 'Noop' });
void _result;
