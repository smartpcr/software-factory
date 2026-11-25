// Story-wide standing rules: the operator decision pins of a story spawn.
// A pin with no work item goes into every iteration prompt of every plan doc
// and workstream in the story; a scoped pin only into that one item's prompt.
// The per-doc 📌 button only creates scoped pins, so this is where story-wide
// rules are added, read and removed.

import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Link } from 'react-router-dom';
import { forgeApi } from '@/api/forgeApi';
import { useConfirmStore } from '@/state/useConfirmStore';
import { useToastStore } from '@/state/useToastStore';
import { RuleCatalogView } from './RuleCatalogView';

export interface StoryPin {
  id: string;
  text: string;
  workItemId: string | null;
  createdAt: string;
  createdBy: string;
}

/** Story-wide pins first (oldest first), then pins scoped to one item. */
export function splitPins(pins: StoryPin[]): { story: StoryPin[]; scoped: StoryPin[] } {
  const byAge = [...pins].sort((a, b) => a.createdAt.localeCompare(b.createdAt));
  return {
    story: byAge.filter((p) => !p.workItemId),
    scoped: byAge.filter((p) => !!p.workItemId),
  };
}

/** A work item id shortened for display: the plan doc name or the stage slug. */
export function scopeLabel(workItemId: string): string {
  if (workItemId.startsWith('plan-doc:')) return workItemId.slice(workItemId.lastIndexOf(':') + 1);
  const stage = workItemId.indexOf('-stage-');
  return stage >= 0 ? workItemId.slice(stage + 7) : workItemId;
}

function when(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime())
    ? iso
    : d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

/** The pin list, grouped. Pure so it can be rendered in tests. */
export function StoryRulesList({ pins, busy, onRemove }: {
  pins: StoryPin[];
  busy?: boolean;
  onRemove?: (pin: StoryPin) => void;
}) {
  const { story, scoped } = splitPins(pins);
  const row = (p: StoryPin) => (
    <li key={p.id} className="sc-rules__pin">
      <div className="sc-rules__pin-text">{p.text}</div>
      <div className="sc-rules__pin-meta">
        {p.workItemId && <span className="sc-rules__scope" title={p.workItemId}>{scopeLabel(p.workItemId)}</span>}
        <span>{when(p.createdAt)}</span>
        {onRemove && (
          <button type="button" className="sc-rules__remove" disabled={busy} onClick={() => onRemove(p)}>
            Remove
          </button>
        )}
      </div>
    </li>
  );
  return (
    <>
      <h3 className="sc-rules__group">Whole story · {story.length}</h3>
      {story.length === 0
        ? <p className="sc-rules__empty">No story-wide rules yet.</p>
        : <ul className="sc-rules__list">{story.map(row)}</ul>}
      {scoped.length > 0 && (
        <>
          <h3 className="sc-rules__group">One document or workstream · {scoped.length}</h3>
          <ul className="sc-rules__list">{scoped.map(row)}</ul>
        </>
      )}
    </>
  );
}

export function StoryRulesButton({ storyId, productId }: { storyId: string; productId: string | null }) {
  const [pins, setPins] = useState<StoryPin[] | null>(null);
  const [open, setOpen] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);

  const load = useCallback(async () => {
    try {
      setPins(await forgeApi.listStoryDecisionPins(storyId));
    } catch {
      setPins([]);
    }
  }, [storyId]);

  useEffect(() => { void load(); }, [load]);

  const storyWide = pins ? pins.filter((p) => !p.workItemId).length : 0;
  return (
    <>
      <button
        ref={trigger}
        type="button"
        className="sc-rules-btn"
        title="Standing rules: the product rule catalog (rules that follow tags across stories) and this story's pins"
        onClick={() => { setOpen(true); void load(); }}
      >
        📌 RULES{storyWide > 0 ? ` ${storyWide}` : ''}
      </button>
      {open && (
        <StoryRulesPanel
          storyId={storyId}
          productId={productId}
          pins={pins ?? []}
          reload={load}
          onClose={() => { setOpen(false); trigger.current?.focus(); }}
        />
      )}
    </>
  );
}

