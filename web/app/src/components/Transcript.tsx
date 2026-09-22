import { usePresenterStore } from "../store/presenterStore";
export function Transcript() {
  const turns = usePresenterStore((s) => s.transcript);
  return (
    <section className="min-h-48 overflow-auto rounded-xl border border-gray-200 bg-white p-3 text-gray-900 dark:border-gray-800 dark:bg-gray-900 dark:text-gray-100">
      <h2 className="mb-2 text-sm font-semibold">Transcript</h2>
      {turns.map((turn, i) => (
        <div key={i} className="mb-2 text-sm">
          <b>{turn.role === "user" ? "You" : "Presenter"}: </b>
          {turn.text}
        </div>
      ))}
    </section>
  );
}
