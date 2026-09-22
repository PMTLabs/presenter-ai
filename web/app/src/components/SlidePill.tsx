import { usePresenterStore } from "../store/presenterStore";
export function SlidePill() {
  const slide = usePresenterStore((state) => state.slide);
  const slideCount = usePresenterStore((state) => state.snapshot.slideCount);
  return (
    <span className="rounded-full bg-gray-200 px-3 py-1 text-xs text-gray-900 dark:bg-gray-800 dark:text-gray-100">
      slide {slide + 1}/{slideCount || "–"}
    </span>
  );
}
