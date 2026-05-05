// Phase 28 — shared agent-pair header chip. The DocConversationPane uses
// the same shape as the LiveExecutionView header (avatar + name + crew +
// model), keyed off the latest deliverable's generator/evaluator agents.
import { avatarFor, agentTooltip } from '@/agentAvatar';

export function PairSide(props: {
  role: 'generator' | 'evaluator';
  crewName?: string | null;
  agentName?: string | null;
  agentId?: string | null;
  model?: string | null;
  sameAsOther?: boolean;
  selected?: boolean;
  onClick?: () => void;
}): JSX.Element {
  const { role, crewName, agentName, agentId, model, sameAsOther, selected, onClick } = props;
  const portraitKey = agentId ?? agentName ?? null;
  const portrait = portraitKey ? avatarFor(portraitKey, agentName ?? null) : null;
  const tip = portraitKey ? agentTooltip(portraitKey, agentName ?? null, role) : '';
  const modelLabel = model ? formatModelLabel(model) : null;
  return (
    <div
      className="live-exec__pair-side"
      data-role={role}
      onClick={onClick}
      role={onClick ? 'button' : undefined}
      tabIndex={onClick ? 0 : undefined}
      style={{
        cursor: onClick ? 'pointer' : undefined,
        borderBottom: selected ? '2px solid var(--accent, #3b82f6)' : '2px solid transparent',
        paddingBottom: 2,
      }}
    >
      {portrait ? (
        <img
          src={portrait}
          alt={agentName ?? role}
          className={`live-exec__pair-avatar ${role === 'generator' ? 'eng' : 'rev'}`}
          title={tip}
          onError={(e) => { (e.target as HTMLImageElement).style.display = 'none'; }}
        />
      ) : (
        <span className="live-exec__pair-mono" title={tip}>
          {(role === 'generator' ? 'G' : 'E')}
        </span>
      )}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 1, minWidth: 0 }}>
        <strong style={{ whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {agentName ?? '—'}
        </strong>
        {crewName && (
          <span style={{ fontSize: 10, color: 'var(--text-tertiary)', whiteSpace: 'nowrap' }}>
            {crewName}
          </span>
        )}
        {modelLabel && (
          <span
            className="live-exec__pair-model"
            data-warn={sameAsOther ? 'true' : undefined}
            title={
              (sameAsOther ? 'Generator and evaluator share the same model — pair-diversity rule violated.\n' : '')
              + `Model: ${model}`
            }
            style={{
              fontSize: 10,
              fontFamily: 'var(--font-mono)',
              color: sameAsOther ? 'rgb(220, 38, 38)' : 'var(--text-tertiary)',
              whiteSpace: 'nowrap',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              maxWidth: 180,
            }}
          >
            {sameAsOther ? '⚠ ' : ''}{modelLabel}
          </span>
        )}
      </div>
    </div>
  );
}

// Trim a long backend/model id like "copilot/claude-opus-4-6-1m" or
// "anthropic/claude-opus-4-6" down to just the model part for the header
// chip — the full id stays in the tooltip.
export function formatModelLabel(model: string): string {
  const slash = model.lastIndexOf('/');
  return slash >= 0 ? model.slice(slash + 1) : model;
}
