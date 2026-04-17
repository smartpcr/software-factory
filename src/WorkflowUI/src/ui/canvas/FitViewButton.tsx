import { useFitWorkflow } from './useFitWorkflow';

export function FitViewButton() {
  const { fitView } = useFitWorkflow();

  return (
    <button
      type="button"
      onClick={fitView}
      aria-label="Fit workflow to view"
      data-testid="fit-view-button"
      style={{
        position: 'absolute',
        bottom: 8,
        right: 8,
        zIndex: 10,
        padding: '4px 8px',
        cursor: 'pointer',
      }}
    >
      Fit View
    </button>
  );
}