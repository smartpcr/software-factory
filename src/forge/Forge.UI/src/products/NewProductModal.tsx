import { useEffect, useMemo, useRef, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import { ProductIcon } from '@/products/ProductIcon';
import type { Product } from '@/types';

interface Props {
  open: boolean;
  onClose: () => void;
  onCreated: (product: Product) => void;
}

// Phase 8.7 — modern product-create modal. Uses the established
// `.modal-backdrop.show` + `.modal.show` animation pattern so it sits
// alongside NewWorkstreamModal without introducing new tokens. Slug is
// derived live from the name and editable, so the operator sees the
// URL-safe key before they hit Create.
export function NewProductModal({ open, onClose, onCreated }: Props) {
  const [name, setName] = useState('');
  const [slug, setSlug] = useState('');
  const [slugDirty, setSlugDirty] = useState(false);
  const [description, setDescription] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const nameRef = useRef<HTMLInputElement | null>(null);

  // Reset on open and focus the first field.
  useEffect(() => {
    if (!open) return;
    setName('');
    setSlug('');
    setSlugDirty(false);
    setDescription('');
    setBusy(false);
    setErr(null);
    // RAF so the input exists when we focus.
    const t = window.requestAnimationFrame(() => nameRef.current?.focus());
    return () => window.cancelAnimationFrame(t);
  }, [open]);

  // Auto-derive the slug from the name until the user edits it explicitly.
  useEffect(() => {
    if (slugDirty) return;
    setSlug(slugify(name));
  }, [name, slugDirty]);

  // Live slug validation — only render the inline message once the user
  // has typed something. Hides the "Slug is required" noise on first
  // open while still catching real errors as the user types.
  const liveSlugError = useMemo(() => {
    if (!slug.trim()) return null;
    return validateSlug(slug.trim());
  }, [slug]);

  // Esc to close.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  async function submit() {
    const cleanName = name.trim();
    const cleanSlug = slug.trim();
    if (!cleanName) { setErr('Name is required'); return; }
    const slugError = validateSlug(cleanSlug);
    if (slugError) { setErr(slugError); return; }
    setBusy(true);
    setErr(null);
    try {
      const product = await forgeApi.createProduct({
        id: cleanSlug,
        name: cleanName,
        slug: cleanSlug,
        description: description.trim() || undefined,
      });
      useToastStore.getState().show(`Product "${product.name}" created`, 'success');
      onCreated(product);
      onClose();
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Create failed';
      setErr(humanizeError(msg));
    } finally {
      setBusy(false);
    }
  }

  function onKeyDownForm(e: React.KeyboardEvent) {
    if (e.key === 'Enter' && !e.shiftKey && !busy) {
      e.preventDefault();
      void submit();
    }
  }

  return (
    <>
      <div
        className={`modal-backdrop${open ? ' show' : ''}`}
        onClick={onClose}
        aria-hidden={!open}
      />
      <div
        className={`modal${open ? ' show' : ''}`}
        role="dialog"
        aria-modal="true"
        aria-labelledby="new-product-title"
        onKeyDown={onKeyDownForm}
        style={{ width: 480 }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 14, marginBottom: 14 }}>
          <PreviewIcon name={name} slug={slug} />
          <div style={{ minWidth: 0, flex: 1 }}>
            <h3 className="modal-title" id="new-product-title" style={{ margin: 0 }}>
              {name.trim() || 'New product'}
            </h3>
            <p className="modal-text" style={{ margin: '4px 0 0' }}>
              A product groups one or more repos. Forge scopes every view to the active product.
            </p>
          </div>
        </div>

        <div style={{ display: 'grid', gap: 12, marginBottom: 18 }}>
          <FieldGroup label="Name" hint="Display name shown in the TopBar switcher.">
            <input
              ref={nameRef}
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="Atlas"
              maxLength={64}
              spellCheck={false}
              disabled={busy}
              style={inputStyle}
            />
          </FieldGroup>

          <FieldGroup
            label="Slug"
            hint={slug ? `Used in API paths and DB file: /api/products/${slug || '…'} (\`${slug || '…'}.db\`)` : 'Used in API paths and as the SQLite file name.'}
          >
            <div style={{ position: 'relative' }}>
              <span
                aria-hidden
                style={{
                  position: 'absolute', left: 12, top: '50%',
                  transform: 'translateY(-50%)',
                  fontSize: 12, color: 'var(--text-tertiary)',
                  fontFamily: 'var(--font-mono, monospace)',
                  pointerEvents: 'none',
                }}
              >
                /
              </span>
              <input
                value={slug}
                onChange={(e) => { setSlug(e.target.value); setSlugDirty(true); }}
                placeholder="atlas"
                maxLength={100}
                spellCheck={false}
                disabled={busy}
                aria-invalid={liveSlugError !== null}
                style={{
                  ...inputStyle,
                  paddingLeft: 22,
                  fontFamily: 'var(--font-mono, monospace)',
                  borderColor: liveSlugError ? 'var(--danger, #dc2626)' : (inputStyle.border as string | undefined)?.toString().includes('border-strong') ? undefined : undefined,
                }}
              />
            </div>
            {liveSlugError && (
              <span style={{ fontSize: 11, color: 'var(--danger, #dc2626)', lineHeight: 1.4 }}>
                {liveSlugError}
              </span>
            )}
          </FieldGroup>

          <FieldGroup label="Description" hint="Optional. One short sentence shown on the products grid.">
            <textarea
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="What does this product do?"
              maxLength={240}
              disabled={busy}
              rows={2}
              style={{ ...inputStyle, resize: 'vertical', minHeight: 56 }}
            />
          </FieldGroup>

          {err && (
            <div role="alert" style={errStyle}>
              <span style={{ marginRight: 6 }}>⚠</span>
              {err}
            </div>
          )}
        </div>

        <div className="modal-actions">
          <button className="btn btn-secondary" onClick={onClose} disabled={busy}>
            Cancel
          </button>
          <button
            className="btn btn-primary"
            onClick={() => void submit()}
            disabled={busy || !name.trim() || !slug.trim() || liveSlugError !== null}
          >
            {busy ? 'Creating…' : 'Create product'}
          </button>
        </div>
      </div>
    </>
  );
}

