import { usePresenterStore } from "../store/presenterStore";
import { useStickToBottom } from "./useStickToBottom";
export function LogPanel() {
  const logs = usePresenterStore((s) => s.logs);
  const { ref, onScroll } = useStickToBottom<HTMLElement>(logs);
  return (
    <section ref={ref} onScroll={onScroll} className="min-h-0 flex-1 overflow-auto rounded-xl bg-gray-950 p-3 font-mono text-xs text-gray-200 dark:bg-gray-950 dark:text-gray-200">
      <h2 className="mb-2 font-sans font-semibold">Log</h2>
      {logs.map((line, i) => (
        <div
          key={i}
          className={
            line.level === "error"
              ? "text-red-300"
              : line.level === "warn"
                ? "text-yellow-300"
                : ""
          }
        >
          {line.time} {line.message}
        </div>
      ))}
    </section>
  );
}
