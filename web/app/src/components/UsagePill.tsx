import { usePresenterStore } from "../store/presenterStore";
export function UsagePill() {
  const usage = usePresenterStore((s) => s.usage);
  return (
    <span className="rounded-full bg-gray-200 px-3 py-1 text-xs dark:bg-gray-800">
      {usage ?? 0} s
    </span>
  );
}
