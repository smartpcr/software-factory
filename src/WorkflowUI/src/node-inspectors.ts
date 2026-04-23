// -----------------------------------------------------------------------
// Node-type-specific inspector renderers.
// Each renderer returns HTML for its node type's configuration panel.
// -----------------------------------------------------------------------

import type { FlowNode, NodeTypeDefinition } from './types';
import type { InboundField } from './inboundSchema';
import type { AIRegistryItem, AIInputParam, AIOutputField } from './api';
import type { AIModel } from './config';

export interface InspectorContext {
  node: FlowNode;
  id: string;
  c: Record<string, unknown>;
  escapeHtml: (s: string) => string;
  inboundFields: InboundField[];
  renderInboundFields: (fields: InboundField[], nodeId: string, placeholders: string[], bindings: Record<string, string>) => string;
  describeCron: (cron: string) => string;
  validateCron: (cron: string) => string | null;
  ai?: {
    skills: AIRegistryItem[];
    agents: AIRegistryItem[];
    inputParams: AIInputParam[];
    inputParamsKey: string;
    models: AIModel[];
    tierColor: (tier: AIModel['tier']) => string;
    scenarios?: Record<string, { description?: string; inputs: AIInputParam[]; outputs: AIOutputField[] }>;
    defaultScenario?: string;
  };
  workflows?: Record<string, { workflowId: string; workflowName: string }>;
  /** Server-side workflow summaries for subflow dropdown */
  serverWorkflows?: { id: string; workflowId?: string; workflowName?: string }[];
  currentWfId?: string;
  nodeType?: NodeTypeDefinition & { serverProperties?: { name: string; label: string; type: string; required: boolean; default: string | null; placeholder: string | null; options: string[] | null }[] };
}

export interface NodeInspectorResult {
  title: string;
  html: string;
  /** If true, configFieldsHtml appends a generic inbound fields panel. Default: true for nodes with input ports. */
  includeInboundPanel: boolean;
}

// ---------------------------------------------------------------------------
// Individual renderers
// ---------------------------------------------------------------------------

function renderStart(ctx: InspectorContext): NodeInspectorResult {
  const { id, c, escapeHtml, describeCron, validateCron } = ctx;
  const trigType = String(c.triggerType || 'Manual');
  const cronDesc = trigType === 'Schedule' && c.schedule ? describeCron(String(c.schedule)) : '';
  const cronValid = trigType === 'Schedule' ? validateCron(String(c.schedule || '')) : null;

  const html = `
    <div class="ins-field">
      <label class="ins-field-label">Trigger type</label>
      <select data-config-key="triggerType" data-node-id="${id}">
        <option value="Manual" ${trigType === 'Manual' ? 'selected' : ''}>Manual</option>
        <option value="Schedule" ${trigType === 'Schedule' ? 'selected' : ''}>Schedule</option>
        <option value="Event" ${trigType === 'Event' ? 'selected' : ''}>Event</option>
        <option value="WebHook" ${trigType === 'WebHook' ? 'selected' : ''}>WebHook</option>
      </select>
    </div>
    ${trigType === 'Schedule' ? `
    <div class="ins-field">
      <label class="ins-field-label">Cron schedule ${cronValid ? '<span class="ins-validation-err" data-tooltip="' + escapeHtml(cronValid) + '">⚠</span>' : '<span class="ins-validation-ok">✓</span>'}</label>
      <input class="mono" value="${escapeHtml(String(c.schedule || ''))}" placeholder="0 */5 * * *" data-config-key="schedule" data-node-id="${id}" />
      ${cronDesc ? `<div class="ins-cron-friendly">${escapeHtml(cronDesc)}</div>` : ''}
      <div class="ins-field-hint">
        Format: minute hour day month weekday<br>
        <code>*</code> = any &nbsp; <code>*/5</code> = every 5 &nbsp; <code>1,15</code> = 1st and 15th
      </div>
      <div class="ins-cron-presets">
        <button class="ins-cron-preset" data-preset="* * * * *" data-node-id="${id}">Every min</button>
        <button class="ins-cron-preset" data-preset="*/5 * * * *" data-node-id="${id}">Every 5m</button>
        <button class="ins-cron-preset" data-preset="0 * * * *" data-node-id="${id}">Hourly</button>
        <button class="ins-cron-preset" data-preset="0 0 * * *" data-node-id="${id}">Daily</button>
        <button class="ins-cron-preset" data-preset="0 0 * * 1" data-node-id="${id}">Weekly</button>
      </div>
    </div>` : ''}
    ${trigType === 'Event' ? `
    <div class="ins-field">
      <label class="ins-field-label">Event name</label>
      <input class="mono" value="${escapeHtml(String(c.eventName || ''))}" placeholder="order.created" data-config-key="eventName" data-node-id="${id}" />
      <div class="ins-field-hint">Name of the external event to listen for.</div>
    </div>` : ''}
    ${trigType === 'WebHook' ? `
    <div class="ins-field">
      <label class="ins-field-label">Method</label>
      <select data-config-key="webHookMethod" data-node-id="${id}">
        <option value="POST" ${String(c.webHookMethod || 'POST') === 'POST' ? 'selected' : ''}>POST</option>
        <option value="PUT" ${String(c.webHookMethod) === 'PUT' ? 'selected' : ''}>PUT</option>
        <option value="PATCH" ${String(c.webHookMethod) === 'PATCH' ? 'selected' : ''}>PATCH</option>
      </select>
    </div>
    <div class="ins-field">
      <label class="ins-field-label">Route</label>
      <div style="display:flex; align-items:center; gap:0;">
        <span class="ins-field-hint" style="white-space:nowrap; padding:4px 6px; background:var(--bg-2); border:1px solid var(--border); border-right:none; border-radius:4px 0 0 4px; font-size:12px;">/webhook/</span>
        <input class="mono" style="border-radius:0 4px 4px 0;" value="${escapeHtml(String(c.webHookRoute || ''))}" placeholder="my-workflow" data-config-key="webHookRoute" data-node-id="${id}" />
      </div>
    </div>
    <div class="ins-field">
      <label class="ins-field-label">Body JSON Schema</label>
      <div class="ins-monaco-container" id="monaco-${id}-bodyJsonSchema" data-node-id="${id}" data-config-key="bodyJsonSchema" data-lang="json"></div>
      <div class="ins-field-hint">JSON Schema describing the expected request body. Output messageType is the parsed JSON object.</div>
    </div>` : ''}
  `;
  return { title: 'Start Trigger', html, includeInboundPanel: false };
}

