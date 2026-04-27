import { useEffect, useRef, useState } from 'react';
import { useProductsStore } from '@/state/useProductsStore';
import { useToastStore } from '@/state/useToastStore';
import { ProductIcon } from '@/products/ProductIcon';
import type { Product } from '@/types';

interface Props {
  open: boolean;
  product: Product;
  onClose: () => void;
}

// Edit product modal — mirrors NewProductModal layout so the UX is consistent.
// Slug is read-only (it's the product id and can't be changed after creation).
export function EditProductModal({ open, product, onClose }: Props) {
  const [name, setName] = useState(product.name);
  const [description, setDescription] = useState(product.description ?? '');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const nameRef = useRef<HTMLInputElement | null>(null);
  const patchProduct = useProductsStore((s) => s.patchProduct);

  // Reset form when opened or product changes.
  useEffect(() => {
    if (!open) return;
    setName(product.name);
    setDescription(product.description ?? '');
    setBusy(false);
    setErr(null);
    const t = window.requestAnimationFrame(() => nameRef.current?.focus());
    return () => window.cancelAnimationFrame(t);
  }, [open, product]);

  // Esc to close.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  async function submit() {
    const cleanName = name.trim();
    if (!cleanName) { setErr('Name is required'); return; }
    setBusy(true);
    setErr(null);
    try {
      await patchProduct(product.id, {
        name: cleanName,
        description: description.trim(),
      });
      useToastStore.getState().show(`Updated "${cleanName}"`, 'success');
      onClose();
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Update failed';
      setErr(msg);
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
        aria-labelledby="edit-product-title"
        onKeyDown={onKeyDownForm}
        style={{ width: 480 }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 14, marginBottom: 14 }}>
          <ProductIcon product={{ ...product, name: name.trim() || product.name }} size={48} />
          <div style={{ minWidth: 0, flex: 1 }}>
            <h3 className="modal-title" id="edit-product-title" style={{ margin: 0 }}>
              Edit product
            </h3>
            <p className="modal-text" style={{ margin: '4px 0 0' }}>
              Update the product details. Slug cannot be changed after creation.
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

          <FieldGroup label="Slug" hint={`Used in API paths: /api/products/${product.slug}`}>
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
                value={product.slug}
                disabled
                style={{
                  ...inputStyle,
                  paddingLeft: 22,
                  fontFamily: 'var(--font-mono, monospace)',
                  opacity: 0.5,
                  cursor: 'not-allowed',
                }}
              />
            </div>
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
            disabled={busy || !name.trim()}
          >
            {busy ? 'Saving…' : 'Save changes'}
          </button>
        </div>
      </div>
    </>
  );
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
