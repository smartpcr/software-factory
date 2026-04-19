import { describe, it, expect } from 'vitest';
import { EDGE_STYLE_MAP, getEdgeStyle } from '../../edges/edgeStyleMap';
import { ALL_MESSAGE_TYPES } from '../fixtures/connections/all-message-types';

describe('edgeStyleMap', () => {
  it('covers all 5 message types', () => {
    expect(Object.keys(EDGE_STYLE_MAP)).toHaveLength(5);
    for (const mt of ALL_MESSAGE_TYPES) {
      expect(EDGE_STYLE_MAP[mt]).toBeDefined();
    }
  });

  it('each style has color, cssColor, strokeWidth, and label', () => {
    for (const mt of ALL_MESSAGE_TYPES) {
      const style = getEdgeStyle(mt);
      expect(style.color).toBeTruthy();
      expect(style.cssColor).toBeTruthy();
      expect(style.strokeWidth).toBeGreaterThan(0);
      expect(style.label).toBeTruthy();
    }
  });

  it('Fail uses dashed style', () => {
    const style = getEdgeStyle('Fail');
    expect(style.dashArray).toBeTruthy();
  });

  it('Complete uses solid style', () => {
    const style = getEdgeStyle('Complete');
    expect(style.dashArray).toBeUndefined();
  });

  it('Fail has error-edge CSS class', () => {
    const style = getEdgeStyle('Fail');
    expect(style.cssClass).toBe('error-edge');
  });
});
