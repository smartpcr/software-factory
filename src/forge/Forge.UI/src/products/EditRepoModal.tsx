import { useEffect, useRef, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import type { BuildValidationRun, ValidateStep } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import { RepoIcon } from '@/products/RepoIcon';
import type { Repo } from '@/types';

interface Props {
  open: boolean;
  repo: Repo;
  onClose: () => void;
  onSaved: (updated: Repo) => void;
}

type Tab = 'general' | 'sync' | 'pipeline';

export function EditRepoModal({ open, repo, onClose, onSaved }: Props) {
  const [tab, setTab] = useState<Tab>('general');
  const [displayName, setDisplayName] = useState(repo.displayName);
  const [cloneRoot, setCloneRoot] = useState(repo.cloneRoot);
  const [defaultBranch, setDefaultBranch] = useState(repo.defaultBranch);
  const [areaPathsText, setAreaPathsText] = useState((repo.areaPaths ?? []).join('\n'));
  const [agentReadyTeam, setAgentReadyTeam] = useState(repo.agentReadyDefaultTeam ?? '');
  const [initCommand, setInitCommand] = useState(repo.initCommand ?? '');
  const [restoreCommand, setRestoreCommand] = useState(repo.restoreCommand ?? '');
  const [buildCommand, setBuildCommand] = useState(repo.buildCommand ?? '');
  const [testCommand, setTestCommand] = useState(repo.testCommand ?? '');
  const [buildTimeoutSec, setBuildTimeoutSec] = useState<number | ''>(repo.buildTimeoutSec ?? '');
  const [testTimeoutSec, setTestTimeoutSec] = useState<number | ''>(repo.testTimeoutSec ?? '');
  const [detecting, setDetecting] = useState(false);
  const [validating, setValidating] = useState(false);
  const [validationRuns, setValidationRuns] = useState<BuildValidationRun[]>([]);
  const [expandedRunId, setExpandedRunId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [phase, setPhase] = useState<'idle' | 'saving' | 'cloning' | 'detecting'>('idle');
  const [err, setErr] = useState<string | null>(null);
  const nameRef = useRef<HTMLInputElement | null>(null);
  const toast = useToastStore((s) => s.show);

  useEffect(() => {
    if (!open) return;
    setTab('general');
    setDisplayName(repo.displayName);
    setCloneRoot(repo.cloneRoot);
    setDefaultBranch(repo.defaultBranch);
    setAreaPathsText((repo.areaPaths ?? []).join('\n'));
    setAgentReadyTeam(repo.agentReadyDefaultTeam ?? '');
    setInitCommand(repo.initCommand ?? '');
    setRestoreCommand(repo.restoreCommand ?? '');
    setBuildCommand(repo.buildCommand ?? '');
    setTestCommand(repo.testCommand ?? '');
    setBuildTimeoutSec(repo.buildTimeoutSec ?? '');
    setTestTimeoutSec(repo.testTimeoutSec ?? '');
    setValidating(false);
    setValidationRuns([]);
    setExpandedRunId(null);
    setBusy(false);
    setPhase('idle');
    setErr(null);
    forgeApi.getValidationRuns(repo.id).then(setValidationRuns).catch(() => {});
    const t = window.requestAnimationFrame(() => nameRef.current?.focus());
    return () => window.cancelAnimationFrame(t);
  }, [open, repo]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !busy) onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose, busy]);

  async function submit() {
    const cleanName = displayName.trim();
    const cleanPath = cloneRoot.trim();
    const cleanBranch = defaultBranch.trim() || 'main';
    if (!cleanName) { setErr('Display name is required'); return; }
    if (!cleanPath) { setErr('Local repo path is required'); return; }

    const cleanAreaPaths = areaPathsText.split(/\r?\n/).map((s) => s.trim()).filter((s) => s.length > 0);
    const cleanTeam = agentReadyTeam.trim();
    const prevAreaPaths = repo.areaPaths ?? [];
    const areaPathsChanged = cleanAreaPaths.length !== prevAreaPaths.length
      || cleanAreaPaths.some((p, i) => p !== prevAreaPaths[i]);
    const teamChanged = cleanTeam !== (repo.agentReadyDefaultTeam ?? '');

    setBusy(true);
    setErr(null);
    try {
      setPhase('saving');
      const patch: Record<string, unknown> = {};
      if (cleanName !== repo.displayName) patch.displayName = cleanName;
      if (cleanPath !== repo.cloneRoot) patch.cloneRoot = cleanPath;
      if (cleanBranch !== repo.defaultBranch) patch.defaultBranch = cleanBranch;
      if (areaPathsChanged) patch.areaPaths = cleanAreaPaths;
      if (teamChanged) patch.agentReadyDefaultTeam = cleanTeam.length === 0 ? null : cleanTeam;
      if (initCommand !== (repo.initCommand ?? '')) patch.initCommand = initCommand;
      if (restoreCommand !== (repo.restoreCommand ?? '')) patch.restoreCommand = restoreCommand;
      if (buildCommand !== (repo.buildCommand ?? '')) patch.buildCommand = buildCommand;
      if (testCommand !== (repo.testCommand ?? '')) patch.testCommand = testCommand;
      const bTimeout = typeof buildTimeoutSec === 'number' ? buildTimeoutSec : 0;
      const tTimeout = typeof testTimeoutSec === 'number' ? testTimeoutSec : 0;
      if (bTimeout !== (repo.buildTimeoutSec ?? 0)) patch.buildTimeoutSec = bTimeout;
      if (tTimeout !== (repo.testTimeoutSec ?? 0)) patch.testTimeoutSec = tTimeout;

      const updated = Object.keys(patch).length > 0
        ? await forgeApi.patchRepo(repo.id, patch as Parameters<typeof forgeApi.patchRepo>[1])
        : repo;
      onSaved(updated);

      if (updated.initCommand && !initCommand) setInitCommand(updated.initCommand);
      if (updated.restoreCommand && !restoreCommand) setRestoreCommand(updated.restoreCommand);
      if (updated.buildCommand && !buildCommand) setBuildCommand(updated.buildCommand);
      if (updated.testCommand && !testCommand) setTestCommand(updated.testCommand);

      if (patch.cloneRoot) {
        setPhase('cloning');
        const result = await forgeApi.cloneRepo(repo.id);
        toast(result.outcome === 'cloned' ? `Cloned ${cleanName} to ${cleanPath}` : `Saved "${cleanName}" — local repo already present`, 'success');
      }

      // Auto-detect + validate.
      const eI = initCommand || updated.initCommand;
      const eR = restoreCommand || updated.restoreCommand;
      const eB = buildCommand || updated.buildCommand;
      const eT = testCommand || updated.testCommand;
      if (!eI && !eR && !eB && !eT) {
        try {
          setPhase('detecting');
          const detected = await forgeApi.detectBuild(repo.id);
          if (detected.initCommand || detected.restoreCommand || detected.buildCommand || detected.testCommand) {
            const autoPatch: Record<string, string> = {};
            if (detected.initCommand && !eI) { autoPatch.initCommand = detected.initCommand; setInitCommand(detected.initCommand); }
            if (detected.restoreCommand && !eR) { autoPatch.restoreCommand = detected.restoreCommand; setRestoreCommand(detected.restoreCommand); }
            if (detected.buildCommand && !eB) { autoPatch.buildCommand = detected.buildCommand; setBuildCommand(detected.buildCommand); }
            if (detected.testCommand && !eT) { autoPatch.testCommand = detected.testCommand; setTestCommand(detected.testCommand); }
            if (Object.keys(autoPatch).length > 0) {
              const freshRepo = await forgeApi.patchRepo(repo.id, autoPatch as Parameters<typeof forgeApi.patchRepo>[1]);
              onSaved(freshRepo);
            }
            toast(
              detected.ambiguous ? `Detected ${detected.ecosystem} (ambiguous — review commands)` : `Detected ${detected.ecosystem} build system`,
              detected.ambiguous ? 'info' : 'success',
            );
          }
        } catch { /* non-fatal */ }
      }

      // Phase 29 — only auto-validate when pipeline commands actually changed.
      const pipelineChanged = patch.initCommand !== undefined || patch.restoreCommand !== undefined
        || patch.buildCommand !== undefined || patch.testCommand !== undefined
        || patch.buildTimeoutSec !== undefined || patch.testTimeoutSec !== undefined;
      const fI = initCommand || eI; const fR = restoreCommand || eR;
      const fB = buildCommand || eB; const fT = testCommand || eT;
      if (pipelineChanged && (fI || fR || fB || fT)) {
        try {
          const accepted = await forgeApi.validateBuild(repo.id);
          toast(`Validation queued (run ${accepted.runId}) — you'll be notified when it completes`, 'info');
        } catch (e) {
          const msg = e instanceof Error ? e.message : '';
          if (msg.includes('already running') || msg.includes('409')) toast('A validation is already running for this repo', 'info');
        }
      }

      if (!patch.cloneRoot) toast(`Saved "${cleanName}"`, 'success');
      onClose();
    } catch (e) {
      setErr(e instanceof Error ? e.message : 'Save failed');
    } finally {
      setBusy(false);
      setPhase('idle');
    }
  }

  function onKeyDownForm(e: React.KeyboardEvent) {
    if (e.key === 'Enter' && !e.shiftKey && !busy) { e.preventDefault(); void submit(); }
  }

  const buttonLabel = phase === 'saving' ? 'Saving…' : phase === 'cloning' ? 'Cloning…' : phase === 'detecting' ? 'Detecting…' : 'Save changes';

  const pipelineCount = [initCommand, restoreCommand, buildCommand, testCommand].filter(Boolean).length;

  return (
    <>
      <div className={`modal-backdrop${open ? ' show' : ''}`} onClick={() => { if (!busy) onClose(); }} aria-hidden={!open} />
      <div className={`modal${open ? ' show' : ''}`} role="dialog" aria-modal="true" aria-labelledby="edit-repo-title" onKeyDown={onKeyDownForm} style={{ width: 580 }}>

        {/* ── Header ─────────────────────────────────────────────── */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 14, marginBottom: 10 }}>
          <RepoIcon kind={repo.kind} size={28} />
          <div style={{ minWidth: 0, flex: 1 }}>
            <h3 className="modal-title" id="edit-repo-title" style={{ margin: 0, fontSize: 15 }}>Edit repo</h3>
            <code style={{ fontSize: 11, opacity: 0.5 }}>{repo.remoteUrl}</code>
          </div>
        </div>

        {/* ── Tab bar ─────────────────────────────────────────────── */}
        <div style={{ display: 'flex', gap: 0, borderBottom: '1px solid var(--border-subtle)', marginBottom: 14 }}>
          {([
            ['general', 'General'],
            ['sync', 'Sync'],
            ['pipeline', `Pipeline${pipelineCount ? ` (${pipelineCount})` : ''}`],
          ] as [Tab, string][]).map(([id, label]) => (
            <button
              key={id}
              type="button"
              onClick={() => setTab(id)}
              style={{
                background: 'none', border: 'none', cursor: 'pointer', font: 'inherit',
                padding: '7px 16px', fontSize: 12, fontWeight: 600,
                color: tab === id ? 'var(--accent)' : 'var(--text-tertiary)',
                borderBottom: tab === id ? '2px solid var(--accent)' : '2px solid transparent',
                marginBottom: -1, transition: 'color 100ms, border-color 100ms',
              }}
            >
              {label}
            </button>
          ))}
        </div>

        {/* ── Tab content (scrollable) ───────────────────────────── */}
        <div style={{ maxHeight: 'min(60vh, 440px)', overflowY: 'auto', paddingRight: 4, marginBottom: 14 }}>
          <div style={{ display: 'grid', gap: 12 }}>

            {/* ── General tab ──────────────────────────────────────── */}
            {tab === 'general' && (
              <>
                <FieldGroup label="Display name">
                  <input ref={nameRef} value={displayName} onChange={(e) => setDisplayName(e.target.value)} maxLength={120} spellCheck={false} disabled={busy} style={inputStyle} />
                </FieldGroup>
                <FieldGroup label="Local repo path" hint="Absolute path on this machine. Missing folders will be cloned automatically.">
                  <input value={cloneRoot} onChange={(e) => setCloneRoot(e.target.value)} placeholder="C:\\src\\my-repo" spellCheck={false} disabled={busy} style={{ ...inputStyle, ...monoStyle }} />
                </FieldGroup>
                <FieldGroup label="Default branch">
                  <input value={defaultBranch} onChange={(e) => setDefaultBranch(e.target.value)} placeholder="main" maxLength={120} spellCheck={false} disabled={busy} style={{ ...inputStyle, ...monoStyle }} />
                </FieldGroup>
              </>
            )}

            {/* ── Sync tab ─────────────────────────────────────────── */}
            {tab === 'sync' && (
              <>
                <FieldGroup label="Area paths" hint="One per line. Empty = whole project. ADO uses UNDER prefix; GitHub ignores.">
                  <textarea value={areaPathsText} onChange={(e) => setAreaPathsText(e.target.value)} placeholder={'Atlas\\\\Platform\nAtlas\\\\Edge\\\\IoT'} rows={4} spellCheck={false} disabled={busy} style={{ ...inputStyle, ...monoStyle, resize: 'vertical' }} data-testid="repo-area-paths" />
                </FieldGroup>
                <FieldGroup label="Agent-ready default team" hint="Team auto-assigned to work items tagged `agent-ready`. Defaults to Engineering.">
                  <input value={agentReadyTeam} onChange={(e) => setAgentReadyTeam(e.target.value)} placeholder="Engineering" spellCheck={false} disabled={busy} style={inputStyle} data-testid="repo-agent-ready-team" />
                </FieldGroup>
              </>
            )}

            {/* ── Pipeline tab ─────────────────────────────────────── */}
            {tab === 'pipeline' && (
              <>
                {/* Commands — 2-column grid for init+restore, build+test */}
                <SectionLabel>Commands</SectionLabel>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
                  <FieldGroup label="Init">
                    <input value={initCommand} onChange={(e) => setInitCommand(e.target.value)} placeholder="(none)" spellCheck={false} disabled={busy} style={{ ...inputStyle, ...monoStyle }} data-testid="repo-init-command" />
                  </FieldGroup>
                  <FieldGroup label="Restore">
                    <input value={restoreCommand} onChange={(e) => setRestoreCommand(e.target.value)} placeholder="dotnet restore …" spellCheck={false} disabled={busy} style={{ ...inputStyle, ...monoStyle }} data-testid="repo-restore-command" />
                  </FieldGroup>
                  <FieldGroup label="Build">
                    <input value={buildCommand} onChange={(e) => setBuildCommand(e.target.value)} placeholder="dotnet build …" spellCheck={false} disabled={busy} style={{ ...inputStyle, ...monoStyle }} data-testid="repo-build-command" />
                  </FieldGroup>
                  <FieldGroup label="Test">
                    <input value={testCommand} onChange={(e) => setTestCommand(e.target.value)} placeholder="dotnet test …" spellCheck={false} disabled={busy} style={{ ...inputStyle, ...monoStyle }} data-testid="repo-test-command" />
                  </FieldGroup>
                </div>

                {/* Timeouts */}
                <SectionLabel>Timeouts</SectionLabel>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
                  <FieldGroup label="Build timeout (sec)" hint="Applies to init / restore / build.">
                    <input type="number" value={buildTimeoutSec} onChange={(e) => setBuildTimeoutSec(e.target.value ? Number(e.target.value) : '')} placeholder="600" min={0} disabled={busy} style={{ ...inputStyle, ...monoStyle }} data-testid="repo-build-timeout" />
                  </FieldGroup>
                  <FieldGroup label="Test timeout (sec)" hint="Applies to test step.">
                    <input type="number" value={testTimeoutSec} onChange={(e) => setTestTimeoutSec(e.target.value ? Number(e.target.value) : '')} placeholder="600" min={0} disabled={busy} style={{ ...inputStyle, ...monoStyle }} data-testid="repo-test-timeout" />
                  </FieldGroup>
                </div>

                {/* Detect / Validate actions */}
                <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginTop: 2 }}>
                  <button type="button" className="btn btn-secondary" title="Scan repo for build system markers and auto-fill empty command fields" disabled={busy || detecting || validating} onClick={async () => {
                    setDetecting(true);
                    try {
                      const r = await forgeApi.detectBuild(repo.id);
                      if (r.initCommand && !initCommand) setInitCommand(r.initCommand);
                      if (r.restoreCommand && !restoreCommand) setRestoreCommand(r.restoreCommand);
                      if (r.buildCommand && !buildCommand) setBuildCommand(r.buildCommand);
                      if (r.testCommand && !testCommand) setTestCommand(r.testCommand);
                      toast(r.ambiguous ? `Detected ${r.ecosystem} (ambiguous)` : `Detected ${r.ecosystem}`, r.ambiguous ? 'info' : 'success');
                    } catch (e) { toast(e instanceof Error ? e.message : 'Detection failed', 'danger'); }
                    finally { setDetecting(false); }
                  }} style={{ fontSize: 12 }}>
                    {detecting ? '🔍 Detecting…' : '🔍 Detect'}
                  </button>
                  <button type="button" className="btn btn-secondary" title="Run the full pipeline (init → restore → build → test) to verify commands work" disabled={busy || detecting || validating || (!initCommand && !restoreCommand && !buildCommand && !testCommand)} onClick={async () => {
                    setValidating(true);
                    try {
                      const a = await forgeApi.validateBuild(repo.id);
                      toast(`Validation queued (${a.runId})`, 'info');
                      setTimeout(() => { forgeApi.getValidationRuns(repo.id).then(setValidationRuns).catch(() => {}); }, 1000);
                    } catch (e) { const m = e instanceof Error ? e.message : ''; toast(m.includes('already running') ? 'Already running' : m || 'Failed', 'danger'); }
                    finally { setValidating(false); }
                  }} style={{ fontSize: 12 }}>
                    {validating ? '⏳ Validating…' : '✓ Validate'}
                  </button>
                </div>

                {/* Validation history */}
                {validationRuns.length > 0 && <ValidationHistory runs={validationRuns} expandedRunId={expandedRunId} onToggle={(id) => setExpandedRunId(expandedRunId === id ? null : id)} />}
              </>
            )}
          </div>
        </div>

        {/* ── Status / error ─────────────────────────────────────── */}
        {phase === 'cloning' && (
          <div role="status" style={infoStyle}>Cloning {repo.remoteUrl} into {cloneRoot.trim()}…</div>
        )}
        {err && (
          <div role="alert" style={errStyle}><span style={{ marginRight: 6 }}>⚠</span>{err}</div>
        )}

        {/* ── Footer actions ─────────────────────────────────────── */}
        <div className="modal-actions" style={{ marginTop: 6 }}>
          <button className="btn btn-secondary" onClick={onClose} disabled={busy}>Cancel</button>
          <button className="btn btn-primary" onClick={() => void submit()} disabled={busy || !displayName.trim() || !cloneRoot.trim()}>{buttonLabel}</button>
        </div>
      </div>
    </>
  );
}

