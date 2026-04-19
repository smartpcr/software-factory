import { useEffect } from 'react';

export interface KeyboardShortcutHandlers {
  onSave?: () => void;
  onNew?: () => void;
  onDelete?: () => void;
  onEscape?: () => void;
  onZoomIn?: () => void;
  onZoomOut?: () => void;
  onZoomReset?: () => void;
  onFitView?: () => void;
}

export function useKeyboardShortcuts(handlers: KeyboardShortcutHandlers) {
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement;
      if (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT') {
        return;
      }

      if ((e.ctrlKey || e.metaKey) && e.key === 's') {
        e.preventDefault();
        handlers.onSave?.();
      }
      if ((e.ctrlKey || e.metaKey) && e.key === 'n') {
        e.preventDefault();
        handlers.onNew?.();
      }
      if (e.key === 'Delete' || e.key === 'Backspace') {
        handlers.onDelete?.();
      }
      if (e.key === 'Escape') {
        handlers.onEscape?.();
      }
      if (e.key === '=' || e.key === '+') {
        handlers.onZoomIn?.();
      }
      if (e.key === '-') {
        handlers.onZoomOut?.();
      }
      if (e.key === '0') {
        handlers.onZoomReset?.();
      }
      if (e.key === 'f' || e.key === 'F') {
        handlers.onFitView?.();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [handlers]);
}