function renderTrigger(ctx: InspectorContext): NodeInspectorResult {
  const { id, c, escapeHtml } = ctx;
  let params: { name: string; value: string }[] = [];
  try { params = JSON.parse(String(c.params || '[]')); } catch { /* ignore */ }
  const html = `
    <div class="ins-field" style="display:flex; gap:8px;">
      <div style="width: 90px">
        <label class="ins-field-label">Method</label>
        <select data-config-key="method" data-node-id="${id}">
          <option ${c.method === 'GET' ? 'selected' : ''}>GET</option>
          <option ${c.method === 'POST' ? 'selected' : ''}>POST</option>
          <option ${c.method === 'PUT' ? 'selected' : ''}>PUT</option>
          <option ${c.method === 'PATCH' ? 'selected' : ''}>PATCH</option>
          <option ${c.method === 'DELETE' ? 'selected' : ''}>DELETE</option>
        </select>
      </div>
      <div style="flex:1">
        <label class="ins-field-label">Route URI</label>
        <input class="mono" value="${escapeHtml(String(c.route || ''))}" placeholder="/webhook/my-hook" data-config-key="route" data-node-id="${id}" />
      </div>
    </div>
    <div class="ins-field">
      <label class="ins-field-label">Parameters</label>
      <div class="switch-cases-list" id="triggerParams_${id}">
        ${params.map((p, i) => `
          <div class="switch-case" style="display:flex; gap:4px; align-items:center;">
            <input value="${escapeHtml(p.name)}" placeholder="name" class="mono" style="flex:1" data-param-name data-node-id="${id}" data-param-index="${i}" />
            <input value="${escapeHtml(p.value)}" placeholder="value" class="mono" style="flex:1" data-param-value data-node-id="${id}" data-param-index="${i}" />
            <button class="switch-case-del" data-action="remove-trigger-param" data-node-id="${id}" data-param-index="${i}">
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M18 6 6 18M6 6l12 12"/></svg>
            </button>
          </div>
        `).join('')}
      </div>
      <button class="add-case-btn" data-action="add-trigger-param" data-node-id="${id}">+ Add parameter</button>
      <div class="ins-field-hint">Query or body parameters passed to the webhook.</div>
    </div>
  `;
  return { title: 'Webhook', html, includeInboundPanel: false };
}

function renderIfElse(ctx: InspectorContext): NodeInspectorResult {
  const { id, c, inboundFields, renderInboundFields } = ctx;
  const condEditorId = `monaco-${id}-condition`;
  const html = `
    <div class="ins-field">
      <label class="ins-field-label">Condition expression <span class="ins-field-hint">(C# — must return bool)</span></label>
      <div class="ins-monaco-container ins-monaco-sm" id="${condEditorId}" data-node-id="${id}" data-config-key="condition"></div>
      <div class="ins-field-hint">Use <code>{varName}</code> to reference input fields or global variables.</div>
    </div>
    ${inboundFields.length > 0 ? `
    <div class="ins-field">
      <label class="ins-field-label">Available fields</label>
      <div class="inbound-fields-panel">
        ${renderInboundFields(inboundFields, id, [], {})}
      </div>
      <div class="ins-field-hint">Click a field to copy its name.</div>
    </div>` : ''}
  `;
  return { title: 'Condition', html, includeInboundPanel: false };
}

function renderSwitch(ctx: InspectorContext): NodeInspectorResult {
  const { node, id, c, escapeHtml, inboundFields, renderInboundFields } = ctx;
  const html = `
    <div class="ins-field">
      <label class="ins-field-label">Switch expression</label>
      <textarea data-config-key="expression" data-node-id="${id}" placeholder="input.type">${escapeHtml(String(c.expression || ''))}</textarea>
    </div>
    <div class="ins-field">
      <label class="ins-field-label">Cases</label>
      <div class="switch-cases-list">
        ${(node.branches || []).map((b, i) => `
          <div class="switch-case">
            <input value="${escapeHtml(b)}" data-branch-rename data-node-id="${id}" data-branch-index="${i}" class="mono" />
            <button class="switch-case-del" data-action="remove-branch" data-node-id="${id}" data-branch-index="${i}">
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M18 6 6 18M6 6l12 12"/></svg>
            </button>
          </div>
        `).join('')}
      </div>
      <button class="add-case-btn" data-action="add-branch" data-node-id="${id}">+ Add case</button>
    </div>
    ${inboundFields.length > 0 ? `
    <div class="ins-field">
      <label class="ins-field-label">Available fields</label>
      <div class="inbound-fields-panel">
        ${renderInboundFields(inboundFields, id, [], {})}
      </div>
    </div>` : ''}
  `;
  return { title: 'Switch', html, includeInboundPanel: false };
}