function StoryRulesPanel({ storyId, productId, pins, reload, onClose }: {
  storyId: string;
  productId: string | null;
  pins: StoryPin[];
  reload: () => Promise<void>;
  onClose: () => void;
}) {
  const titleId = useId();
  const [tab, setTab] = useState<'catalog' | 'pins'>('catalog');
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const textArea = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    textArea.current?.focus();
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  async function add() {
    const text = draft.trim();
    if (!text) return;
    setBusy(true);
    setError(null);
    try {
      await forgeApi.addStoryDecisionPin(storyId, text);
      setDraft('');
      await reload();
      useToastStore.getState().show('Rule added. Every workstream sees it from its next iteration.', 'success');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Adding the rule failed');
    } finally {
      setBusy(false);
    }
  }

  async function remove(pin: StoryPin) {
    const ok = await useConfirmStore.getState().ask({
      title: 'REMOVE THIS RULE?',
      body: pin.text.length > 240 ? `${pin.text.slice(0, 240)}…` : pin.text,
      tone: 'warn',
      confirmLabel: 'Remove',
      cancelLabel: 'Keep',
    });
    if (!ok) return;
    setBusy(true);
    setError(null);
    try {
      await forgeApi.removeStoryDecisionPin(storyId, pin.id);
      await reload();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Removing the rule failed');
    } finally {
      setBusy(false);
    }
  }

  // The layer carries `swarm-ctrl` so the panel picks up the scoped theme
  // tokens outside the story view's own subtree.
  return createPortal(
    <div className="swarm-ctrl sc-rules-layer" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="sc-rules sc-rules--wide" role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <header className="sc-rules__head">
          <h2 id={titleId} className="sc-rules__title">STANDING RULES · {storyId}</h2>
          <button type="button" className="sc-rules__close" aria-label="Close" onClick={onClose}>✕</button>
        </header>
        <div className="sc-rules__tabs" role="tablist" aria-label="Rule sources">
          {([['catalog', 'Product catalog'], ['pins', `Story pins · ${pins.length}`]] as const).map(([key, label]) => (
            <button
              key={key}
              type="button"
              role="tab"
              aria-selected={tab === key}
              className={tab === key ? 'is-active' : undefined}
              onClick={() => setTab(key)}
            >
              {label}
            </button>
          ))}
        </div>
        {tab === 'catalog' && (
          <div className="sc-rules__body" role="tabpanel">
            <p className="sc-rc__dim sc-rc__page-link">
              The same catalog is on the <Link to="/policies" onClick={onClose}>Policies page</Link>. Attach a rule to one
              workstream from its Overview tab.
            </p>
            {productId
              ? <RuleCatalogView productId={productId} storyId={storyId} />
              : <p className="sc-rules__empty">This story has no product, so it has no rule catalog.</p>}
          </div>
        )}
        {tab === 'pins' && (<>
        <p className="sc-rules__help">
          A story pin is a one-off rule for this story only. A pin with no document or workstream goes into every
          iteration prompt of every plan doc and workstream in this story, from each one&apos;s next iteration. For a
          rule that should follow a kind of work across stories, use the product catalog.
        </p>
        <div className="sc-rules__body" role="tabpanel">
          <StoryRulesList pins={pins} busy={busy} onRemove={(p) => void remove(p)} />
        </div>
        <div className="sc-rules__add">
          <label className="sc-rules__label" htmlFor={`${titleId}-draft`}>New story-wide rule</label>
          <textarea
            id={`${titleId}-draft`}
            ref={textArea}
            className="sc-rules__input"
            rows={6}
            maxLength={8000}
            value={draft}
            placeholder="e.g. Never run the full gate inside an iteration; check only the packages your diff touches."
            onChange={(e) => setDraft(e.target.value)}
          />
          {error && <p className="sc-rules__error" role="alert">{error}</p>}
          <div className="sc-rules__actions">
            <button type="button" onClick={onClose}>Close</button>
            <button type="button" className="primary" disabled={busy || !draft.trim()} onClick={() => void add()}>
              {busy ? 'Saving…' : 'Add pin'}
            </button>
          </div>
        </div>
        </>)}
      </div>
    </div>,
    document.body,
  );
}