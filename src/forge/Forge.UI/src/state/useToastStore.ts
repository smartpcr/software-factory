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
const TTL_MS = 2400; // matches the prototype's setTimeout in showToast.

export const useToastStore = create<ToastState>((set, get) => ({
  toasts: [],
  show: (msg, kind = 'success') => {
    const id = nextId++;
    set({ toasts: [...get().toasts, { id, msg, kind }] });
    window.setTimeout(() => get().dismiss(id), TTL_MS);
  },
  dismiss: (id) => set({ toasts: get().toasts.filter((t) => t.id !== id) }),
}));
