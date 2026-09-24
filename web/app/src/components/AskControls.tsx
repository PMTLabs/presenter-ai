import { usePresenterStore, type PresenterAskState } from "../store/presenterStore";

export type AskControlsProps = {
  onAsk?: () => void;
  onDone?: () => void;
  onExtend?: () => void;
  onCancel?: () => void;
  onContinue?: () => void;
  disabled?: boolean;
  ask?: PresenterAskState | null;
  muted?: boolean;
};

const actionButtonClassName = [
  "rounded-lg border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-900",
  "hover:bg-gray-100 disabled:cursor-not-allowed disabled:opacity-40",
  "dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100 dark:hover:bg-gray-700",
].join(" ");

function formatMinutesSeconds(ms: number): string {
  const totalSeconds = Math.max(0, Math.floor(ms / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes}:${seconds.toString().padStart(2, "0")}`;
}

export function AskControls({
  onAsk,
  onDone,
  onExtend,
  onCancel,
  onContinue,
  disabled,
  ask: propsAsk,
  muted: propsMuted,
}: AskControlsProps) {
  const storeAsk = usePresenterStore((state) => state.ask);
  const storeMuted = usePresenterStore((state) => state.snapshot.muted);

  const effectiveAsk = propsAsk !== undefined ? propsAsk : storeAsk;
  const isMuted = propsMuted !== undefined ? propsMuted : storeMuted;
  const isDisabled = (disabled ?? false) || isMuted;

  if (effectiveAsk?.state === "listening") {
    const showQuietCountdown =
      effectiveAsk.quietRemainingMs !== null &&
      effectiveAsk.quietRemainingMs !== undefined &&
      effectiveAsk.quietRemainingMs < 20000;

    return (
      <div className="flex items-center gap-2">
        <span role="status" className="text-sm font-medium text-blue-700 dark:text-blue-300">
          Listening… {formatMinutesSeconds(effectiveAsk.elapsedMs)}
        </span>
        {effectiveAsk.speechRemainingMs !== null && effectiveAsk.speechRemainingMs !== undefined && (
          <span className="text-xs text-gray-600 dark:text-gray-400">
            {formatMinutesSeconds(effectiveAsk.speechRemainingMs)} of speech left
          </span>
        )}
        {showQuietCountdown && (
          <span className="text-xs text-amber-700 dark:text-amber-300">
            {effectiveAsk.heard ? "Sending" : "Closing"} in {formatMinutesSeconds(effectiveAsk.quietRemainingMs!)} if quiet
          </span>
        )}
        <button
          type="button"
          className={actionButtonClassName}
          onClick={onDone}
          aria-keyshortcuts="A, Enter"
        >
          Ask done
        </button>
        <button
          type="button"
          className={actionButtonClassName}
          onClick={onExtend}
        >
          Extend
        </button>
        <button
          type="button"
          className={actionButtonClassName}
          onClick={onCancel}
        >
          Cancel
        </button>
      </div>
    );
  }

  if (effectiveAsk?.state === "answering") {
    return (
      <div className="flex items-center gap-2">
        <button
          type="button"
          className={actionButtonClassName}
          onClick={onContinue}
        >
          Continue
        </button>
        <span role="status" className="text-sm font-medium text-blue-700 dark:text-blue-300">
          Answering…
        </span>
      </div>
    );
  }

  return (
    <div className="flex items-center gap-2">
      <button
        type="button"
        className={actionButtonClassName}
        onClick={() => {
          if (!isDisabled && onAsk) onAsk();
        }}
        disabled={isDisabled}
        aria-keyshortcuts="A"
      >
        Ask
      </button>
      {isMuted && (
        <span className="text-xs text-gray-500 dark:text-gray-400">
          Unmute to ask
        </span>
      )}
    </div>
  );
}
