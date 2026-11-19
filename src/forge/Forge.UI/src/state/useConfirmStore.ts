import { create } from 'zustand';

/** Visual treatment for the confirm dialog — drives the icon + accent color. */
export type ConfirmTone = 'danger' | 'warn' | 'info';

/** Text field shown by {@link State.askText} (replaces `window.prompt`). */
export interface TextInputOptions {
  /** Small uppercase label above the field. */
  label?: string;
  placeholder?: string;
  initialValue?: string;
  /** Rows for a multi-line field; 1 (default) renders a single-line input. */
  rows?: number;
  /** When true (default) the confirm button is disabled while the trimmed value is empty. */
  required?: boolean;
  maxLength?: number;
}

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
  /** Set by {@link State.askText}; renders a themed text field. */
  input?: TextInputOptions;
}

interface State {
  /** Set when a dialog is awaiting an answer; null otherwise. */
  pending: (ConfirmOptions & { resolve: (ok: boolean, value?: string) => void }) | null;
  /** Open a styled confirm dialog. Returns a promise that resolves to the operator's answer. */
  ask: (opts: ConfirmOptions) => Promise<boolean>;
  /**
   * Open the same styled dialog with a text field. Resolves to the entered
   * text (untrimmed), or null when cancelled. Use instead of `window.prompt`,
   * which renders an unthemed native browser dialog.
   */
  askText: (opts: ConfirmOptions & { input?: TextInputOptions }) => Promise<string | null>;
  /** Programmatic close — resolves to false / null. */
  cancel: () => void;
  /** Programmatic accept — resolves to true, or to `value` for askText. */
  confirm: (value?: string) => void;
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
    set({ pending: { ...opts, input: undefined, resolve: (ok) => resolve(ok) } });
  }),
  askText: (opts) => new Promise<string | null>((resolve) => {
    set({
      pending: {
        ...opts,
        input: opts.input ?? {},
        resolve: (ok, value) => resolve(ok ? (value ?? '') : null),
      },
    });
  }),
  cancel: () => {
    const p = get().pending;
    if (p) p.resolve(false);
    set({ pending: null });
  },
  confirm: (value) => {
    const p = get().pending;
    if (p) p.resolve(true, value);
    set({ pending: null });
  },
}));