function PreviewIcon({ name, slug }: { name: string; slug: string }) {
  // Live preview of the auto-generated icon. Falls back to a placeholder
  // until the user types something so the modal doesn't render an empty
  // monogram on first open.
  const product = useMemo(
    () => ({
      id: slug || 'new-product',
      slug: slug || 'new-product',
      name: name.trim() || 'New Product',
    }),
    [name, slug],
  );
  return <ProductIcon product={product} size={48} />;
}

function FieldGroup({
  label, hint, children,
}: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <label style={{ display: 'grid', gap: 5 }}>
      <span
        style={{
          fontSize: 11,
          fontWeight: 600,
          letterSpacing: 0.2,
          textTransform: 'uppercase',
          color: 'var(--text-secondary)',
        }}
      >
        {label}
      </span>
      {children}
      {hint && (
        <span style={{ fontSize: 11, color: 'var(--text-tertiary)', lineHeight: 1.4 }}>
          {hint}
        </span>
      )}
    </label>
  );
}

const inputStyle: React.CSSProperties = {
  width: '100%',
  padding: '8px 11px',
  fontSize: 13,
  fontFamily: 'inherit',
  color: 'var(--text-primary)',
  background: 'var(--bg-surface)',
  border: '1px solid var(--border-strong)',
  borderRadius: 'var(--radius-sm)',
  outline: 'none',
  transition: 'border-color 120ms ease, box-shadow 120ms ease',
};

const errStyle: React.CSSProperties = {
  fontSize: 12,
  color: 'var(--danger)',
  background: 'var(--danger-bg, rgba(220, 38, 38, 0.08))',
  border: '1px solid var(--danger-border, rgba(220, 38, 38, 0.25))',
  borderRadius: 'var(--radius-sm)',
  padding: '7px 10px',
};

function slugify(s: string): string {
  return s
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

// Mirrors `ForgeDbPaths.ReservedSlugs` in Forge.Api so the operator sees
// the rejection before submit. Includes the control DB collision
// (`forge`) and Windows reserved device names (SQLite cannot open
// `con.db`, `nul.db`, etc. on Windows).
const RESERVED_SLUGS = new Set<string>([
  'forge',
  'con', 'nul', 'aux', 'prn',
  'com1', 'com2', 'com3', 'com4', 'com5', 'com6', 'com7', 'com8', 'com9',
  'lpt1', 'lpt2', 'lpt3', 'lpt4', 'lpt5', 'lpt6', 'lpt7', 'lpt8', 'lpt9',
]);

function validateSlug(slug: string): string | null {
  if (!slug) return 'Slug is required.';
  if (slug.length > 100) return `Slug is ${slug.length} characters; the maximum is 100.`;
  if (!/^[a-z0-9-]+$/.test(slug)) {
    return 'Slug can only contain lowercase letters, digits, and hyphens.';
  }
  if (slug.startsWith('-') || slug.endsWith('-')) {
    return 'Slug cannot start or end with a hyphen.';
  }
  if (slug.includes('--')) {
    return 'Slug cannot contain consecutive hyphens.';
  }
  if (RESERVED_SLUGS.has(slug)) {
    return `Slug "${slug}" is reserved. Each product becomes a SQLite file at \`{folder}/${slug}.db\`, and this name would collide with the control DB or a Windows device name.`;
  }
  return null;
}

function humanizeError(msg: string): string {
  // The fetch wrapper formats errors as "/api/products: 409 …".
  // Strip the path / status prefix when present.
  const m = msg.match(/^.*?:\s*\d+\s*(.*)$/);
  return (m?.[1] ?? msg).trim();
}
