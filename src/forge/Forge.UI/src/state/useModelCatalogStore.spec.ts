import { describe, it, expect } from 'vitest';
import { selectableModels } from './useModelCatalogStore';
import type { ModelInfo } from '@/types';

function model(id: string, over: Partial<ModelInfo> = {}): ModelInfo {
  return {
    id,
    vendor: 'copilot',
    family: 'gpt-5',
    displayName: id,
    contextWindow: 200000,
    inputPricePerMTok: 3,
    outputPricePerMTok: 15,
    supportsTools: true,
    supportsStreaming: true,
    status: 'live',
    available: true,
    supportsReasoning: false,
    ...over,
  } as ModelInfo;
}

describe('selectableModels', () => {
  it('keeps models the live catalog still serves', () => {
    const models = [model('gpt-6-astra'), model('claude-opus-5')];
    expect(selectableModels(models).map((m) => m.id)).toEqual(['gpt-6-astra', 'claude-opus-5']);
  });

  it('hides models the live Copilot catalog no longer serves', () => {
    const models = [model('gpt-6-astra'), model('gpt-5.2', { available: false })];
    expect(selectableModels(models).map((m) => m.id)).toEqual(['gpt-6-astra']);
  });

  it('hides retired models', () => {
    const models = [model('gpt-6-astra'), model('gpt-4.1', { status: 'retired' })];
    expect(selectableModels(models).map((m) => m.id)).toEqual(['gpt-6-astra']);
  });

  it('keeps the currently assigned model visible even when stale', () => {
    // An already-assigned model must stay in the list so the picker can render
    // it as the selected value and warn, rather than silently showing blank.
    const models = [model('gpt-6-astra'), model('gpt-5.2', { available: false, status: 'retired' })];
    expect(selectableModels(models, 'gpt-5.2').map((m) => m.id)).toEqual(['gpt-6-astra', 'gpt-5.2']);
  });

  it('does not resurrect other stale models when a current value is given', () => {
    const models = [
      model('gpt-6-astra'),
      model('gpt-5.2', { available: false }),
      model('gpt-4.1', { status: 'retired' }),
    ];
    expect(selectableModels(models, 'gpt-5.2').map((m) => m.id)).toEqual(['gpt-6-astra', 'gpt-5.2']);
  });
});
