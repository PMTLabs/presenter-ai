import { useLayoutEffect, useRef } from "react";

/** Distance from the bottom, in pixels, that still counts as "reading the newest line". */
export const STICK_THRESHOLD_PX = 24;

/**
 * Keeps a scroll container pinned to its newest content as `content` changes, unless the reader has scrolled up;
 * scrolling back to the bottom re-pins it.
 */
export function useStickToBottom<T extends HTMLElement>(content: unknown) {
  const ref = useRef<T>(null);
  const pinned = useRef(true);
  useLayoutEffect(() => {
    const element = ref.current;
    if (element && pinned.current) element.scrollTop = element.scrollHeight;
  }, [content]);
  const onScroll = () => {
    const element = ref.current;
    if (!element) return;
    pinned.current = element.scrollHeight - element.scrollTop - element.clientHeight <= STICK_THRESHOLD_PX;
  };
  return { ref, onScroll };
}