function renderParallel(ctx: InspectorContext): NodeInspectorResult {
  const { node, id, c, escapeHtml } = ctx;
  const html = `
    <div class="ins-field">
      <label class="ins-field-label">Join mode</label>
      <select data-config-key="joinMode" data-node-id="${id}">
        <option ${c.joinMode === 'all' ? 'selected' : ''} value="all">all — wait for all</option>
        <option ${c.joinMode === 'any' ? 'selected' : ''} value="any">any — first to finish</option>
        <option ${c.joinMode === 'race' ? 'selected' : ''} value="race">race — cancel others</option>
      </select>
    </div>
    <div class="ins-field">
      <label class="ins-field-label">Timeout (ms)</label>
      <input type="number" value="${c.timeout || 30000}" data-config-key="timeout" data-node-id="${id}" />
    </div>
    <div class="ins-field">
      <label class="ins-field-label">Branches</label>
      <div class="switch-cases-list">
        ${(node.branches || []).map((b, i) => `
          <div class="switch-case">
            <input value="${escapeHtml(b)}" data-branch-rename data-node-id="${id}" data-branch-index="${i}" class="mono" />
            <button class="switch-case-del" data-action="remove-branch" data-node-id="${id}" data-branch-index="${i}">
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M18 6 6 18M6 6l12 12"/></svg>
            </button>
          </div>
        `).join('')}
      </div>
      <button class="add-case-btn" data-action="add-branch" data-node-id="${id}">+ Add branch</button>
    </div>
  `;
  return { title: 'Parallel', html, includeInboundPanel: false };
}

function renderForEach(ctx: InspectorContext): NodeInspectorResult {
  const { id, c, escapeHtml, inboundFields } = ctx;
  const collectionVar = String(c.collectionVariable || '');
  const itemVar = String(c.itemVariable || 'item');
  const fieldOptions = inboundFields.map(f =>
    `<option value="${escapeHtml(f.path)}" ${collectionVar === f.path ? 'selected' : ''}>${escapeHtml(f.name)} · ${escapeHtml(f.sourceNode)}</option>`
  ).join('');
  const html = `
    <div class="ins-field">
      <label class="ins-field-label">Collection variable</label>
      <select data-config-key="collectionVariable" data-node-id="${id}">
        <option value="">— select field —</option>
        ${fieldOptions}
      </select>
    </div>
    <div class="ins-field">
      <label class="ins-field-label">Item variable</label>
      <input class="mono" value="${escapeHtml(itemVar)}" data-config-key="itemVariable" data-node-id="${id}" placeholder="item" />
      <div class="ins-field-hint">Available to child nodes as <code>{${escapeHtml(itemVar)}}</code></div>
    </div>
    <div class="ins-field">
      <label class="ins-field-label">Max iterations (safety)</label>
      <input type="number" value="${c.maxIterations || 100}" data-config-key="maxIterations" data-node-id="${id}" />
    </div>
  `;
  return { title: 'ForEach', html, includeInboundPanel: false };
}

function renderSubflow(ctx: InspectorContext): NodeInspectorResult {
  const { id, c, escapeHtml, serverWorkflows, currentWfId } = ctx;
  const currentVal = String(c.workflowId || '');
  const wfOptions = (serverWorkflows || [])
    .filter(w => w.workflowId !== currentWfId && w.id !== currentWfId)
    .map(w => {
      const val = w.workflowId || w.id;
      const label = w.workflowName || w.id;
      return `<option value="${escapeHtml(val)}" ${currentVal === val ? 'selected' : ''}>${escapeHtml(label)}</option>`;
    })
    .join('');
  const html = `
    <div class="ins-field">
      <label class="ins-field-label">Workflow</label>
      <select data-config-key="workflowId" data-node-id="${id}">
        <option value="">— select a workflow —</option>
        ${wfOptions}
      </select>
    </div>
    <div class="ins-field">
      <label class="ins-field-label">Input mapping (JSON)</label>
      <textarea data-config-key="inputMapping" data-node-id="${id}">${escapeHtml(String(c.inputMapping || '{}'))}</textarea>
    </div>
    <div class="ins-field">
      <label style="display:flex; align-items:center; gap:6px; font-size:12px;">
        <input type="checkbox" ${c.waitForCompletion ? 'checked' : ''} data-config-key="waitForCompletion" data-node-id="${id}" style="width:auto" />
        Wait for subflow completion
      </label>
    </div>
  `;
  return { title: 'Subflow Reference', html, includeInboundPanel: false };
}

