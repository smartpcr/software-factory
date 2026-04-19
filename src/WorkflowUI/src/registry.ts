// -----------------------------------------------------------------------
// Node type registry and palette ordering
// -----------------------------------------------------------------------

import type { NodeTypeDefinition, PaletteSection } from './types';

export const NodeTypes: Record<string, NodeTypeDefinition> = {
  start: {
    label: 'Start', category: 'Trigger',
    color: 'var(--n-start)',
    icon: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>',
    desc: 'Workflow entry point',
    ports: { in: false, out: true, error: false },
    config: { triggerType: 'manual', schedule: '0 0 * * *' }
  },
  trigger: {
    label: 'Webhook Trigger', category: 'Trigger',
    color: 'var(--n-start)',
    icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M13 2L3 14h9l-1 8 10-12h-9l1-8z"/></svg>',
    desc: 'HTTP webhook entry',
    ports: { in: false, out: true, error: false },
    config: { method: 'POST', path: '/hook' }
  },
  action: {
    label: 'Action', category: 'Actions',
    color: 'var(--n-action)',
    icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="M9 12h6"/></svg>',
    desc: 'Execute a task',
    ports: { in: true, out: true, error: true },
    config: { actionType: 'function', handler: '' }
  },
  http: {
    label: 'HTTP Request', category: 'Actions',
    color: 'var(--n-action)',
    icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="10"/><path d="M2 12h20M12 2a15 15 0 0 1 0 20M12 2a15 15 0 0 0 0 20"/></svg>',
    desc: 'Call external API',
    ports: { in: true, out: true, error: true },
    config: { method: 'GET', url: 'https://api.example.com', headers: '{}' }
  },
  transform: {
    label: 'Transform', category: 'Actions',
    color: 'var(--n-action)',
    icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M4 6h16M4 12h10M4 18h7"/></svg>',
    desc: 'Map or transform data',
    ports: { in: true, out: true, error: true },
    config: { expression: 'input' }
  },
  delay: {
    label: 'Delay', category: 'Actions',
    color: 'var(--n-action)',
    icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/></svg>',
    desc: 'Pause for duration',
    ports: { in: true, out: true, error: false },
    config: { duration: '1000', unit: 'ms' }
  },
  if: {
    label: 'If / Else', category: 'Flow Control',
    color: 'var(--n-if)',
    icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M7 4v6a4 4 0 0 0 4 4h6M7 4H3M17 14l3-3-3-3M17 14v6h-4M17 14h4"/></svg>',
    desc: 'Conditional branching',
    ports: { in: true, out: false, error: true },
    branches: ['true', 'false'],
    config: { condition: '' }
  },
  switch: {
    label: 'Switch', category: 'Flow Control',
    color: 'var(--n-switch)',
    icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 3v18M3 12h18M6 6l12 12M18 6 6 18"/></svg>',
    desc: 'Multi-case routing',
    ports: { in: true, out: false, error: true },
    branches: ['case_a', 'case_b', 'default'],
    config: { expression: '' }
  },
  parallel: {
    label: 'Parallel', category: 'Flow Control',
    color: 'var(--n-loop)',
    icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M8 3v4M8 17v4M16 3v4M16 17v4M4 10h4M4 14h4M16 10h4M16 14h4M8 7a3 3 0 0 1 3 3v4a3 3 0 0 1-3 3M16 7a3 3 0 0 0-3 3v4a3 3 0 0 0 3 3"/></svg>',
    desc: 'Fan-out parallel branches',
    ports: { in: true, out: false, error: true },
    branches: ['branch_1', 'branch_2'],
    config: { joinMode: 'all', timeout: 30000 }
  },
  loop: {
    label: 'Loop', category: 'Flow Control',
    color: 'var(--n-loop)',
    icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M17 1l4 4-4 4M3 11V9a4 4 0 0 1 4-4h14M7 23l-4-4 4-4M21 13v2a4 4 0 0 1-4 4H3"/></svg>',
    desc: 'Iterate over items',
    ports: { in: true, out: false, error: true },
    branches: ['iterate', 'complete'],
    config: { source: 'input.items', maxIterations: 100, mode: 'foreach' }
  },
  container: {
    label: 'Group', category: 'Containers',
    color: 'var(--n-container)',
    icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="3" width="18" height="18" rx="2" stroke-dasharray="3 2"/></svg>',
    desc: 'Collapsible group',
    ports: { in: true, out: true, error: true },
    config: { label: 'Group' }
  },
  subflow: {
    label: 'Subflow', category: 'Containers',
    color: 'var(--n-subflow)',
    icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><path d="M3.27 6.96 12 12.01l8.73-5.05M12 22.08V12"/></svg>',
    desc: 'Reference another workflow',
    ports: { in: true, out: true, error: true },
    config: { workflowId: '', inputMapping: '{}', waitForCompletion: true }
  },
  ai: {
    label: 'AI', category: 'AI',
    color: 'var(--n-ai)',
    icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 2a4 4 0 0 0-4 4v1H7a3 3 0 0 0-3 3v7a3 3 0 0 0 3 3h10a3 3 0 0 0 3-3v-7a3 3 0 0 0-3-3h-1V6a4 4 0 0 0-4-4z"/><circle cx="9" cy="13" r="1"/><circle cx="15" cy="13" r="1"/></svg>',
    desc: 'AI skill or subagent',
    ports: { in: true, out: true, error: true },
    config: {
      mode: 'skill',
      skillId: '',
      subagentId: '',
      model: 'claude-opus-4-7',
      systemPrompt: '',
      inputMapping: 'input',
      outputKey: 'result',
      temperature: 0.7,
      maxTokens: 4096
    }
  },
  log: {
    label: 'Log', category: 'Actions',
    color: 'var(--n-action)',
    icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6M16 13H8M16 17H8M10 9H8"/></svg>',
    desc: 'Output a log message',
    ports: { in: true, out: true, error: false },
    config: { level: 'info', message: '' }
  },
  end: {
    label: 'End', category: 'Flow Control',
    color: 'var(--n-end)',
    icon: '<svg viewBox="0 0 24 24" fill="currentColor"><rect x="5" y="5" width="14" height="14" rx="2"/></svg>',
    desc: 'Workflow terminator',
    ports: { in: true, out: false, error: false },
    config: { status: 'success' }
  }
};

export const PaletteOrder: PaletteSection[] = [
  { category: 'Trigger', types: ['start', 'trigger'] },
  { category: 'Actions', types: ['action', 'http', 'transform', 'delay', 'log'] },
  { category: 'Flow Control', types: ['if', 'switch', 'parallel', 'loop', 'end'] },
  { category: 'AI', types: ['ai'] },
  { category: 'Containers', types: ['container', 'subflow'] },
];
