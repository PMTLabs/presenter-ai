import { usePresenterStore } from "../store/presenterStore";
import { useExchanges } from "../store/exchanges";
import { useStickToBottom } from "./useStickToBottom";

export type TranscriptProps = {
  onTrainOnThis?: (question: string, answer: string, slideIndex: number) => void;
};

export function Transcript({ onTrainOnThis }: TranscriptProps = {}) {
  const turns = usePresenterStore((s) => s.transcript);
  const trainerMode = usePresenterStore((s) => s.trainerMode);
  const exchanges = useExchanges();
  const { ref, onScroll } = useStickToBottom<HTMLElement>(turns);
  return (
    <section ref={ref} onScroll={onScroll} className="min-h-0 flex-1 overflow-auto rounded-xl border border-gray-200 bg-white p-3 text-gray-900 dark:border-gray-800 dark:bg-gray-900 dark:text-gray-100">
      <h2 className="mb-2 text-sm font-semibold">Transcript</h2>
      {turns.map((turn, i) => {
        const exchange = exchanges[i];
        return (
          <div key={i} className="mb-2 text-sm">
            <b>{turn.role === "user" ? "You" : "Presenter"}: </b>
            {turn.text}
            {trainerMode && turn.role !== "user" && (
              <button
                type="button"
                className="ml-2 text-xs font-medium text-blue-600 hover:underline disabled:cursor-not-allowed disabled:text-gray-400 disabled:no-underline dark:text-blue-400 dark:disabled:text-gray-600"
                disabled={!exchange}
                title={exchange ? undefined : "No question before this answer"}
                onClick={() => exchange && onTrainOnThis?.(exchange.question, exchange.answer, exchange.slideIndex)}
              >
                Train on this
              </button>
            )}
          </div>
        );
      })}
    </section>
  );
}
