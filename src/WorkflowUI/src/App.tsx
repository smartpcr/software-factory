import { WorkflowCanvas } from './ui/canvas/WorkflowCanvas';
import { Palette } from './ui/palette/Palette';

export function App() {
  return (
    <div style={{ display: 'flex', height: '100vh' }}>
      <Palette />
      <div style={{ flex: 1, position: 'relative' }}>
        <h1 style={{ position: 'absolute', top: 8, left: 16, zIndex: 5, margin: 0, fontSize: 18 }}>
          Workflow UI
        </h1>
        <WorkflowCanvas showMiniMap showControls />
      </div>
    </div>
  );
}
