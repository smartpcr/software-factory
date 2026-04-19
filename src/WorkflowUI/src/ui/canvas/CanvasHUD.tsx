import { useReactFlow } from '@xyflow/react';
import './CanvasHUD.css';

export function CanvasHUD() {
  const { zoomIn, zoomOut, fitView, setViewport, getZoom } = useReactFlow();
  const zoom = Math.round(getZoom() * 100);

  return (
    <div className="canvas-hud" data-testid="canvas-hud">
      <div className="zoom-level mono" data-testid="zoom-level">{zoom}%</div>
      <div className="zoom-ctrls">
        <button onClick={() => zoomIn()} title="Zoom in" aria-label="Zoom in">
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <path d="M12 5v14M5 12h14" />
          </svg>
        </button>
        <button onClick={() => zoomOut()} title="Zoom out" aria-label="Zoom out">
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <path d="M5 12h14" />
          </svg>
        </button>
        <button onClick={() => fitView({ padding: 0.2 })} title="Fit to view" aria-label="Fit to view">
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <path d="M3 7V3h4M21 7V3h-4M3 17v4h4M21 17v4h-4" />
          </svg>
        </button>
        <button onClick={() => setViewport({ x: 0, y: 0, zoom: 1 })} title="Reset view" aria-label="Reset view">
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <circle cx="12" cy="12" r="10" />
            <circle cx="12" cy="12" r="3" />
          </svg>
        </button>
      </div>
    </div>
  );
}