function renderAI(ctx: InspectorContext): NodeInspectorResult {
  const { node, id, c, escapeHtml, inboundFields, renderInboundFields, ai } = ctx;
  const models = ai?.models || [];
  const tColor = ai?.tierColor || (() => '');
  const skills = ai?.skills || [];
  const agents = ai?.agents || [];
  const scenarios = ai?.scenarios;
  const scenarioNames = scenarios ? Object.keys(scenarios) : [];
  const hasScenarios = scenarioNames.length > 0;

  // Resolve active scenario and derive inputs from it
  const selectedScenario = hasScenarios
    ? String(c.scenario || ai?.defaultScenario || scenarioNames[0])
    : '';
  let inputParams: AIInputParam[];
  const inputParamsKey = ai?.inputParamsKey || '';
  if (hasScenarios && selectedScenario && scenarios![selectedScenario]) {
    inputParams = scenarios![selectedScenario].inputs || [];
  } else {
    inputParams = ai?.inputParams || [];
  }

  const mode = String(c.mode || 'skill').toLowerCase();
  const backend = String(c.backend || 'copilot').toLowerCase();
  const modelVal = String(c.model || 'claude-opus-4.6-1m').replace(/_/g, '-');
  const modelId = models.find(m => m.id === modelVal)
    ? modelVal
    : models.find(m => m.id === modelVal.replace(/-(\d+)-(\d)/g, '-$1.$2'))?.id
      ?? models.find(m => m.label.toLowerCase().replace(/[^a-z0-9]/g, '').includes(modelVal.toLowerCase().replace(/[^a-z0-9]/g, '')))?.id
      ?? models[0]?.id ?? '';

  const html = `
    <div class="ins-field">
      <label class="ins-field-label">Mode</label>
      <select data-config-key="mode" data-node-id="${id}">
        <option ${mode === 'skill' ? 'selected' : ''} value="skill">Skill</option>
        <option ${mode === 'subagent' ? 'selected' : ''} value="subagent">SubAgent</option>
        <option ${mode === 'mcp' ? 'selected' : ''} value="mcp">MCP</option>
        <option ${mode === 'prompt' || mode === 'rawprompt' ? 'selected' : ''} value="prompt">Raw Prompt</option>
      </select>
    </div>
    <div class="ins-field">
      <label class="ins-field-label">Backend</label>
      <select data-config-key="backend" data-node-id="${id}">
        <option ${backend === 'copilot' ? 'selected' : ''} value="copilot">Copilot</option>
        <option ${backend === 'claude' ? 'selected' : ''} value="claude">Claude</option>
      </select>
    </div>
    ${mode === 'skill' ? `
      <div class="ins-field">
        <label class="ins-field-label">Skill Name</label>
        <select data-config-key="skillName" data-node-id="${id}">
          <option value="">— select skill —</option>
          ${skills.map(s => `<option value="${escapeHtml(s.name)}" ${String(c.skillName) === s.name ? 'selected' : ''} title="${escapeHtml(s.description)}">${escapeHtml(s.name)}</option>`).join('')}
        </select>
      </div>
    ` : ''}
    ${mode === 'subagent' ? `
      <div class="ins-field">
        <label class="ins-field-label">SubAgent Name</label>
        <select data-config-key="subAgentName" data-node-id="${id}">
          <option value="">— select agent —</option>
          ${agents.map(a => `<option value="${escapeHtml(a.name)}" ${String(c.subAgentName) === a.name ? 'selected' : ''} title="${escapeHtml(a.description)}">${escapeHtml(a.name)}</option>`).join('')}
        </select>
      </div>
    ` : ''}
    ${hasScenarios ? `
      <div class="ins-field">
        <label class="ins-field-label">Scenario</label>
        <select data-config-key="scenario" data-node-id="${id}">
          ${scenarioNames.map(s => {
            const desc = scenarios![s].description || '';
            return `<option value="${escapeHtml(s)}" ${selectedScenario === s ? 'selected' : ''} title="${escapeHtml(desc)}">${escapeHtml(s)}</option>`;
          }).join('')}
        </select>
        ${selectedScenario && scenarios![selectedScenario]?.description
          ? `<div class="ins-field-hint">${escapeHtml(scenarios![selectedScenario].description!)}</div>`
          : ''}
      </div>
    ` : ''}
    <div class="ins-field">
      <label class="ins-field-label">Model</label>
      <select data-config-key="model" data-node-id="${id}" class="ai-model-select">
        ${models.map(m => `<option value="${escapeHtml(m.id)}" ${modelId === m.id ? 'selected' : ''}>${escapeHtml(m.label)}</option>`).join('')}
      </select>
      ${(() => {
        const sel = models.find(m => m.id === modelId);
        if (!sel) return '';
        return `<div class="ins-model-badge" style="color: ${tColor(sel.tier)}">
          <span class="ins-model-tier">${sel.tier}</span>
          <span class="ins-model-cost">${sel.costMultiplier}x</span>
        </div>`;
      })()}
    </div>
    ${mode === 'prompt' || mode === 'rawprompt' ? `
      <div class="ins-field">
        <label class="ins-field-label">User Prompt</label>
        <textarea data-config-key="userPrompt" data-node-id="${id}" placeholder="Enter your prompt...">${escapeHtml(String(c.userPrompt || ''))}</textarea>
      </div>
      <div class="ins-field">
        <label class="ins-field-label">System Prompt</label>
        <textarea data-config-key="systemPrompt" data-node-id="${id}" placeholder="You are a helpful assistant...">${escapeHtml(String(c.systemPrompt || ''))}</textarea>
      </div>
    ` : ''}
    <div class="ins-field">
      <label class="ins-field-label">Input mapping</label>
      <input class="mono" value="${escapeHtml(String(c.inputMapping || 'input'))}" data-config-key="inputMapping" data-node-id="${id}" />
    </div>
    <div class="ins-field">
      <label class="ins-field-label">Output key</label>
      <input class="mono" value="${escapeHtml(String(c.outputKey || 'result'))}" data-config-key="outputKey" data-node-id="${id}" />
    </div>
    <div class="ins-field" style="display:flex; gap:8px; align-items:center;">
      <div style="flex:1">
        <label class="ins-field-label">Confidence <span class="confidence-value" style="opacity:.5">${c.confidence ?? 70}%</span></label>
        <input type="range" min="0" max="100" value="${c.confidence ?? 70}" data-config-key="confidence" data-node-id="${id}" oninput="this.closest('.ins-field').querySelector('.confidence-value').textContent=this.value+'%'" />
      </div>
      <div style="flex:1">
        <label class="ins-field-label">Max tokens</label>
        <input type="number" step="1024" min="4096" max="32768" value="${c.maxTokens || 4096}" data-config-key="maxTokens" data-node-id="${id}" />
      </div>
    </div>
    ${(() => {
      const selectedName = mode === 'skill' ? String(c.skillName || '') : mode === 'subagent' ? String(c.subAgentName || '') : '';
      if (!selectedName || (mode !== 'skill' && mode !== 'subagent')) return '';
      let bindings: Record<string, string> = {};
      try {
        bindings = typeof c.inputBindings === 'string'
          ? JSON.parse(c.inputBindings as string)
          : (c.inputBindings as unknown as Record<string, string>) || {};
      } catch { /* ignore */ }
      const params = inputParams.filter(() => inputParamsKey === `${mode}:${selectedName}`);
      if (params.length === 0 && inboundFields.length === 0) return '';
      return `
      <div class="ins-field">
        <label class="ins-field-label">Input Bindings</label>
        ${params.length > 0 ? `
        <div class="binding-editor">
          ${params.map(p => {
            const currentBinding = bindings[p.name] || '';
            const isStatic = currentBinding.startsWith('static:');
            const isFieldMode = currentBinding.startsWith('field:') || (!isStatic && currentBinding !== '' && !currentBinding.startsWith('static:'));
            const staticVal = isStatic ? currentBinding.slice(7) : '';
            const fieldVal = isFieldMode ? (currentBinding.startsWith('field:') ? currentBinding.slice(6) : currentBinding) : '';
            const reqBadge = p.required ? '<span class="binding-req">required</span>' : '';
            const isBool = p.type === 'boolean';
            const isNone = !isStatic && !isFieldMode;

            return `
            <div class="binding-card" title="${escapeHtml(p.description)}">
              <div class="binding-card-header">
                <div class="binding-param-name">${escapeHtml(p.name)}</div>
                <div class="binding-param-meta">
                  <span class="binding-type-badge">${escapeHtml(p.type)}</span>
                  ${reqBadge}
                </div>
              </div>
              ${p.description ? `<div class="binding-param-desc">${escapeHtml(p.description)}</div>` : ''}
              <div class="binding-card-body">
                <div class="binding-mode-tabs">
                  <button class="binding-mode-tab ${isNone ? 'active' : ''}" data-ai-mode="none" data-ai-param="${escapeHtml(p.name)}" data-node-id="${id}">None</button>
                  <button class="binding-mode-tab ${isFieldMode ? 'active' : ''}" data-ai-mode="field" data-ai-param="${escapeHtml(p.name)}" data-node-id="${id}">Field</button>
                  <button class="binding-mode-tab ${isStatic ? 'active' : ''}" data-ai-mode="static" data-ai-param="${escapeHtml(p.name)}" data-node-id="${id}">Static</button>
                </div>
                <div class="binding-card-value">
                  ${isFieldMode ? `
                    <select class="binding-field-select" data-ai-binding-key="${escapeHtml(p.name)}" data-node-id="${id}">
                      <option value="">— select field —</option>
                      ${inboundFields.map(f => `<option value="${escapeHtml(f.path)}" ${fieldVal === f.path ? 'selected' : ''}>${escapeHtml(f.name)} · ${escapeHtml(f.sourceNode)}</option>`).join('')}
                    </select>
                  ` : isStatic ? (isBool
                    ? `<div class="binding-toggle-wrap">
                        <label class="binding-toggle">
                          <input type="checkbox" ${staticVal === 'true' ? 'checked' : ''} data-ai-static-key="${escapeHtml(p.name)}" data-node-id="${id}" data-is-bool="1" />
                          <span class="binding-toggle-track"></span>
                          <span class="binding-toggle-label">${staticVal === 'true' ? 'true' : 'false'}</span>
                        </label>
                      </div>`
                    : `<input class="binding-static-input" data-ai-static-key="${escapeHtml(p.name)}" data-node-id="${id}" value="${escapeHtml(staticVal)}" placeholder="Enter ${escapeHtml(p.type)} value…" />`
                  ) : `
                    <div class="binding-empty-hint">Choose Field or Static to bind this parameter</div>
                  `}
                </div>
              </div>
            </div>`;
          }).join('')}
        </div>` : `
        <div class="ins-field-hint" style="font-style:italic; opacity:0.7;">
          No declared inputs. Add <code>inputs:</code> to the agent/skill frontmatter.
        </div>`}
        ${inboundFields.length > 0 ? `
        <div class="inbound-fields-panel" style="margin-top:6px;">
          ${renderInboundFields(inboundFields, id, params.map(p => p.name), bindings)}
        </div>` : ''}
      </div>`;
    })()}
  `;
  return { title: 'AI Configuration', html, includeInboundPanel: false };
}

