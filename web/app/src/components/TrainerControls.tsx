import { usePresenterStore } from "../store/presenterStore";

const EDIT_ERROR_TEXT: Record<string, string> = {
  timeout: "timed out",
  upstream: "upstream error",
  invalid_output: "invalid output",
  conflict: "a version conflict",
  cancelled: "cancelled",
  queue_full: "the edit queue is full",
  trainer_mode_off: "trainer mode is off",
  not_presenting: "not presenting",
};

function formatEditError(error: string | null): string {
  if (!error) return "unknown error";
  return EDIT_ERROR_TEXT[error] ?? error;
}

export type TrainerControlsProps = {
  onToggle: () => void;
};

/** Trainer toggle, voice-training-unavailable notice, and the queued/processing/applied/failed script-edit chip. */
export function TrainerControls({ onToggle }: TrainerControlsProps) {
  const trainerAvailable = usePresenterStore((state) => state.trainerAvailable);
  const trainerMode = usePresenterStore((state) => state.trainerMode);
  const voiceTraining = usePresenterStore((state) => state.voiceTraining);
  const edits = usePresenterStore((state) => state.edits);
  const currentEditId = usePresenterStore((state) => state.currentEditId);
  const currentEdit = currentEditId ? edits[currentEditId] : undefined;

  if (!trainerAvailable) return null;

  const chip =
    currentEdit?.status === "queued" || currentEdit?.status === "processing"
      ? { text: "Updating…", className: "bg-blue-100 text-blue-900 dark:bg-blue-900 dark:text-blue-100" }
      : currentEdit?.status === "applied"
        ? {
            text: `Updated — v${currentEdit.version}: ${currentEdit.summary}`,
            className: "bg-green-100 text-green-900 dark:bg-green-900 dark:text-green-100",
          }
        : currentEdit?.status === "failed"
          ? {
              text: `Couldn't update — ${formatEditError(currentEdit.error)}`,
              className: "bg-red-100 text-red-900 dark:bg-red-900 dark:text-red-100",
            }
          : null;

  return (
    <>
      <label className="flex items-center gap-1.5 rounded-full bg-gray-200 px-3 py-1 text-xs font-medium text-gray-900 dark:bg-gray-800 dark:text-gray-100">
        <input type="checkbox" checked={trainerMode} onChange={onToggle} />
        <span>Trainer mode</span>
      </label>
      {!voiceTraining && (
        <span className="rounded-full bg-amber-100 px-3 py-1 text-xs text-amber-900 dark:bg-amber-900 dark:text-amber-100">
          Voice training is unavailable on this connection — use Train on this
        </span>
      )}
      {chip && (
        <span role="status" className={`rounded-full px-3 py-1 text-xs ${chip.className}`}>
          {chip.text}
        </span>
      )}
    </>
  );
}
