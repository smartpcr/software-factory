import { create } from 'zustand';

/** Visual treatment for the confirm dialog — drives the icon + accent color. */
export type ConfirmTone = 'danger' | 'warn' | 'info';

export interface ConfirmOptions {
  /** Big tracked-out display-font heading. Keep short (≤ 60 chars). */
  title: string;
  /** Body copy. Plain string or React node. Multi-line strings are split on `\n` and rendered as paragraphs. */
  body?: string | React.ReactNode;
  /** Primary-button label. Defaults to "Confirm". */
  confirmLabel?: string;
  /** Secondary-button label. Defaults to "Cancel". */
  cancelLabel?: string;
  /** Visual treatment. Defaults to `warn` (amber). `danger` (red) for destructive ops. */
  tone?: ConfirmTone;
  /**
   * Optional bullet checklist rendered between body and buttons. Each entry
   * is one item. Useful for "this will remove X / Y / Z" lists where the
   * operator wants explicit accounting before clicking through.
   */
  checklist?: string[];
}

interface State {
  /** Set when a dialog is awaiting an answer; null otherwise. */
  pending: (ConfirmOptions & { resolve: (ok: boolean) => void }) | null;
  /** Open a styled confirm dialog. Returns a promise that resolves to the operator's answer. */
  ask: (opts: ConfirmOptions) => Promise<boolean>;
  /** Programmatic close — resolves to false. */
  cancel: () => void;
  /** Programmatic accept — resolves to true. */
  confirm: () => void;
}

/**
 * Zustand-backed promise-style confirm dialog. Replaces `window.confirm` so
 * the dialog matches the rest of the Forge UI (terminal aesthetic, mono
 * font, amber/red accents) and so we can render rich content (checklists,
 * keyboard hints, multi-line bodies). The `<ConfirmDialog />` component
 * mounted at the AppShell renders whatever is in `pending`.
 *
 * Usage:
 * ```ts
 * const ask = useConfirmStore.getState().ask;
 * if (await ask({ title: 'Restart workstream?', body: '...', tone: 'warn' })) {
 *   // ...
 * }
 * ```
 */
export const useConfirmStore = create<State>((set, get) => ({
  pending: null,
  ask: (opts) => new Promise<boolean>((resolve) => {
    set({ pending: { ...opts, resolve } });
  }),
  cancel: () => {
    const p = get().pending;
    if (p) p.resolve(false);
    set({ pending: null });
  },
  confirm: () => {
    const p = get().pending;
    if (p) p.resolve(true);
    set({ pending: null });
  },
}));