function renderHttp(ctx: InspectorContext): NodeInspectorResult {
  const { id, c, escapeHtml } = ctx;
  const html = `
    <div class="ins-field" style="display:flex; gap:8px;">
      <div style="width: 90px">
        <label class="ins-field-label">Method</label>
        <select data-config-key="method" data-node-id="${id}">
          <option ${c.method === 'GET' ? 'selected' : ''}>GET</option>
          <option ${c.method === 'POST' ? 'selected' : ''}>POST</option>
          <option ${c.method === 'PUT' ? 'selected' : ''}>PUT</option>
          <option ${c.method === 'PATCH' ? 'selected' : ''}>PATCH</option>
          <option ${c.method === 'DELETE' ? 'selected' : ''}>DELETE</option>
        </select>
      </div>
      <div style="flex:1">
        <label class="ins-field-label">URL</label>
        <input class="mono" value="${escapeHtml(String(c.url || ''))}" data-config-key="url" data-node-id="${id}" />
      </div>
    </div>
    <div class="ins-field">
      <label class="ins-field-label">Headers (JSON)</label>
      <textarea data-config-key="headers" data-node-id="${id}">${escapeHtml(String(c.headers || '{}'))}</textarea>
    </div>
  `;
  return { title: 'Configuration', html, includeInboundPanel: true };
}