// ═══════════════════════════════════════════════════════════════════════
// Sub-components
// ═══════════════════════════════════════════════════════════════════════

function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      fontSize: 10, fontWeight: 700, textTransform: 'uppercase',
      letterSpacing: 0.6, color: 'var(--text-tertiary)',
      borderBottom: '1px solid var(--border-subtle)', paddingBottom: 3, marginTop: 4,
    }}>
      {children}
    </div>
  );
}

function FieldGroup({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <label style={{ display: 'grid', gap: 4 }}>
      <span style={{ fontSize: 11, fontWeight: 600, letterSpacing: 0.2, textTransform: 'uppercase', color: 'var(--text-secondary)' }}>
        {label}
      </span>
      {children}
      {hint && <span style={{ fontSize: 10, color: 'var(--text-tertiary)', lineHeight: 1.3 }}>{hint}</span>}
    </label>
  );
}

function ValidationHistory({ runs, expandedRunId, onToggle }: {
  runs: BuildValidationRun[];
  expandedRunId: string | null;
  onToggle: (id: string) => void;
}) {
  return (
    <div style={{ marginTop: 4 }}>
      <SectionLabel>History ({runs.length} runs)</SectionLabel>
      <div style={{ display: 'grid', gap: 2, marginTop: 4 }}>
        {runs.map((run) => {
          const open = expandedRunId === run.id;
          const steps: ValidateStep[] = run.stepsJson ? JSON.parse(run.stepsJson) : [];
          const icon = run.status === 'succeeded' ? '✓'
            : run.status === 'failed' ? '✗'
            : run.status === 'cancelled' ? '⊘'
            : run.status === 'running' ? '⏳'
            : run.status === 'queued' ? '◻'
            : '—';
          const color = run.status === 'succeeded' ? 'rgb(34,197,94)'
            : run.status === 'failed' ? 'rgb(220,38,38)'
            : run.status === 'cancelled' ? 'var(--text-tertiary)'
            : run.status === 'running' ? 'var(--accent)'
            : 'var(--text-tertiary)';
          const statusLabel = run.status === 'cancelled' ? ' cancelled'
            : run.status === 'running' ? ' running…'
            : run.status === 'queued' ? ' queued'
            : '';
          return (
            <div key={run.id}>
              <button type="button" onClick={() => onToggle(run.id)} title={`${run.status} — click to ${open ? 'collapse' : 'expand'} details`} style={{
                background: open ? 'color-mix(in srgb, var(--accent) 6%, transparent)' : 'transparent',
                border: 'none', cursor: 'pointer', color: 'inherit', font: 'inherit',
                padding: '3px 6px', borderRadius: 3, width: '100%', textAlign: 'left',
                display: 'flex', gap: 6, alignItems: 'center', fontSize: 11,
              }}>
                <span style={{ color }}>{icon}</span>
                <span style={{ fontFamily: 'var(--font-mono)', fontSize: 10 }}>{new Date(run.startedAt).toLocaleString()}</span>
                {statusLabel && <span style={{ color, fontSize: 10, fontWeight: 600 }}>{statusLabel}</span>}
                <span style={{ opacity: 0.5, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {run.errorMessage
                    ? run.errorMessage
                    : steps.map((s) => `${s.step}:${s.passed ? 'ok' : 'fail'}`).join(' → ')}
                </span>
                {run.triggeredBy && <span style={{ opacity: 0.4, fontSize: 10 }}>by {run.triggeredBy}</span>}
                <span style={{ opacity: 0.4, fontSize: 9 }}>{open ? '▼' : '▶'}</span>
              </button>
              {open && steps.length > 0 && (
                <div style={{ padding: '4px 0 4px 18px' }}>
                  {steps.map((s) => (
                    <div key={s.step} style={{ marginBottom: 3 }}>
                      <div style={{ display: 'flex', gap: 5, alignItems: 'center', fontSize: 11 }}>
                        <span style={{ color: s.passed ? 'rgb(34,197,94)' : 'rgb(220,38,38)' }}>{s.passed ? '✓' : '✗'}</span>
                        <span style={{ fontWeight: 600, minWidth: 44 }}>{s.step}</span>
                        <code style={{ fontFamily: 'var(--font-mono)', fontSize: 10, opacity: 0.6 }}>{s.command}</code>
                        {s.timedOut && <span style={{ color: 'rgb(220,38,38)', fontSize: 9 }}>TIMED OUT</span>}
                        {!s.passed && <span style={{ color: 'rgb(220,38,38)', fontSize: 9 }}>exit {s.exitCode}</span>}
                      </div>
                      {(s.stderr?.trim() || s.stdout?.trim()) && (
                        <pre style={{
                          fontFamily: 'var(--font-mono, monospace)', fontSize: 10,
                          background: 'rgba(0,0,0,0.3)', color: s.passed ? 'var(--text-secondary)' : 'rgb(248,113,113)',
                          padding: '5px 7px', borderRadius: 4, marginTop: 2,
                          maxHeight: 160, overflow: 'auto', whiteSpace: 'pre-wrap', wordBreak: 'break-all',
                        }}>
                          {s.stderr?.trim() ? `── stderr ──\n${s.stderr.trim()}` : ''}
                          {s.stderr?.trim() && s.stdout?.trim() ? '\n\n' : ''}
                          {s.stdout?.trim() ? `── stdout ──\n${s.stdout.trim()}` : ''}
                        </pre>
                      )}
                    </div>
                  ))}
                  {run.errorMessage && <div style={{ color: 'rgb(220,38,38)', fontSize: 10, marginTop: 3 }}>Error: {run.errorMessage}</div>}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════
// Shared styles
// ═══════════════════════════════════════════════════════════════════════

const inputStyle: React.CSSProperties = {
  width: '100%', padding: '7px 10px', fontSize: 13,
  fontFamily: 'inherit', color: 'var(--text-primary)',
  background: 'var(--bg-surface)', border: '1px solid var(--border-strong)',
  borderRadius: 'var(--radius-sm)', outline: 'none',
  transition: 'border-color 120ms ease, box-shadow 120ms ease',
};

const monoStyle: React.CSSProperties = { fontFamily: 'var(--font-mono, monospace)', fontSize: 12 };

const errStyle: React.CSSProperties = {
  fontSize: 12, color: 'var(--danger)',
  background: 'var(--danger-bg, rgba(220,38,38,0.08))',
  border: '1px solid var(--danger-border, rgba(220,38,38,0.25))',
  borderRadius: 'var(--radius-sm)', padding: '7px 10px', marginBottom: 8,
};

const infoStyle: React.CSSProperties = {
  fontSize: 12, color: 'var(--text-secondary)',
  background: 'var(--bg-surface-alt, rgba(255,255,255,0.04))',
  border: '1px solid var(--border-subtle)',
  borderRadius: 'var(--radius-sm)', padding: '7px 10px', marginBottom: 8,
};
