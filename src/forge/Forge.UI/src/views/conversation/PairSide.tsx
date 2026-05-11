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
  /**
   * Phase 47c — surfacing the backend ("copilot" vs "claude") next to the
   * model so operators can see which usage cap each agent is hitting. Read
   * from the most recent `task.prompt` transcript event; null when the
   * iter hasn't dispatched yet.
   */
  backend?: string | null;
  invokeMode?: string | null;
  sameAsOther?: boolean;
  selected?: boolean;
  onClick?: () => void;
}): JSX.Element {
  const { role, crewName, agentName, agentId, model, backend, invokeMode, sameAsOther, selected, onClick } = props;
  const portraitKey = agentId ?? agentName ?? null;
  const portrait = portraitKey ? avatarFor(portraitKey, agentName ?? null) : null;
  const tip = portraitKey ? agentTooltip(portraitKey, agentName ?? null, role) : '';
  const modelLabel = model ? formatModelLabel(model) : null;
  const backendLabel = formatBackendLabel(backend, invokeMode);
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
              + (backend ? `\nBackend: ${backend}${invokeMode ? `/${invokeMode}` : ''}` : '')
            }
            style={{
              fontSize: 10,
              fontFamily: 'var(--font-mono)',
              color: sameAsOther ? 'rgb(220, 38, 38)' : 'var(--text-tertiary)',
              whiteSpace: 'nowrap',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              maxWidth: 220,
              display: 'inline-flex',
              alignItems: 'center',
              gap: 4,
            }}
          >
            {sameAsOther ? '⚠ ' : ''}
            {backendLabel && (
              <span
                title={
                  backend === 'claude'
                    ? 'Routes through the Anthropic Claude CLI/SDK (uses your Anthropic plan quota).'
                    : backend === 'copilot'
                    ? 'Routes through the GitHub Copilot SDK (uses your Copilot subscription quota).'
                    : `Backend: ${backend}`
                }
                style={{
                  display: 'inline-block',
                  padding: '0 4px',
                  fontSize: 9,
                  letterSpacing: '0.08em',
                  textTransform: 'uppercase',
                  borderRadius: 3,
                  border: '1px solid currentColor',
                  // Color-code so claude (Anthropic billing) reads warn-amber
                  // and copilot (subscription billing) reads neutral. Helps
                  // operators spot agents on the wrong rail at a glance.
                  color: backend === 'claude'
                    ? 'rgb(217, 119, 6)'
                    : backend === 'copilot'
                    ? 'rgb(56, 189, 248)'
                    : 'inherit',
                  opacity: 0.9,
                }}
              >
                {backendLabel}
              </span>
            )}
            <span>{modelLabel}</span>
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

/**
 * Format the backend + invoke-mode pair as a compact pill label. Returns
 * null when both inputs are missing so the chip falls back to the previous
 * model-only layout. Examples:
 *   ("copilot", "sdk") → "copilot"
 *   ("claude",  "cli") → "claude·cli"
 *   ("copilot", null)  → "copilot"
 *   (null,       null) → null
 */
function formatBackendLabel(backend?: string | null, invokeMode?: string | null): string | null {
  if (!backend) return null;
  // Hide the default copilot/sdk invoke-mode (it's the inherited norm) so
  // the pill stays short for the common case; surface anything else
  // (claude/cli, copilot/cli, claude/sdk, …) since divergence from the
  // norm is the actionable signal.
  if (backend === 'copilot' && (!invokeMode || invokeMode === 'sdk')) return backend;
  return invokeMode ? `${backend}·${invokeMode}` : backend;
}
