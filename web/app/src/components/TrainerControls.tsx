import { usePresenterStore } from "../store/presenterStore";
import { InfoTooltip } from "./InfoTooltip";

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
      <span className="flex items-center gap-1.5 rounded-full bg-gray-200 py-0.5 pl-1 pr-2 text-xs font-medium text-gray-900 dark:bg-gray-800 dark:text-gray-100">
        <button
          type="button"
          role="switch"
          aria-checked={trainerMode}
          onClick={onToggle}
          className="group inline-flex items-center gap-2 rounded-full py-0.5 pl-0.5 pr-1 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 focus-visible:ring-offset-gray-200 dark:focus-visible:ring-offset-gray-800"
        >
          <span
            aria-hidden="true"
            className={[
              "relative inline-flex h-5 w-9 flex-none items-center rounded-full transition-colors duration-200 motion-reduce:transition-none",
              trainerMode
                ? "bg-blue-600 text-white dark:bg-blue-500 dark:text-white"
                : "bg-gray-400 text-gray-900 dark:bg-gray-600 dark:text-gray-100",
            ].join(" ")}
          >
            <span
              className={[
                "absolute left-0.5 h-4 w-4 rounded-full bg-white shadow transition-transform duration-200 ease-out motion-reduce:transition-none",
                trainerMode ? "translate-x-4" : "translate-x-0",
              ].join(" ")}
            />
          </span>
          <span>Trainer mode</span>
        </button>
        <InfoTooltip label="About Trainer mode">
          <p className="font-semibold">Trainer mode</p>
          <p className="mt-1">
            Change the script live by voice. This updates the presentation content — it does not retrain the AI.
          </p>
          <ul className="mt-2 list-disc space-y-1 pl-4">
            <li>
              Say what to add or change, e.g. “On this slide also mention …”, then answer “yes” / “có” when asked.
            </li>
            <li>Or press “Train on this” on an answer in the transcript.</li>
          </ul>
          <p className="mt-2">Each change creates a new script version you can review and revert in Script versions.</p>
        </InfoTooltip>
      </span>
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
