import { usePresenterStore } from "../store/presenterStore";
export function UsagePill() {
  const usage = usePresenterStore((s) => s.usage);
  return (
    <span className="rounded-full bg-gray-200 px-3 py-1 text-xs text-gray-900 dark:bg-gray-800 dark:text-gray-100">
      {usage ?? 0} s
    </span>
  );
}