function renderDelay(ctx: InspectorContext): NodeInspectorResult {
  const { id, c } = ctx;
  const html = `
    <div class="ins-field" style="display:flex; gap:8px;">
      <div style="flex:2">
        <label class="ins-field-label">Duration</label>
        <input type="number" value="${c.duration || 1000}" data-config-key="duration" data-node-id="${id}" />
      </div>
      <div style="flex:1">
        <label class="ins-field-label">Unit</label>
        <select data-config-key="unit" data-node-id="${id}">
          <option ${c.unit === 'ms' ? 'selected' : ''}>ms</option>
          <option ${c.unit === 's' ? 'selected' : ''}>s</option>
          <option ${c.unit === 'm' ? 'selected' : ''}>m</option>
        </select>
      </div>
    </div>
  `;
  return { title: 'Configuration', html, includeInboundPanel: false };
}

function renderTransform(ctx: InspectorContext): NodeInspectorResult {
  const { id, c, escapeHtml } = ctx;
  const html = `
    <div class="ins-field">
      <label class="ins-field-label">Expression</label>
      <textarea data-config-key="expression" data-node-id="${id}">${escapeHtml(String(c.expression || ''))}</textarea>
      <div class="ins-field-hint">Transform the input payload. Reference with <code>input</code>.</div>
    </div>
  `;
  return { title: 'Configuration', html, includeInboundPanel: true };
}

function renderLog(ctx: InspectorContext): NodeInspectorResult {
  const { node, id, c, escapeHtml, inboundFields, renderInboundFields } = ctx;
  const level = String(c.level || 'Info');
  const msgTpl = String(c.messageTemplate || c.message || '');
  let bindings: Record<string, string> = {};
  try {
    bindings = typeof c.parameterBindings === 'string'
      ? JSON.parse(c.parameterBindings as string)
      : (c.parameterBindings as unknown as Record<string, string>) || {};
  } catch { /* ignore */ }
  const placeholders = (msgTpl.match(/\{(\w+)\}/g) || []).map((m: string) => m.slice(1, -1));

  const html = `
    <div class="ins-field">
      <label class="ins-field-label">Level</label>
      <select data-config-key="level" data-node-id="${id}">
        <option value="Debug" ${level === 'Debug' || level === 'debug' ? 'selected' : ''}>Debug</option>
        <option value="Info" ${level === 'Info' || level === 'info' ? 'selected' : ''}>Info</option>
        <option value="Warn" ${level === 'Warn' || level === 'warn' ? 'selected' : ''}>Warn</option>
        <option value="Error" ${level === 'Error' || level === 'error' ? 'selected' : ''}>Error</option>
      </select>
    </div>
    <div class="ins-field">
      <label class="ins-field-label">Message template</label>
      <div id="tpl-editor-${id}" class="ins-monaco-container" data-node-id="${id}" data-config-key="messageTemplate" data-lang="template" style="height:60px"></div>
      <div class="ins-field-hint">Type <code>{</code> for available field suggestions.</div>
    </div>
    ${placeholders.length > 0 ? `
    <div class="ins-field">
      <label class="ins-field-label">Parameter bindings</label>
      <div class="binding-editor">
        ${placeholders.map((p: string) => {
          const currentBinding = bindings[p] || '';
          return `
          <div class="binding-row">
            <div class="binding-placeholder">
              <span class="binding-brace">{</span>${escapeHtml(p)}<span class="binding-brace">}</span>
            </div>
            <svg class="binding-arrow" width="16" height="12" viewBox="0 0 16 12"><path d="M0 6h12M10 2l4 4-4 4" stroke="currentColor" stroke-width="1.5" fill="none" stroke-linecap="round"/></svg>
            <div class="binding-value-wrap">
              <input class="binding-input mono" value="${escapeHtml(currentBinding)}" data-binding-key="${escapeHtml(p)}" data-node-id="${id}" placeholder="select or type…" list="inbound-fields-${id}" />
            </div>
          </div>`;
        }).join('')}
      </div>
    </div>` : ''}
    ${inboundFields.length > 0 ? `
    <div class="ins-field">
      <label class="ins-field-label">Available inbound fields</label>
      <div class="inbound-fields-panel">
        ${renderInboundFields(inboundFields, id, placeholders, bindings)}
      </div>
      <div class="ins-field-hint">Click a field to insert it into the active binding, or drag into the template.</div>
    </div>` : `
    <div class="ins-field">
      <div class="ins-field-hint" style="font-style:italic; opacity:0.7;">Connect an input edge to discover available fields.</div>
    </div>`}
    <datalist id="inbound-fields-${id}">
      ${inboundFields.map(f => `<option value="${escapeHtml(f.path)}">${escapeHtml(f.name)} (${f.sourceNode})</option>`).join('')}
    </datalist>
  `;
  return { title: 'Log Output', html, includeInboundPanel: false };
}

