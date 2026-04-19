import { create } from 'zustand';

export type ToastKind = 'success' | 'error' | 'info';

export interface ToastEntry {
  id: string;
  message: string;
  kind: ToastKind;
  duration: number;
}

interface ToastState {
  toasts: ToastEntry[];
  addToast: (message: string, kind?: ToastKind, duration?: number) => void;
  removeToast: (id: string) => void;
}

let counter = 0;

export const useToastStore = create<ToastState>()((set) => ({
  toasts: [],
  addToast: (message, kind = 'info', duration = 2800) => {
    const id = `toast_${++counter}`;
    set((state) => ({
      toasts: [...state.toasts, { id, message, kind, duration }],
    }));
    setTimeout(() => {
      set((state) => ({
        toasts: state.toasts.filter((t) => t.id !== id),
      }));
    }, duration);
  },
  removeToast: (id) =>
    set((state) => ({
      toasts: state.toasts.filter((t) => t.id !== id),
    })),
}));

export function toast(message: string, kind: ToastKind = 'info', duration = 2800) {
  useToastStore.getState().addToast(message, kind, duration);
}
