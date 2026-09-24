import { useToastStore, type ToastKind } from "../store/toastStore";

const KIND_CLASS: Record<ToastKind, string> = {
  info: "border-gray-200 bg-white text-gray-900 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100",
  success: "border-green-200 bg-green-50 text-green-900 dark:border-green-800 dark:bg-green-950 dark:text-green-100",
  warning: "border-amber-200 bg-amber-50 text-amber-900 dark:border-amber-800 dark:bg-amber-950 dark:text-amber-100",
  error: "border-red-200 bg-red-50 text-red-900 dark:border-red-800 dark:bg-red-950 dark:text-red-100",
};

const KIND_ICON: Record<ToastKind, string> = { info: "ℹ", success: "✓", warning: "!", error: "✕" };

/** Top-center toast stack (at most three). Errors are alerts; everything else is a polite status. */
export function Toaster() {
  const toasts = useToastStore((state) => state.toasts);
  const dismiss = useToastStore((state) => state.dismiss);
  return (
    <section
      aria-label="Notifications"
      className="pointer-events-none fixed inset-x-0 top-4 z-50 flex flex-col items-center gap-2 px-4"
    >
      {toasts.map((toast) => (
        <div
          key={toast.id}
          role={toast.kind === "error" ? "alert" : "status"}
          aria-live={toast.kind === "error" ? "assertive" : "polite"}
          data-kind={toast.kind}
          className={[
            "pointer-events-auto flex w-full max-w-md items-start gap-3 rounded-lg border px-4 py-3 text-sm shadow-lg",
            "motion-safe:animate-[toast-in_150ms_ease-out]",
            KIND_CLASS[toast.kind],
          ].join(" ")}
        >
          <span aria-hidden="true" className="mt-px w-4 flex-none text-center font-bold">
            {KIND_ICON[toast.kind]}
          </span>
          <p className="min-w-0 flex-1 break-words">{toast.message}</p>
          <button
            type="button"
            aria-label="Dismiss notification"
            onClick={() => dismiss(toast.id)}
            className="-my-1 -mr-2 flex-none rounded p-1 leading-none opacity-70 hover:opacity-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
          >
            ×
          </button>
        </div>
      ))}
    </section>
  );
}
