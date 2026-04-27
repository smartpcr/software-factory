// -----------------------------------------------------------------------
// UI configuration — AI models and other settings
// -----------------------------------------------------------------------

export interface AIModel {
  id: string;
  label: string;
  tier: 'fast' | 'standard' | 'premium';
  costMultiplier: number;
}

export const aiModels: AIModel[] = [
  // Claude models
  { id: 'claude-sonnet-4.6', label: 'Claude Sonnet 4.6', tier: 'standard', costMultiplier: 1 },
  { id: 'claude-sonnet-4.5', label: 'Claude Sonnet 4.5', tier: 'standard', costMultiplier: 1 },
  { id: 'claude-haiku-4.5', label: 'Claude Haiku 4.5', tier: 'fast', costMultiplier: 0.33 },
  { id: 'claude-opus-4.7', label: 'Claude Opus 4.7', tier: 'premium', costMultiplier: 7.5 },
  { id: 'claude-opus-4.6', label: 'Claude Opus 4.6', tier: 'premium', costMultiplier: 3 },
  { id: 'claude-opus-4.6-1m', label: 'Claude Opus 4.6 (1M context)', tier: 'premium', costMultiplier: 6 },
  { id: 'claude-opus-4.5', label: 'Claude Opus 4.5', tier: 'premium', costMultiplier: 3 },
  { id: 'claude-sonnet-4', label: 'Claude Sonnet 4', tier: 'standard', costMultiplier: 1 },
  // GPT models
  { id: 'gpt-5.4', label: 'GPT-5.4', tier: 'standard', costMultiplier: 1 },
  { id: 'gpt-5.3-codex', label: 'GPT-5.3-Codex', tier: 'standard', costMultiplier: 1 },
  { id: 'gpt-5.2-codex', label: 'GPT-5.2-Codex', tier: 'standard', costMultiplier: 1 },
  { id: 'gpt-5.2', label: 'GPT-5.2', tier: 'standard', costMultiplier: 1 },
  { id: 'gpt-5.4-mini', label: 'GPT-5.4 mini', tier: 'fast', costMultiplier: 0.33 },
  { id: 'gpt-5-mini', label: 'GPT-5 mini', tier: 'fast', costMultiplier: 0 },
  { id: 'gpt-4.1', label: 'GPT-4.1', tier: 'fast', costMultiplier: 0 },
];

/** Get tier badge color for CSS */
export function tierColor(tier: AIModel['tier']): string {
  switch (tier) {
    case 'fast': return 'var(--ok)';
    case 'standard': return 'var(--accent)';
    case 'premium': return 'var(--n-ai)';
  }
}
