import { useEffect, useMemo, useRef, useState } from 'react';
import type { Card, ColumnId, Priority, CardType, Crew } from '@/types';
import { useStateStore } from '@/state/useStateStore';
import { useBoardStore } from '@/state/useBoardStore';
import { useProductsStore } from '@/state/useProductsStore';
import { sampleState } from '@/sampleData';
import { forgeApi, ForgeApiError } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import { avatarFor, agentTooltip } from '@/agentAvatar';
import type { WorkItem } from '@/workitems/workItemTypes';

interface CardModalProps {
  open: boolean;
  onClose: () => void;
  /** Pass a card to edit it; omit for the "new card" flow. */
  initial?: Card | null;
  /**
   * Phase 14 — when set, the modal seeds itself from an existing work
   * item (typically one imported from ADO/GitHub). Title pre-fills from
   * `title`, prompt from `descriptionMd`, the task kind is forced based
   * on the WI's type (phase/stage/step → plan; else → coding), and the
   * <c>createWorkItem</c> step is skipped at submit because the WI
   * already exists. Submit creates the board mirror + crew assign +
   * runner kickoff.
   */
  fromWorkItem?: WorkItem | null;
}

const TYPES: CardType[] = ['story', 'task', 'bug'];
const PRIORITIES: Priority[] = ['low', 'med', 'high'];

// Phase 14 — task-type toggle. 'none' is the existing simple-create flow;
// 'plan' / 'coding' route the create through the WIT + crew dispatch
// pipeline so the new card is auto-assigned to the matching crew lead.
type TaskKind = 'none' | 'plan' | 'coding';

const PLAN_SUBKINDS: Array<{ key: 'phase' | 'stage' | 'step'; label: string }> = [
  { key: 'phase', label: 'Phase' },
  { key: 'stage', label: 'Stage' },
  { key: 'step',  label: 'Step' },
];

const CODING_TYPES: Array<{ key: 'epic' | 'feature' | 'task' | 'bug'; label: string }> = [
  { key: 'epic',    label: 'Epic' },
  { key: 'feature', label: 'Feature' },
  { key: 'task',    label: 'Task' },
  { key: 'bug',     label: 'Bug' },
];

