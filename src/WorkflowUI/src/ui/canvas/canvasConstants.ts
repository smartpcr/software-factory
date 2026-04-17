export const CANVAS_SNAP_GRID: [number, number] = [16, 16];

export const DEFAULT_VIEWPORT = { x: 0, y: 0, zoom: 1 };

export const ZOOM_LIMITS = { min: 0.1, max: 4 } as const;

export const WHEEL_GUARD = {
  requireModifier: true,
  modifierKey: 'ctrlKey' as const,
} as const;