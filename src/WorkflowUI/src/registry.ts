// -----------------------------------------------------------------------
// Node type registry and palette ordering
// Server is the single source of truth. Local fallback for offline use.
// -----------------------------------------------------------------------

import type { NodeTypeDefinition, PaletteSection } from './types';
import type { ServerNodeType } from './api';

/** The live node type registry — populated from server, with local fallback */
export const NodeTypes: Record<string, NodeTypeDefinition> = {};

/** Palette ordering — rebuilt when types are loaded */
export const PaletteOrder: PaletteSection[] = [];

// Fallback icon for types without a mapping
const fallbackIcon = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="10"/><path d="M8 12h8"/></svg>';

/** SVG icons mapped by RuntimeType on the client side */
const iconMap: Record<string, string> = {
  Start: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>',
  Trigger: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M13 2L3 14h9l-1 8 10-12h-9l1-8z"/></svg>',
  Timer: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/></svg>',
  Action: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="M9 12h6"/></svg>',
  HttpRequest: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="10"/><path d="M2 12h20M12 2a15 15 0 0 1 0 20M12 2a15 15 0 0 0 0 20"/></svg>',
  Transform: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M4 6h16M4 12h10M4 18h7"/></svg>',
  Delay: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/></svg>',
  Log: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6M16 13H8M16 17H8M10 9H8"/></svg>',
  Noop: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="10"/><path d="M8 12h8"/></svg>',
  End: '<svg viewBox="0 0 24 24" fill="currentColor"><rect x="5" y="5" width="14" height="14" rx="2"/></svg>',
  CSharp: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M16 18l2-2-2-2M8 18l-2-2 2-2M14 4l-4 16"/></svg>',
  CSharpScript: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M16 18l2-2-2-2M8 18l-2-2 2-2M14 4l-4 16"/></svg>',
  CSharpTask: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="M10 8l4 4-4 4"/></svg>',
  PowerShell: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M5 7l7 5-7 5M13 17h6"/></svg>',
  PowerShellTask: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M5 7l7 5-7 5M13 17h6"/></svg>',
  IfElse: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M7 4v6a4 4 0 0 0 4 4h6M7 4H3M17 14l3-3-3-3M17 14v6h-4M17 14h4"/></svg>',
  Switch: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 3v18M3 12h18M6 6l12 12M18 6 6 18"/></svg>',
  Parallel: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M8 3v4M8 17v4M16 3v4M16 17v4M4 10h4M4 14h4M16 10h4M16 14h4M8 7a3 3 0 0 1 3 3v4a3 3 0 0 1-3 3M16 7a3 3 0 0 0-3 3v4a3 3 0 0 0 3 3"/></svg>',
  ForEach: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M17 1l4 4-4 4M3 11V9a4 4 0 0 1 4-4h14M7 23l-4-4 4-4M21 13v2a4 4 0 0 1-4 4H3"/></svg>',
  While: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M23 4v6h-6M1 20v-6h6M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"/></svg>',
  AI: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 2a4 4 0 0 0-4 4v1H7a3 3 0 0 0-3 3v7a3 3 0 0 0 3 3h10a3 3 0 0 0 3-3v-7a3 3 0 0 0-3-3h-1V6a4 4 0 0 0-4-4z"/><circle cx="9" cy="13" r="1"/><circle cx="15" cy="13" r="1"/></svg>',
  Container: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="3" width="18" height="18" rx="2" stroke-dasharray="3 2"/></svg>',
  Subflow: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><path d="M3.27 6.96 12 12.01l8.73-5.05M12 22.08V12"/></svg>',
};

/** Color mapped by category on the client side */
const colorMap: Record<string, string> = {
  Workflow: 'var(--n-start)',
  Trigger: 'var(--n-start)',
  Actions: 'var(--n-action)',
  Code: 'var(--n-action)',
  'Flow Control': 'var(--n-if)',
  AI: 'var(--n-ai)',
  Containers: 'var(--n-container)',
};

/** Per-type color overrides */
const colorOverrides: Record<string, string> = {
  ForEach: 'var(--n-loop)',
  While: 'var(--n-loop)',
  Parallel: 'var(--n-loop)',
  Switch: 'var(--n-switch)',
  End: 'var(--n-end)',
  Subflow: 'var(--n-subflow)',
};

/**
 * Load all node types from the server response into the registry.
 * Completely replaces any existing types — server is the source of truth.
 */