export function CardModal({ open, onClose, initial, fromWorkItem }: CardModalProps) {
  const apiState = useStateStore((s) => s.state);
  const allEpics = apiState?.epics ?? sampleState.epics;
  const cards = useBoardStore((s) => s.cards);
  const upsert = useBoardStore((s) => s.upsertFromRealtime);
  const setSelected = useBoardStore((s) => s.setSelected);

  const activeProductId = useProductsStore((s) => s.activeProductId);
  const reposByProduct = useProductsStore((s) => s.reposByProduct);
  const productKinds = useMemo(() => {
    const repos = activeProductId ? reposByProduct[activeProductId] ?? [] : [];
    const kinds = new Set(repos.map((r) => r.kind));
    return { ado: kinds.has('ado'), github: kinds.has('github') };
  }, [activeProductId, reposByProduct]);

  // Product-scoped stories for the story/epic dropdown. Falls back to the
  // global epics from /api/state when no product stories are loaded.
  const [productStories, setProductStories] = useState<Array<{ id: string; name: string }>>([]);
  useEffect(() => {
    if (!open || !activeProductId) return;
    let cancelled = false;
    forgeApi.listProductStories(activeProductId)
      .then((stories) => { if (!cancelled) setProductStories(stories.map((s) => ({ id: s.id, name: s.name }))); })
      .catch(() => { /* ignore */ });
    return () => { cancelled = true; };
  }, [open, activeProductId]);

  const epics = useMemo(() => {
    if (productStories.length > 0) return productStories;
    const seen = new Set<string>();
    for (const c of cards) seen.add(c.epic);
    const filtered = allEpics.filter((e) => seen.has(e.id));
    return filtered.length > 0 ? filtered : allEpics;
  }, [cards, allEpics, productStories]);

  // ── form state ──────────────────────────────────────────────────────
  const [taskKind, setTaskKind] = useState<TaskKind>('none');
  const [id, setId] = useState('');
  const [epic, setEpic] = useState(epics[0]?.id ?? '');
  const [col, setCol] = useState<ColumnId>('backlog');
  const [type, setType] = useState<CardType>('story');
  const [title, setTitle] = useState('');
  const [pts, setPts] = useState(3);
  const [priority, setPriority] = useState<Priority>('med');
  const [tags, setTags] = useState('');
  const [postTo, setPostTo] = useState<'' | 'ado' | 'github'>('');
  const [description, setDescription] = useState('');
  const [busy, setBusy] = useState(false);

  // Phase 14 task-flow specific.
  const [prompt, setPrompt] = useState('');
  const [planSubkinds, setPlanSubkinds] = useState<Array<'phase' | 'stage' | 'step'>>(['phase', 'stage', 'step']);
  const [codingType, setCodingType] = useState<'epic' | 'feature' | 'task' | 'bug'>('feature');
  const [crews, setCrews] = useState<Crew[]>([]);
  const [crewId, setCrewId] = useState<string>('');
  const [newStoryName, setNewStoryName] = useState('');
  const [creatingStory, setCreatingStory] = useState(false);
  // Load crews lazily once the modal opens with a task kind set, so simple-
  // create flows don't pay the round-trip.  When the active product has no
  // crews (e.g. "local" repos that share the canonical atlas crews), fall
  // back to an unscoped fetch so the picker is never empty.
  useEffect(() => {
    if (!open || taskKind === 'none' || crews.length > 0) return;
    let cancelled = false;
    const applyCrews = (cs: Crew[]) => {
      if (cancelled) return;
      const dayCrews = cs.filter((c) => !c.parentCrewId);
      setCrews(dayCrews.length > 0 ? dayCrews : cs);
    };
    forgeApi.listCrews(activeProductId ?? undefined)
      .then((cs) => {
        if (cancelled) return;
        if (cs.length > 0) { applyCrews(cs); return; }
        // Fallback: active product has no crews — fetch all.
        forgeApi.listCrews().then(applyCrews).catch(() => {});
      })
      .catch(() => { /* ignore */ });
    return () => { cancelled = true; };
  }, [open, taskKind, crews.length, activeProductId]);

  // Auto-pick the default crew per spec: arch for plan, eng for coding.
  // Re-resolve every time taskKind changes so toggling Plan → Coding swaps
  // the crew (and the lead pre-fill) without the operator clearing first.
  // Match by id ('crew-arch'/'crew-eng' from the seed) first, then by name
  // substring so renamed crews still resolve.
  useEffect(() => {
    if (taskKind === 'none' || crews.length === 0) return;
    // Order matters — most-specific id wins so the 'eng' substring doesn't
    // accidentally match 'crew-arch' (it doesn't, but 'rev' and 'qa' could
    // collide on future renames).
    const want = taskKind === 'plan'
      ? ['crew-arch', 'architecture', 'arch']
      : ['crew-eng',  'engineering',  'eng'];
    let found: Crew | undefined;
    for (const w of want) {
      found = crews.find((c) => c.id.toLowerCase() === w
        || c.id.toLowerCase().includes(w)
        || c.name.toLowerCase().includes(w));
      if (found) break;
    }
    if (found) setCrewId(found.id);
  }, [taskKind, crews]);

  useEffect(() => {
    if (!open) return;
    if (fromWorkItem) {
      // Force task kind based on the WI's track / type.
      const isPlan = fromWorkItem.track === 'design'
        || ['phase', 'stage', 'step'].includes(fromWorkItem.type);
      setTaskKind(isPlan ? 'plan' : 'coding');
      setId(fromWorkItem.id);
      setEpic(fromWorkItem.featureRef ?? epics[0]?.id ?? '');
      setCol('backlog');
      setType('story');
      setTitle(fromWorkItem.title);
      setPts(fromWorkItem.points || 3);
      setPriority(fromWorkItem.priority);
      setTags(fromWorkItem.tags.join(', '));
      setPostTo('');
      setDescription('');
      setPrompt(fromWorkItem.descriptionMd ?? '');
      setPlanSubkinds(['phase', 'stage', 'step']);
      // Coding type pre-fills only when WI's type matches a coding-ladder type.
      const validCoding: Array<'epic' | 'feature' | 'task' | 'bug'> = ['epic', 'feature', 'task', 'bug'];
      setCodingType(validCoding.includes(fromWorkItem.type as 'epic' | 'feature' | 'task' | 'bug')
        ? (fromWorkItem.type as 'epic' | 'feature' | 'task' | 'bug')
        : 'feature');
      setCrewId('');
      setNewStoryName('');
      setCreatingStory(false);
      return;
    }
    setTaskKind('none');
    setId(initial?.id ?? '');
    setEpic(initial?.epic ?? epics[0]?.id ?? '');
    setCol(initial?.col ?? 'backlog');
    setType(initial?.type ?? 'story');
    setTitle(initial?.title ?? '');
    setPts(initial?.pts ?? 3);
    setPriority(initial?.priority ?? 'med');
    setTags((initial?.tags ?? []).join(', '));
    setPostTo('');
    setDescription('');
    setPrompt('');
    setPlanSubkinds(['phase', 'stage', 'step']);
    setCodingType('feature');
    setCrewId('');
    setNewStoryName('');
    setCreatingStory(false);
  }, [open, initial, fromWorkItem, epics]);

  function togglePlanSubkind(k: 'phase' | 'stage' | 'step') {
    setPlanSubkinds((cur) => cur.includes(k) ? cur.filter((x) => x !== k) : [...cur, k]);
  }

  /** Resolve the single-create form values into a Card payload. */
  function buildCardPayload(): Card {
    const tagArr = tags.split(',').map((t) => t.trim()).filter(Boolean);
    return {
      id: id || `#${Math.floor(Math.random() * 9000 + 1000)}`,
      epic, col, type, title, tags: tagArr,
      pts, priority, agent: null, cost: 0,
    };
  }

  async function submitSimple() {
    if (initial) {
      const tagArr = tags.split(',').map((t) => t.trim()).filter(Boolean);
      const updated = await forgeApi.patchCard(initial.id, { title, pts, priority, type, tags: tagArr });
      upsert(updated);
      useToastStore.getState().show(`${initial.id} updated`, 'success');
      return;
    }
    const newCard = buildCardPayload();
    const provider = postTo === '' ? undefined : postTo;
    const created = await forgeApi.createCard(newCard, provider, description || undefined);
    upsert(created);
    const where = provider ? ` on ${provider}` : '';
    useToastStore.getState().show(`${created.id} created${where}`, 'success');
  }

  async function submitTask() {
    if (taskKind === 'none') return;
    if (!crewId) {
      useToastStore.getState().show('Pick a crew first.', 'danger');
      return;
    }

    // Resolve the work-item id. From-WI flow reuses the existing id and
    // skips the createWorkItem step (the row already exists, possibly
    // imported from ADO/GitHub). Otherwise we mint a fresh id and create
    // the row server-side.
    const witId = fromWorkItem?.id || id || `WI-${Math.floor(Math.random() * 9000 + 1000)}`;
    const tagArr = tags.split(',').map((t) => t.trim()).filter(Boolean);

    if (!fromWorkItem) {
      // 1. Create the WIT row. Plan tasks live on the design track and use
      //    'phase' as the umbrella type (per spec §13 Q13.6); the multi-
      //    selected sub-kinds become tags so the planning runner knows what
      //    decomposition shape to produce. Coding tasks live on the dev
      //    track and use the explicit selected type.
      const witTags = taskKind === 'plan'
        ? [...tagArr, ...planSubkinds.map((k) => `kind:${k}`), 'task:plan']
        : [...tagArr, 'task:coding'];
      // Bind to the active product's first repo so the work item
      // is associated with the correct project, not the default "atlas".
      const activeRepos = activeProductId ? reposByProduct[activeProductId] ?? [] : [];
      const repoId = activeRepos.length > 0 ? activeRepos[0].id : undefined;
      await forgeApi.createWorkItem({
        id: witId,
        track: taskKind === 'plan' ? 'design' : 'dev',
        type: taskKind === 'plan' ? 'phase' : codingType,
        title,
        descriptionMd: prompt,
        state: 'proposed',
        featureRef: epic || null,
        points: pts,
        priority,
        tags: witTags,
        repoId,
      });
    } else {
      // From-WI flow: keep the existing row but apply any operator edits
      // (title, prompt, points, priority) and append the task:* tag.
      const taskTag = taskKind === 'plan' ? 'task:plan' : 'task:coding';
      const mergedTags = Array.from(new Set([...tagArr, taskTag]));
      const patchBody: Record<string, unknown> = {};
      if (title !== fromWorkItem.title) patchBody.title = title;
      if (prompt !== (fromWorkItem.descriptionMd ?? '')) patchBody.descriptionMd = prompt;
      if (pts !== fromWorkItem.points) patchBody.points = pts;
      if (priority !== fromWorkItem.priority) patchBody.priority = priority;
      patchBody.tags = mergedTags;
      try {
        await forgeApi.patchWorkItem(fromWorkItem.id, patchBody);
      } catch {
        // Non-fatal — proceed to dispatch even if the patch failed.
      }
    }

    // 2. Mirror to the board so it shows up under Plan & Design too.
    //    If a card with the same id already exists (re-dispatch), delete
    //    the old one first so createCard doesn't 409.
    const cardPayload: Card = {
      ...buildCardPayload(),
      id: witId,
      type: taskKind === 'plan' ? 'story' : (codingType === 'epic' || codingType === 'feature' ? 'story' : codingType),
    };
    try {
      await forgeApi.deleteCard(witId);
    } catch {
      // 404 is expected when no prior card exists.
    }
    const created = await forgeApi.createCard(cardPayload).catch(() => cardPayload);
    upsert(created);

    // 3. Assign to the chosen crew. The crew's lead becomes the
    //    generator (server falls back to lead when memberAgentId is omitted).
    try {
      const crew = crews.find((c) => c.id === crewId);
      const memberAgentId = crew?.lead ?? '';
      if (memberAgentId) {
        await forgeApi.assignCrewTask(crewId, { workItemId: witId, memberAgentId });
      }
    } catch (err) {
      // Non-fatal: record the gap but the card is created.
      // eslint-disable-next-line no-console
      console.warn('[forge] assignCrewTask failed', err);
    }

    // 4. Enqueue into the task pipeline. The plan/code endpoints now create
    //    a queue entry and return 200 with { queued: true }.
    const path = taskKind === 'plan' ? 'plan' : 'code';
    type KickoffOutcome = 'queued' | 'failed';
    let kicked: KickoffOutcome = 'queued';
    try {
      const body = taskKind === 'plan'
        ? { workItemId: witId }
        : { stepWorkItemId: witId };
      const r = await fetch(`/api/crews/${encodeURIComponent(crewId)}/${path}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Forge-User': 'forge-ui' },
        body: JSON.stringify(body),
      });
      kicked = r.ok ? 'queued' : 'failed';
    } catch {
      kicked = 'failed';
    }

    // 5. Surface the outcome and open the new card.
    const crewLabel = crews.find((c) => c.id === crewId)?.name ?? crewId;
    if (kicked === 'queued') {
      useToastStore.getState().show(
        `${witId} → ${crewLabel} · queued for ${taskKind}`,
        'success',
      );
    } else {
      useToastStore.getState().show(`${witId} created · runner kickoff failed`, 'danger');
    }
    setSelected(witId);
  }

  async function submit() {
    setBusy(true);
    try {
      if (taskKind === 'none') {
        await submitSimple();
      } else {
        await submitTask();
      }
      onClose();
    } catch (err) {
      const msg = err instanceof ForgeApiError
        ? `${err.status}: ${err.problem?.detail ?? err.problem?.title ?? err.message}`
        : err instanceof Error ? err.message : 'Save failed';
      useToastStore.getState().show(msg, 'danger');
    } finally {
      setBusy(false);
    }
  }

  const isTask = taskKind !== 'none';

  return (
    <>
      <div className={`modal-backdrop${open ? ' show' : ''}`} onClick={onClose} />
      <div className={`modal${open ? ' show' : ''}`} role="dialog" aria-modal="true" style={{ width: 'min(560px, 95vw)' }}>
        <h3 className="modal-title">
          {initial
            ? `Edit ${initial.id}`
            : fromWorkItem
              ? `Dispatch ${fromWorkItem.id} as ${taskKind === 'plan' ? 'Plan' : 'Coding'} task`
              : 'New card'}
        </h3>

        {!initial && (
          <div style={{ display: 'flex', gap: 4, marginBottom: 14 }}>
            {(['none', 'plan', 'coding'] as TaskKind[]).map((k) => (
              <button
                key={k}
                type="button"
                onClick={() => setTaskKind(k)}
                style={taskKindPillStyle(taskKind === k)}
                // From-WI flow locks the kind to the WI's track so the
                // operator can't accidentally cross-ladder a phase row
                // into the dev pipeline.
                disabled={busy}
                title={fromWorkItem && k !== taskKind ? `${fromWorkItem.id} is on the ${fromWorkItem.track} ladder — override if intentional` : undefined}
              >
                {k === 'none' ? 'Simple card' : k === 'plan' ? 'Plan task' : 'Coding task'}
              </button>
            ))}
          </div>
        )}

        <div style={{ display: 'grid', gap: 10, marginBottom: 18 }}>
          {!initial && (
            <Field label={isTask ? (fromWorkItem ? 'Work item id (locked)' : 'Work item id (optional)') : 'ID (optional)'}>
              <input
                value={id}
                onChange={(e) => setId(e.target.value)}
                placeholder="auto"
                readOnly={!!fromWorkItem}
                style={fromWorkItem ? { opacity: 0.7, cursor: 'not-allowed' } : undefined}
              />
            </Field>
          )}
          <Field label="Title">
            <input value={title} onChange={(e) => setTitle(e.target.value)} autoFocus />
          </Field>

          {isTask && (
            <Field label="Prompt — what the agent should do">
              <textarea
                value={prompt}
                onChange={(e) => setPrompt(e.target.value)}
                rows={4}
                placeholder={taskKind === 'plan'
                  ? 'Describe the feature to plan. The architect crew will produce a spec + WIT decomposition.'
                  : 'Describe the change. The engineer crew will read the repo, write the diff, and open a PR.'}
                style={{ resize: 'vertical', width: '100%' }}
              />
            </Field>
          )}

          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
            <Field label={isTask ? 'Story (parent)' : 'Epic'}>
              {creatingStory ? (
                <div style={{ display: 'flex', gap: 4 }}>
                  <input
                    value={newStoryName}
                    onChange={(e) => setNewStoryName(e.target.value)}
                    placeholder="New story name"
                    autoFocus
                    style={{ flex: 1 }}
                  />
                  <button
                    type="button"
                    className="btn btn-primary"
                    style={{ padding: '4px 8px', fontSize: 11 }}
                    disabled={!newStoryName.trim()}
                    onClick={async () => {
                      if (!activeProductId) return;
                      try {
                        const story = await forgeApi.createProductStory(activeProductId, { name: newStoryName.trim() });
                        setProductStories((prev) => [...prev, { id: story.id, name: story.name }]);
                        setEpic(story.id);
                      } catch {
                        // Fallback: create local-only entry.
                        const fid = newStoryName.trim().toUpperCase().replace(/[^A-Z0-9]+/g, '-').slice(0, 20);
                        setProductStories((prev) => [...prev, { id: fid, name: newStoryName.trim() }]);
                        setEpic(fid);
                      }
                      setCreatingStory(false);
                      setNewStoryName('');
                    }}
                  >
                    Add
                  </button>
                  <button
                    type="button"
                    className="btn btn-secondary"
                    style={{ padding: '4px 8px', fontSize: 11 }}
                    onClick={() => { setCreatingStory(false); setNewStoryName(''); }}
                  >
                    ✕
                  </button>
                </div>
              ) : (
                <div style={{ display: 'flex', gap: 4 }}>
                  <select value={epic} onChange={(e) => setEpic(e.target.value)} style={{ flex: 1 }}>
                    {epics.map((ep) => <option key={ep.id} value={ep.id}>{ep.name ?? ep.id}</option>)}
                  </select>
                  <button
                    type="button"
                    className="btn btn-secondary"
                    style={{ padding: '4px 8px', fontSize: 11, whiteSpace: 'nowrap' }}
                    onClick={() => setCreatingStory(true)}
                    title="Create a new story to associate with"
                  >
                    + New
                  </button>
                </div>
              )}
            </Field>
            {!isTask && (
              <Field label="Column">
                <select value={col} onChange={(e) => setCol(e.target.value as ColumnId)}>
                  {(['backlog','design','build','review','done'] as ColumnId[]).map((c) => <option key={c} value={c}>{c}</option>)}
                </select>
              </Field>
            )}
            {!isTask && (
              <Field label="Type">
                <select value={type} onChange={(e) => setType(e.target.value as CardType)}>
                  {TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
                </select>
              </Field>
            )}
            {taskKind === 'coding' && (
              <Field label="Type">
                <select value={codingType} onChange={(e) => setCodingType(e.target.value as typeof codingType)}>
                  {CODING_TYPES.map((t) => <option key={t.key} value={t.key}>{t.label}</option>)}
                </select>
              </Field>
            )}
            <Field label="Priority">
              <select value={priority} onChange={(e) => setPriority(e.target.value as Priority)}>
                {PRIORITIES.map((p) => <option key={p} value={p}>{p}</option>)}
              </select>
            </Field>
            <Field label="Points">
              <input type="number" min={0} max={21} value={pts} onChange={(e) => setPts(Number(e.target.value) || 0)} />
            </Field>
            <Field label="Tags (comma)">
              <input value={tags} onChange={(e) => setTags(e.target.value)} placeholder="auth, infra" />
            </Field>
          </div>

          {taskKind === 'plan' && (
            <Field label="Decomposition shape (multi-select)">
              <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap' }}>
                {PLAN_SUBKINDS.map((s) => (
                  <button
                    key={s.key}
                    type="button"
                    onClick={() => togglePlanSubkind(s.key)}
                    style={chipStyle(planSubkinds.includes(s.key))}
                  >
                    {s.label}
                  </button>
                ))}
              </div>
            </Field>
          )}

          {isTask && (
            <Field label={`Crew (default ${taskKind === 'plan' ? 'architecture' : 'engineering'})`}>
              <CrewPicker crews={crews} value={crewId} onChange={setCrewId} />
            </Field>
          )}

          {!isTask && !initial && (
            <>
              <Field label="Post to provider (Phase 6.1)">
                <select value={postTo} onChange={(e) => setPostTo(e.target.value as '' | 'ado' | 'github')}>
                  <option value="">Local only</option>
                  {productKinds.ado    && <option value="ado">Azure DevOps</option>}
                  {productKinds.github && <option value="github">GitHub</option>}
                </select>
              </Field>
              {postTo !== '' && (
                <Field label="Description (markdown)">
                  <textarea
                    value={description}
                    onChange={(e) => setDescription(e.target.value)}
                    rows={4}
                    placeholder="Optional body for the new work item / issue."
                    style={{ resize: 'vertical', width: '100%' }}
                  />
                </Field>
              )}
            </>
          )}

          {isTask && (
            <div style={{ fontSize: 11, color: 'var(--text-tertiary)', lineHeight: 1.5 }}>
              On submit: the work item is created on the {taskKind === 'plan' ? 'design' : 'dev'} track,
              the chosen crew's lead is assigned as generator, and the {taskKind} runner is kicked off.
              The runner is currently a Phase 13.4/13.5 stub (501) — once that wires up the agent will
              start writing immediately; today the task lands in the lead's queue.
            </div>
          )}
        </div>

        <div className="modal-actions">
          <button className="btn btn-secondary" onClick={onClose} disabled={busy}>Cancel</button>
          <button
            className="btn btn-primary"
            onClick={submit}
            disabled={busy || !title || (isTask && (!prompt || !crewId))}
          >
            {busy
              ? 'Saving…'
              : initial
                ? 'Save'
                : isTask
                  ? `Create & dispatch ${taskKind}`
                  : 'Create'}
          </button>
        </div>
      </div>
    </>
  );
}

function taskKindPillStyle(active: boolean): React.CSSProperties {
  return {
    flex: 1,
    padding: '6px 10px',
    borderRadius: 'var(--radius-sm)',
    border: active ? '1px solid var(--accent)' : '1px solid var(--border)',
    background: active ? 'color-mix(in srgb, var(--accent) 14%, transparent)' : 'transparent',
    color: active ? 'var(--accent)' : 'var(--text-secondary)',
    cursor: 'pointer',
    fontSize: 12,
    fontWeight: active ? 600 : 400,
  };
}

function chipStyle(active: boolean): React.CSSProperties {
  return {
    padding: '4px 10px',
    borderRadius: 999,
    border: `1px solid ${active ? 'var(--accent)' : 'var(--border)'}`,
    background: active ? 'color-mix(in srgb, var(--accent) 14%, transparent)' : 'transparent',
    color: active ? 'var(--accent)' : 'var(--text-secondary)',
    cursor: 'pointer',
    fontSize: 11,
    fontFamily: 'var(--font-mono, monospace)',
  };
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label style={{ display: 'grid', gap: 4, fontSize: 11, color: 'var(--text-secondary)' }}>
      <span>{label}</span>
      <span style={{
        display: 'block', padding: '6px 10px', borderRadius: 'var(--radius-sm)',
        border: '1px solid var(--border-strong)', background: 'var(--bg-surface)',
      }}>
        {children}
      </span>
    </label>
  );
}

/** Lead avatar — circular img if a portrait exists, else a colored initial. */
function LeadAvatar({ id, size = 22 }: { id: string | null | undefined; size?: number }) {
  if (!id) {
    return (
      <span
        title="(no lead)"
        style={{
          width: size, height: size, borderRadius: 999,
          background: 'var(--bg-sunken, #2a2a2a)', display: 'inline-block',
        }}
      />
    );
  }
  const url = avatarFor(id, null);
  const tip = agentTooltip(id, null, null);
  if (url) {
    return (
      <img
        src={url}
        alt={id}
        title={tip}
        style={{ width: size, height: size, borderRadius: 999, objectFit: 'cover' }}
      />
    );
  }
  return (
    <span
      title={tip}
      style={{
        width: size, height: size, borderRadius: 999, display: 'inline-flex',
        alignItems: 'center', justifyContent: 'center',
        background: 'var(--bg-sunken, #2a2a2a)', color: 'var(--text-secondary)',
        fontFamily: 'var(--font-mono, monospace)', fontSize: size * 0.42,
      }}
    >
      {id.slice(0, 2)}
    </span>
  );
}

/**
 * Phase-14 crew picker — replaces the native <select> so each row can show
 * the crew lead's avatar alongside the crew name. Click outside / Escape
 * closes the popover, same pattern as the other Forge picker popovers.
 */
function CrewPicker({ crews, value, onChange }: {
  crews: Crew[];
  value: string;
  onChange: (id: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const popoverRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    function onDown(e: MouseEvent) {
      const t = e.target as Node;
      if (triggerRef.current?.contains(t)) return;
      if (popoverRef.current?.contains(t)) return;
      setOpen(false);
    }
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape') setOpen(false); }
    window.addEventListener('mousedown', onDown);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('mousedown', onDown);
      window.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const selected = crews.find((c) => c.id === value) ?? null;

  return (
    <div style={{ position: 'relative' }}>
      <button
        ref={triggerRef}
        type="button"
        onClick={() => setOpen((v) => !v)}
        style={{
          width: '100%', display: 'flex', alignItems: 'center', gap: 8,
          padding: '4px 8px', border: 'none', background: 'transparent',
          color: 'inherit', cursor: 'pointer', textAlign: 'left',
        }}
      >
        {selected ? (
          <>
            <LeadAvatar id={selected.lead} size={22} />
            <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontSize: 12 }}>
              <span style={{ fontWeight: 600 }}>{selected.name}</span>
              {selected.lead && (
                <span style={{ color: 'var(--text-tertiary)', marginLeft: 6, fontFamily: 'var(--font-mono, monospace)', fontSize: 10.5 }}>
                  lead {selected.lead}
                </span>
              )}
            </span>
          </>
        ) : (
          <span style={{ flex: 1, color: 'var(--text-tertiary)', fontStyle: 'italic', fontSize: 12 }}>— pick a crew —</span>
        )}
        <span aria-hidden style={{ color: 'var(--text-tertiary)', fontSize: 10 }}>▾</span>
      </button>
      {open && (
        <div
          ref={popoverRef}
          role="listbox"
          style={{
            position: 'absolute', top: 'calc(100% + 4px)', left: 0, right: 0, zIndex: 30,
            maxHeight: 280, overflow: 'auto',
            background: 'var(--bg-elevated, #1c1c1c)',
            border: '1px solid var(--border)', borderRadius: 6,
            boxShadow: '0 6px 20px rgba(0,0,0,0.35)', padding: 4,
          }}
        >
          {crews.length === 0 ? (
            <div style={{ padding: 12, fontSize: 11, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>Loading crews…</div>
          ) : crews.map((c) => (
            <button
              key={c.id}
              type="button"
              onClick={() => { onChange(c.id); setOpen(false); }}
              style={{
                display: 'flex', alignItems: 'center', gap: 8,
                width: '100%', padding: '6px 8px',
                background: c.id === value ? 'color-mix(in srgb, var(--accent) 14%, transparent)' : 'transparent',
                border: 'none', color: 'inherit', cursor: 'pointer', textAlign: 'left',
                borderRadius: 4,
              }}
            >
              <LeadAvatar id={c.lead} size={26} />
              <span style={{ flex: 1, minWidth: 0 }}>
                <div style={{ fontWeight: 600, fontSize: 12.5 }}>{c.name}</div>
                <div style={{ fontSize: 10.5, color: 'var(--text-tertiary)' }}>
                  {c.lead ? `lead ${c.lead}` : 'no lead'}
                  {c.summary ? ` · ${c.summary}` : ''}
                </div>
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
