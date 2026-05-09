import { create } from 'zustand';

export type ToastKind = 'success' | 'danger' | 'info';

export interface Toast {
  id: number;
  msg: string;
  kind: ToastKind;
}

interface ToastState {
  toasts: Toast[];
  show: (msg: string, kind?: ToastKind) => void;
  dismiss: (id: number) => void;
}

let nextId = 1;
const TTL_MS = 5000;
const ERROR_TTL_MS = 30000;

export const useToastStore = create<ToastState>((set, get) => ({
  toasts: [],
  show: (msg, kind = 'success') => {
    const id = nextId++;
    set({ toasts: [...get().toasts, { id, msg, kind }] });
    const ttl = kind === 'danger' ? ERROR_TTL_MS : TTL_MS;
    window.setTimeout(() => get().dismiss(id), ttl);
  },
  dismiss: (id) => set({ toasts: get().toasts.filter((t) => t.id !== id) }),
}));