export function loadServerNodeTypes(serverTypes: ServerNodeType[]): void {
  // Clear existing
  for (const key of Object.keys(NodeTypes)) delete NodeTypes[key];
  PaletteOrder.length = 0;

  const categoryTypes = new Map<string, string[]>();

  for (const st of serverTypes) {
    const key = st.runtimeType;

    const config: Record<string, string | number | boolean> = {};
    for (const prop of st.properties) {
      if (prop.default != null) {
        if (prop.type === 'number') config[prop.name] = parseFloat(prop.default);
        else if (prop.type === 'boolean') config[prop.name] = prop.default === 'true';
        else config[prop.name] = prop.default;
      } else if (prop.type === 'boolean') {
        config[prop.name] = false;
      } else if (prop.type === 'number') {
        config[prop.name] = 0;
      } else {
        config[prop.name] = '';
      }
    }

    const def: NodeTypeDefinition = {
      label: st.label,
      category: st.category,
      color: colorOverrides[key] || colorMap[st.category] || 'var(--n-action)',
      icon: iconMap[key] || fallbackIcon,
      desc: st.description,
      ports: { in: st.ports.in, out: st.ports.out, error: st.ports.error },
      config,
    };

    if (st.outputPorts.length > 0) {
      def.branches = [...st.outputPorts];
    }

    if (st.portLabels) {
      def.portLabels = {
        in: st.portLabels['in'],
        out: st.portLabels['out'],
        error: st.portLabels['error'] || 'onError',
        branches: {},
      };
      for (const port of st.outputPorts) {
        if (st.portLabels[port]) {
          def.portLabels.branches![port] = st.portLabels[port];
        }
      }
    }

    // Attach server property metadata for dynamic inspector rendering
    (def as NodeTypeDefinition & { serverProperties?: ServerNodeType['properties'] }).serverProperties = st.properties;

    NodeTypes[key] = def;
    // Also register lowercase alias for backward compatibility with existing workflows
    const lowerKey = key.toLowerCase();
    if (lowerKey !== key) {
      NodeTypes[lowerKey] = def;
    }

    // Build palette ordering by category
    if (!categoryTypes.has(st.category)) categoryTypes.set(st.category, []);
    categoryTypes.get(st.category)!.push(key);
  }

  // Build palette order from categories (in server-provided order)
  const categoryOrder = ['Workflow', 'Trigger', 'Actions', 'Code', 'Flow Control', 'AI', 'Containers', 'Utility'];
  for (const cat of categoryOrder) {
    const types = categoryTypes.get(cat);
    if (types?.length) {
      PaletteOrder.push({ category: cat, types });
      categoryTypes.delete(cat);
    }
  }
  // Any remaining categories not in the predefined order
  for (const [cat, types] of categoryTypes) {
    if (types.length) PaletteOrder.push({ category: cat, types });
  }

  buildCanonicalKeyMap();

  // Register legacy aliases for old workflow compatibility
  const legacyAliases: Record<string, string> = {
    'http': 'HttpRequest',
    'if': 'IfElse',
    'transform': 'Transform',
    'foreach': 'ForEach',
  };
  for (const [alias, canonical] of Object.entries(legacyAliases)) {
    if (NodeTypes[canonical] && !NodeTypes[alias]) {
      NodeTypes[alias] = NodeTypes[canonical];
      canonicalKeys.set(alias, canonical);
    }
  }
}

/**
 * Load the built-in fallback node types (used when server is unavailable).
 */
export function loadFallbackNodeTypes(): void {
  if (Object.keys(NodeTypes).length > 0) return; // Already loaded

  const fallbackTypes: Record<string, NodeTypeDefinition> = {
    start: {
      label: 'Start', category: 'Trigger',
      color: 'var(--n-start)',
      icon: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>',
      desc: 'Workflow entry point',
      ports: { in: false, out: true, error: false },
      config: { triggerType: 'Manual', schedule: '', eventName: '' }
    },
    action: {
      label: 'Action', category: 'Actions',
      color: 'var(--n-action)',
      icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="M9 12h6"/></svg>',
      desc: 'Execute a task',
      ports: { in: true, out: true, error: true },
      config: { actionType: 'function', handler: '' }
    },
    end: {
      label: 'End', category: 'Flow Control',
      color: 'var(--n-end)',
      icon: '<svg viewBox="0 0 24 24" fill="currentColor"><rect x="5" y="5" width="14" height="14" rx="2"/></svg>',
      desc: 'Workflow terminator',
      ports: { in: true, out: false, error: false },
      config: { status: 'success' }
    },
  };

  Object.assign(NodeTypes, fallbackTypes);
  PaletteOrder.push(
    { category: 'Trigger', types: ['start'] },
    { category: 'Actions', types: ['action'] },
    { category: 'Flow Control', types: ['end'] },
  );
}

/** Map from lowercase to canonical (server) key for type normalization */
const canonicalKeys = new Map<string, string>();

/** Called after loadServerNodeTypes to build the canonical key map */
function buildCanonicalKeyMap(): void {
  canonicalKeys.clear();
  for (const key of Object.keys(NodeTypes)) {
    canonicalKeys.set(key.toLowerCase(), key);
  }
}

/**
 * Resolve a node type string to its canonical registry key.
 * Handles case mismatches (e.g. 'foreach' → 'ForEach', 'start' → 'Start').
 */
export function resolveNodeType(type: string): string {
  if (NodeTypes[type]) return type;
  return canonicalKeys.get(type.toLowerCase()) || type;
}

/**
 * Normalize all node types in a workflow to use canonical casing.
 * Call after loading a workflow from storage or server.
 */
export function normalizeWorkflowNodeTypes(nodes: import('./types').FlowNode[]): void {
  for (const node of nodes) {
    const canonical = resolveNodeType(node.runtimeType);
    if (canonical !== node.runtimeType) {
      node.runtimeType = canonical;
    }
  }
}
