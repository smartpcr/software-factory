import React from 'react';

export interface CanvasSurfaceProps {
  children: React.ReactNode;
  'data-testid'?: string;
}

export function CanvasSurface({ children, 'data-testid': testId }: CanvasSurfaceProps) {
  return (
    <div
      role="application"
      aria-label="Workflow canvas"
      data-testid={testId ?? 'canvas-surface'}
      tabIndex={0}
      style={{
        width: '100%',
        height: '100%',
        position: 'relative',
        outline: 'none',
      }}
    >
      {children}
    </div>
  );
}