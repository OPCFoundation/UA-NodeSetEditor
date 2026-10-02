import * as React from 'react';

/**
 * Drag-to-resize width for a side panel. The width is clamped to [min, max] and
 * remembered in localStorage under `storageKey` so it survives reloads.
 */
export function useResizableWidth(storageKey: string, initial: number, min: number, max: number) {
   const clamp = React.useCallback((w: number) => Math.min(max, Math.max(min, w)), [min, max]);

   const [width, setWidth] = React.useState<number>(() => {
	  const stored = Number(localStorage.getItem(storageKey));
	  return Number.isFinite(stored) && stored > 0 ? Math.min(max, Math.max(min, stored)) : initial;
   });
   const [isResizing, setIsResizing] = React.useState(false);

   React.useEffect(() => {
	  localStorage.setItem(storageKey, String(width));
   }, [storageKey, width]);

   const startResize = React.useCallback((e: React.PointerEvent<HTMLElement>) => {
	  e.preventDefault();
	  const startX = e.clientX;
	  const startWidth = width;
	  setIsResizing(true);

	  const onMove = (ev: PointerEvent) => setWidth(clamp(startWidth + ev.clientX - startX));
	  const onUp = () => {
		 setIsResizing(false);
		 window.removeEventListener('pointermove', onMove);
		 window.removeEventListener('pointerup', onUp);
		 document.body.style.cursor = '';
		 document.body.style.userSelect = '';
	  };

	  document.body.style.cursor = 'col-resize';
	  document.body.style.userSelect = 'none';
	  window.addEventListener('pointermove', onMove);
	  window.addEventListener('pointerup', onUp);
   }, [width, clamp]);

   /** Arrow keys resize by 16px so the handle is keyboard accessible. */
   const onHandleKeyDown = React.useCallback((e: React.KeyboardEvent<HTMLElement>) => {
	  if (e.key === 'ArrowLeft') { e.preventDefault(); setWidth((w) => clamp(w - 16)); }
	  if (e.key === 'ArrowRight') { e.preventDefault(); setWidth((w) => clamp(w + 16)); }
   }, [clamp]);

   const reset = React.useCallback(() => setWidth(initial), [initial]);

   return { width, isResizing, startResize, onHandleKeyDown, reset };
}