function renderGroup(ctx: InspectorContext): NodeInspectorResult {
  const { node, id, c, escapeHtml } = ctx;
  let scopedVars: { name: string; type: string; default: string }[] = [];
  try {
    scopedVars = Array.isArray(c.scopedVariables)
      ? c.scopedVariables as { name: string; type: string; default: string }[]
      : typeof c.scopedVariables === 'string'
        ? JSON.parse(c.scopedVariables as string)
        : [];
  } catch { /* ignore */ }

  const varRows = scopedVars.map((v, i) => `
    <div class="binding-card" style="padding:6px 8px; display:flex; gap:6px; align-items:center;">
      <input class="mono" style="flex:2" value="${escapeHtml(v.name)}" placeholder="name" data-scoped-var-field="name" data-scoped-var-index="${i}" data-node-id="${id}" />
      <select style="flex:1" data-scoped-var-field="type" data-scoped-var-index="${i}" data-node-id="${id}">
        <option ${v.type === 'string' ? 'selected' : ''}>string</option>
        <option ${v.type === 'number' ? 'selected' : ''}>number</option>
        <option ${v.type === 'boolean' ? 'selected' : ''}>boolean</option>
        <option ${v.type === 'object' ? 'selected' : ''}>object</option>
        <option ${v.type === 'array' ? 'selected' : ''}>array</option>
      </select>
      <input class="mono" style="flex:2" value="${escapeHtml(v.default || '')}" placeholder="default" data-scoped-var-field="default" data-scoped-var-index="${i}" data-node-id="${id}" />
      <button class="btn-icon" data-action="remove-scoped-var" data-scoped-var-index="${i}" data-node-id="${id}" title="Remove" style="flex-shrink:0">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="12" height="12"><path d="M18 6 6 18M6 6l12 12"/></svg>
      </button>
    </div>
  `).join('');

  const html = `
    <div class="ins-field">
      <label class="ins-field-label">Label</label>
      <input value="${escapeHtml(String(c.label || 'Group'))}" data-config-key="label" data-node-id="${id}" />
    </div>
    <div class="ins-field">
      <label class="ins-field-label">Scoped Variables</label>
      <div class="ins-field-hint">Available to child nodes as <code>scope.&lt;name&gt;</code></div>
      <div class="scoped-vars-list" style="display:flex; flex-direction:column; gap:4px; margin-top:4px;">
        ${varRows}
      </div>
      <button class="btn-secondary" data-action="add-scoped-var" data-node-id="${id}" style="margin-top:6px; font-size:11px;">+ Add variable</button>
    </div>
    <div class="ins-field">
      <label style="display:flex; align-items:center; gap:6px; font-size:12px;">
        <input type="checkbox" ${node.collapsed ? 'checked' : ''} data-action="toggle-collapse-insp" data-node-id="${id}" style="width:auto" />
        Collapsed
      </label>
    </div>
  `;
  return { title: 'Configuration', html, includeInboundPanel: false };
}

function renderEnd(ctx: InspectorContext): NodeInspectorResult {
  const { id, c } = ctx;
  const html = `
    <div class="ins-field">
      <label class="ins-field-label">Exit status</label>
      <select data-config-key="status" data-node-id="${id}">
        <option ${c.status === 'success' ? 'selected' : ''}>success</option>
        <option ${c.status === 'failure' ? 'selected' : ''}>failure</option>
      </select>
    </div>
  `;
  return { title: 'Configuration', html, includeInboundPanel: false };
}

