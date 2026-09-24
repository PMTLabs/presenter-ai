import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState, type ReactNode } from "react";

const TOOLTIP_WIDTH = 288;
const VIEWPORT_MARGIN = 8;
const HIDE_DELAY_MS = 150;

export type InfoTooltipProps = {
  /** Accessible name of the trigger, e.g. "About Trainer mode". */
  label: string;
  children: ReactNode;
};

/**
 * An info icon with a custom tooltip: opens on hover and on keyboard focus, stays open while the pointer is over it,
 * closes on Escape. It is fixed-positioned from the trigger's rectangle and clamped to the viewport, so no
 * `overflow-hidden` ancestor or screen edge clips it.
 */
export function InfoTooltip({ label, children }: InfoTooltipProps) {
  const id = useId();
  const trigger = useRef<HTMLButtonElement>(null);
  const hideTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [open, setOpen] = useState(false);
  const [position, setPosition] = useState<{ top: number; left: number; width: number } | null>(null);

  const cancelHide = useCallback(() => {
    if (hideTimer.current !== null) clearTimeout(hideTimer.current);
    hideTimer.current = null;
  }, []);
  const show = useCallback(() => {
    cancelHide();
    setOpen(true);
  }, [cancelHide]);
  const hideSoon = useCallback(() => {
    cancelHide();
    hideTimer.current = setTimeout(() => setOpen(false), HIDE_DELAY_MS);
  }, [cancelHide]);
  const hide = useCallback(() => {
    cancelHide();
    setOpen(false);
  }, [cancelHide]);

  const measure = useCallback(() => {
    const rect = trigger.current?.getBoundingClientRect();
    if (!rect) return;
    const viewport = window.innerWidth;
    const width = Math.min(TOOLTIP_WIDTH, viewport - 2 * VIEWPORT_MARGIN);
    // Prefer the trigger's right edge (the info bar sits on the right), then clamp inside the viewport.
    const left = Math.min(Math.max(rect.right - width, VIEWPORT_MARGIN), viewport - width - VIEWPORT_MARGIN);
    setPosition({ top: rect.bottom + 8, left, width });
  }, []);

  useLayoutEffect(() => {
    if (open) measure();
  }, [open, measure]);

  useEffect(() => {
    if (!open) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        // Escape closes the tooltip only; it must not also end the talk (the page binds Escape to End).
        event.stopPropagation();
        event.preventDefault();
        hide();
      }
    };
    window.addEventListener("keydown", onKey, true);
    window.addEventListener("resize", measure);
    window.addEventListener("scroll", measure, true);
    return () => {
      window.removeEventListener("keydown", onKey, true);
      window.removeEventListener("resize", measure);
      window.removeEventListener("scroll", measure, true);
    };
  }, [open, hide, measure]);

  useEffect(() => cancelHide, [cancelHide]);

  return (
    <span className="relative inline-flex" onMouseEnter={show} onMouseLeave={hideSoon}>
      <button
        ref={trigger}
        type="button"
        aria-label={label}
        aria-describedby={open ? id : undefined}
        aria-expanded={open}
        onFocus={show}
        onBlur={hide}
        onClick={show}
        className={[
          "inline-flex h-5 w-5 items-center justify-center rounded-full border border-gray-400 text-[11px] font-semibold",
          "leading-none text-gray-600 hover:border-blue-500 hover:text-blue-600",
          "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2",
          "dark:border-gray-500 dark:text-gray-300 dark:ring-offset-gray-950 dark:hover:border-blue-400 dark:hover:text-blue-300",
        ].join(" ")}
      >
        i
      </button>
      {open && (
        <div
          id={id}
          role="tooltip"
          style={position ? { top: position.top, left: position.left, width: position.width } : { visibility: "hidden" }}
          className={[
            "fixed z-50 rounded-lg border border-gray-200 bg-white p-3 text-left text-xs leading-relaxed text-gray-800 shadow-xl",
            "dark:border-gray-700 dark:bg-gray-900 dark:text-gray-100",
          ].join(" ")}
        >
          {children}
        </div>
      )}
    </span>
  );
}