function renderFallback(ctx: InspectorContext): NodeInspectorResult {
  const { node, id, c, escapeHtml, nodeType } = ctx;
  const rt = node.runtimeType.toLowerCase();
  let html: string;

  if (nodeType?.serverProperties?.length) {
    html = nodeType.serverProperties.map(prop => {
      const val = escapeHtml(String(c[prop.name] ?? prop.default ?? ''));
      if (prop.type === 'enum' && prop.options) {
        const opts = prop.options.map(o => `<option ${String(c[prop.name]) === o ? 'selected' : ''} value="${escapeHtml(o)}">${escapeHtml(o)}</option>`).join('');
        return `<div class="ins-field">
          <label class="ins-field-label">${escapeHtml(prop.label)}</label>
          <select data-config-key="${escapeHtml(prop.name)}" data-node-id="${id}">${opts}</select>
        </div>`;
      }
      if (prop.type === 'boolean') {
        return `<div class="ins-field">
          <label style="display:flex; align-items:center; gap:6px; font-size:12px;">
            <input type="checkbox" ${c[prop.name] ? 'checked' : ''} data-config-key="${escapeHtml(prop.name)}" data-node-id="${id}" style="width:auto" />
            ${escapeHtml(prop.label)}
          </label>
        </div>`;
      }
      if (prop.type === 'number') {
        return `<div class="ins-field">
          <label class="ins-field-label">${escapeHtml(prop.label)}</label>
          <input type="number" value="${val}" data-config-key="${escapeHtml(prop.name)}" data-node-id="${id}" placeholder="${escapeHtml(prop.placeholder || '')}" />
        </div>`;
      }
      const isCSharpCode = (prop.name === 'scriptContent' && (rt === 'csharpscript' || rt === 'csharptask'))
        || (prop.name === 'conditionExpression' && rt === 'while')
        || (prop.name === 'collectionExpression' && rt === 'foreach');
      if (isCSharpCode) {
        const editorId = `monaco-${id}-${prop.name}`;
        const small = prop.name !== 'scriptContent';
        const hint = prop.name === 'conditionExpression' ? ' <span class="ins-field-hint">(C# — must return bool)</span>'
          : prop.name === 'collectionExpression' ? ' <span class="ins-field-hint">(C# — must return IEnumerable)</span>'
          : '';
        return `<div class="ins-field">
          <label class="ins-field-label">${escapeHtml(prop.label)}${hint}</label>
          <div class="ins-monaco-container${small ? ' ins-monaco-sm' : ''}" id="${editorId}" data-node-id="${id}" data-config-key="${escapeHtml(prop.name)}"></div>
        </div>`;
      }
      if (prop.type === 'code' || prop.type === 'expression') {
        return `<div class="ins-field">
          <label class="ins-field-label">${escapeHtml(prop.label)}</label>
          <textarea class="mono" data-config-key="${escapeHtml(prop.name)}" data-node-id="${id}" placeholder="${escapeHtml(prop.placeholder || '')}">${val}</textarea>
        </div>`;
      }
      if (prop.type === 'map') {
        return `<div class="ins-field">
          <label class="ins-field-label">${escapeHtml(prop.label)}</label>
          <textarea class="mono" data-config-key="${escapeHtml(prop.name)}" data-node-id="${id}" placeholder="${escapeHtml(prop.placeholder || '{}')}">${val}</textarea>
          <div class="ins-field-hint">JSON key-value mapping</div>
        </div>`;
      }
      // Default: string input
      return `<div class="ins-field">
        <label class="ins-field-label">${escapeHtml(prop.label)}${prop.required ? ' *' : ''}</label>
        <input value="${val}" data-config-key="${escapeHtml(prop.name)}" data-node-id="${id}" placeholder="${escapeHtml(prop.placeholder || '')}" class="${prop.type === 'duration' ? 'mono' : ''}" />
      </div>`;
    }).join('');
  } else {
    html = `<div class="ins-field-hint">No additional configuration.</div>`;
  }

  return { title: 'Configuration', html, includeInboundPanel: true };
}

// ---------------------------------------------------------------------------
// Setter inspector
// ---------------------------------------------------------------------------

function renderSetter(ctx: InspectorContext): NodeInspectorResult {
  const { id, c, escapeHtml, inboundFields } = ctx;
  const mappings: Record<string, string> = (c.variableMappings as Record<string, string>) || {};
  const entries = Object.entries(mappings);

  const fieldOptions = inboundFields.map(f => {
    const label = escapeHtml(`${f.sourceNode}.${f.name}`);
    return `<option value="${escapeHtml(f.path)}">${label}</option>`;
  }).join('');

  const rows = entries.map(([globalKey, sourceExpr], i) => `
    <tr class="ins-setter-row" data-idx="${i}">
      <td><input type="text" class="ins-input ins-setter-key" value="${escapeHtml(globalKey)}" placeholder="Global variable name" data-node="${id}" data-idx="${i}"/></td>
      <td>
        <select class="ins-select ins-setter-source" data-node="${id}" data-idx="${i}">
          <option value="">— select source —</option>
          ${fieldOptions}
          <option value="${escapeHtml(sourceExpr)}" selected>${escapeHtml(sourceExpr)}</option>
        </select>
      </td>
      <td><button class="ins-btn-icon ins-setter-remove" data-node="${id}" data-idx="${i}" title="Remove">✕</button></td>
    </tr>
  `).join('');

  const html = `
    <div class="ins-section">
      <label class="ins-label">Variable Mappings</label>
      <p class="ins-field-hint">Map upstream output fields to global workflow variables.</p>
      <table class="ins-setter-table" style="width:100%;border-collapse:collapse;">
        <thead><tr><th style="text-align:left;padding:4px;">Global Variable</th><th style="text-align:left;padding:4px;">Source Field</th><th></th></tr></thead>
        <tbody>${rows}</tbody>
      </table>
      <button class="ins-btn ins-setter-add" data-node="${id}" style="margin-top:6px;">+ Add mapping</button>
    </div>
  `;

  return { title: 'Setter', html, includeInboundPanel: false };
}

// ---------------------------------------------------------------------------
// Registry + dispatcher
// ---------------------------------------------------------------------------

const renderers: Record<string, (ctx: InspectorContext) => NodeInspectorResult> = {
  start: renderStart,
  trigger: renderTrigger,
  if: renderIfElse,
  ifelse: renderIfElse,
  switch: renderSwitch,
  parallel: renderParallel,
  foreach: renderForEach,
  subflow: renderSubflow,
  ai: renderAI,
  http: renderHttp,
  httprequest: renderHttp,
  delay: renderDelay,
  transform: renderTransform,
  log: renderLog,
  group: renderGroup,
  end: renderEnd,
  noop: renderSetter,
  setter: renderSetter,
};

export function renderNodeConfig(ctx: InspectorContext): NodeInspectorResult {
  const rt = ctx.node.runtimeType.toLowerCase();
  const renderer = renderers[rt];
  if (renderer) return renderer(ctx);
  return renderFallback(ctx);
}